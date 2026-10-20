using System.Collections;

namespace LocalSeek.Core.Index.Segments;

/// <summary>
/// Streams several segments into one. Only documents live at merge start and contents owned by a live
/// document are copied; ids are renumbered in input order so postings stay sorted. Terms are merged
/// with a k-way merge over the inputs' sorted term dictionaries, so memory is bounded by one term.
/// </summary>
internal static class SegmentMerger
{
    public sealed record Input(SegmentReader Reader, BitArray LiveDocs, BitArray OwnedContents);

    /// <summary>Old id → new id per input (−1 when dropped).</summary>
    public sealed record Result(int[][] DocMap, int[][] ContentMap, int DocCount, int ContentCount);

    public static Result Merge(Storage.IIndexStorage storage, string fileName, long outputId, IReadOnlyList<Input> inputs, CancellationToken cancellation)
    {
        var inputIndexById = inputs.Select((input, i) => (input.Reader.SegmentId, i)).ToDictionary(x => x.SegmentId, x => x.i);
        var contentMap = new int[inputs.Count][];
        var docMap = new int[inputs.Count][];
        var contents = new List<ContentEntry>();
        var docs = new List<DocEntry>();

        for (var i = 0; i < inputs.Count; i++)
        {
            var reader = inputs[i].Reader;
            contentMap[i] = new int[reader.Contents.Count];
            for (var c = 0; c < reader.Contents.Count; c++)
            {
                contentMap[i][c] = inputs[i].OwnedContents[c] ? contents.Count : -1;
                if (contentMap[i][c] >= 0)
                {
                    contents.Add(reader.Contents[c]);
                }
            }
        }

        for (var i = 0; i < inputs.Count; i++)
        {
            var reader = inputs[i].Reader;
            docMap[i] = new int[reader.Docs.Count];
            for (var d = 0; d < reader.Docs.Count; d++)
            {
                if (!inputs[i].LiveDocs[d])
                {
                    docMap[i][d] = -1;
                    continue;
                }

                var doc = reader.Docs[d];
                if (inputIndexById.TryGetValue(doc.ContentSegment, out var target))
                {
                    var newContent = contentMap[target][doc.ContentId];
                    if (newContent < 0)
                    {
                        throw new InvalidOperationException($"Live document {doc.Path} references a content that is not owned.");
                    }

                    doc = doc with { ContentSegment = outputId, ContentId = newContent };
                }

                docMap[i][d] = docs.Count;
                docs.Add(doc);
            }
        }

        using (var output = storage.Create(fileName))
        {
            var writer = new SegmentWriter(output, outputId, contents, docs);
            MergeTerms(inputs, docMap, contentMap, writer, cancellation);
            writer.Finish();
        }

        return new Result(docMap, contentMap, docs.Count, contents.Count);
    }

    private static void MergeTerms(IReadOnlyList<Input> inputs, int[][] docMap, int[][] contentMap, SegmentWriter writer, CancellationToken cancellation)
    {
        var comparer = Comparer<(byte[] Key, int Input)>.Create((a, b) =>
        {
            var order = a.Key.AsSpan().SequenceCompareTo(b.Key);
            return order != 0 ? order : a.Input.CompareTo(b.Input);
        });
        var queue = new PriorityQueue<int, (byte[] Key, int Input)>(comparer);
        var enumerators = inputs.Select(i => i.Reader.AllTerms().GetEnumerator()).ToArray();
        for (var i = 0; i < enumerators.Length; i++)
        {
            if (enumerators[i].MoveNext())
            {
                queue.Enqueue(i, (enumerators[i].Current.Key, i));
            }
        }

        var encoder = new PostingsEncoder();
        var group = new List<int>();
        var processed = 0;
        while (queue.TryPeek(out _, out var head))
        {
            if (++processed % 1024 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            group.Clear();
            while (queue.TryPeek(out var input, out var current) && current.Key.AsSpan().SequenceEqual(head.Key))
            {
                queue.Dequeue();
                group.Add(input);
            }

            var space = (TermSpace)head.Key[0];
            var isBody = space is TermSpace.BodyTerm or TermSpace.BodyChar;
            var positional = space is TermSpace.BodyTerm or TermSpace.NameTerm;
            encoder.Reset();
            foreach (var input in group)
            {
                var entry = enumerators[input].Current;
                var map = isBody ? contentMap[input] : docMap[input];
                var bytes = inputs[input].Reader.ReadPostingsAndPositions(entry);
                if (positional)
                {
                    PostingsDecoder.ReadWithPositions(bytes.AsSpan(0, entry.PostingsLength), bytes.AsSpan(entry.PostingsLength), (id, positions) =>
                    {
                        if (map[id] >= 0)
                        {
                            encoder.Add(map[id], positions.Length, positions);
                        }
                    });
                }
                else
                {
                    PostingsDecoder.Read(bytes.AsSpan(0, entry.PostingsLength), (id, frequency) =>
                    {
                        if (map[id] >= 0)
                        {
                            encoder.Add(map[id], frequency, []);
                        }
                    });
                }

                if (enumerators[input].MoveNext())
                {
                    queue.Enqueue(input, (enumerators[input].Current.Key, input));
                }
            }

            if (encoder.Count > 0)
            {
                writer.AddTerm(head.Key, encoder.Count, encoder.Postings, encoder.Positions);
            }
        }
    }
}
