using LocalSeek.Core.Storage;

namespace LocalSeek.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IIndexStorage"/> for fault-injection tests. It is independent of production code.
/// <list type="bullet">
/// <item>Every file has a durable prefix (bytes covered by Flush) and an unflushed tail.</item>
/// <item>Create, Write, Flush, AtomicRename and Delete are counted; <see cref="FailAtOperation"/> makes
/// that operation throw and marks the storage crashed (every later call throws, like a dead process).</item>
/// <item><see cref="SimulateCrash"/> drops unflushed tails (or keeps a random prefix: a torn write),
/// invalidates all handles and makes the storage usable again. Renames and deletes are durable.</item>
/// <item>Deleting a file that has an open reader throws, to catch premature deletion.</item>
/// </list>
/// </summary>
public sealed class FakeIndexStorage : IIndexStorage
{
    private readonly object _lock = new();
    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<FakeFile, int> _openReaders = [];
    private int _epoch;

    public int OperationCount { get; private set; }

    /// <summary>1-based number of the mutating operation that fails.</summary>
    public int? FailAtOperation { get; set; }

    public bool Crashed { get; private set; }

    public List<string> Log { get; } = [];

    public IIndexOutput Create(string name)
    {
        lock (_lock)
        {
            Operation($"create {name}");
            var file = new FakeFile();
            _files[name] = file;
            return new Output(this, file, name, _epoch);
        }
    }

    public IIndexInput OpenRead(string name)
    {
        lock (_lock)
        {
            ThrowIfCrashed();
            if (!_files.TryGetValue(name, out var file))
            {
                throw new FileNotFoundException($"Fake file not found: {name}", name);
            }

            _openReaders[file] = _openReaders.GetValueOrDefault(file) + 1;
            return new Input(this, file, _epoch);
        }
    }

    public void AtomicRename(string source, string destination)
    {
        lock (_lock)
        {
            Operation($"rename {source} -> {destination}");
            if (!_files.Remove(source, out var file))
            {
                throw new FileNotFoundException($"Fake file not found: {source}", source);
            }

            _files[destination] = file;
        }
    }

    public void Delete(string name)
    {
        lock (_lock)
        {
            Operation($"delete {name}");
            if (_files.TryGetValue(name, out var file) && _openReaders.GetValueOrDefault(file) > 0)
            {
                throw new InvalidOperationException($"File {name} deleted while a reader still has it open.");
            }

            _files.Remove(name);
        }
    }

    public IReadOnlyList<string> List()
    {
        lock (_lock)
        {
            ThrowIfCrashed();
            return [.. _files.Keys];
        }
    }

    /// <summary>Process death: unflushed data is lost (or torn), all handles become invalid.</summary>
    public void SimulateCrash(Random? torn = null)
    {
        lock (_lock)
        {
            foreach (var file in _files.Values)
            {
                var keep = file.DurableLength;
                if (torn is not null && file.Data.Count > keep)
                {
                    keep += torn.Next(file.Data.Count - keep + 1);
                }

                file.Data.RemoveRange(keep, file.Data.Count - keep);
                file.DurableLength = file.Data.Count;
            }

            _openReaders.Clear();
            _epoch++;
            Crashed = false;
            FailAtOperation = null;
        }
    }

    // ---- helpers for corrupting files in tests (not counted as operations) ----

    public bool Exists(string name)
    {
        lock (_lock)
        {
            return _files.ContainsKey(name);
        }
    }

    public IReadOnlyList<string> Files(string prefix, string suffix)
    {
        lock (_lock)
        {
            return _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.EndsWith(suffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        }
    }

    public byte[] ReadAll(string name)
    {
        lock (_lock)
        {
            return [.. _files[name].Data];
        }
    }

    public void ReplaceContent(string name, byte[] content)
    {
        lock (_lock)
        {
            var file = new FakeFile();
            file.Data.AddRange(content);
            file.DurableLength = content.Length;
            _files[name] = file;
        }
    }

    public void FlipByte(string name, int offset)
    {
        var bytes = ReadAll(name);
        bytes[offset] ^= 0x5A;
        ReplaceContent(name, bytes);
    }

    public void Remove(string name)
    {
        lock (_lock)
        {
            _files.Remove(name);
        }
    }

    private void Operation(string description)
    {
        ThrowIfCrashed();
        OperationCount++;
        Log.Add($"{OperationCount}: {description}");
        if (OperationCount == FailAtOperation)
        {
            Crashed = true;
            throw new IOException($"Injected fault at operation {OperationCount}: {description}");
        }
    }

    private void ThrowIfCrashed()
    {
        if (Crashed)
        {
            throw new IOException("Storage is crashed.");
        }
    }

    private void CheckEpoch(int epoch)
    {
        if (epoch != _epoch)
        {
            throw new IOException("Handle from before a crash.");
        }
    }

    private sealed class FakeFile
    {
        public List<byte> Data { get; } = [];

        public int DurableLength { get; set; }
    }

    private sealed class Output(FakeIndexStorage owner, FakeFile file, string name, int epoch) : IIndexOutput
    {
        public long Position
        {
            get
            {
                lock (owner._lock)
                {
                    return file.Data.Count;
                }
            }
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            lock (owner._lock)
            {
                owner.CheckEpoch(epoch);
                owner.Operation($"write {name} ({data.Length} bytes)");
                file.Data.AddRange(data);
            }
        }

        public void Flush()
        {
            lock (owner._lock)
            {
                owner.CheckEpoch(epoch);
                owner.Operation($"flush {name}");
                file.DurableLength = file.Data.Count;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class Input(FakeIndexStorage owner, FakeFile file, int epoch) : IIndexInput
    {
        private bool _disposed;

        public long Length
        {
            get
            {
                lock (owner._lock)
                {
                    return file.Data.Count;
                }
            }
        }

        public void ReadAt(long offset, Span<byte> buffer)
        {
            lock (owner._lock)
            {
                owner.ThrowIfCrashed();
                owner.CheckEpoch(epoch);
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (offset < 0 || offset + buffer.Length > file.Data.Count)
                {
                    throw new EndOfStreamException("Read past end of fake file.");
                }

                for (var i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = file.Data[(int)offset + i];
                }
            }
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (epoch == owner._epoch && owner._openReaders.TryGetValue(file, out var count))
                {
                    if (count <= 1)
                    {
                        owner._openReaders.Remove(file);
                    }
                    else
                    {
                        owner._openReaders[file] = count - 1;
                    }
                }
            }
        }
    }
}
