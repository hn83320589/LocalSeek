using LocalSeek.Core.Storage;

namespace LocalSeek.Core.Index;

/// <summary>
/// Reference counts for index files. Holders are the writer's working set, retained commits and open
/// snapshots. A file is deleted only when its count reaches zero; an attached resource (a segment
/// reader's file handle) is closed first. Failed deletions are remembered and retried.
/// </summary>
internal sealed class FileReferences(IIndexStorage storage)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _resources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingDeletes = new(StringComparer.Ordinal);
    private bool _closed;

    public int PendingDeleteCount
    {
        get
        {
            lock (_lock)
            {
                return _pendingDeletes.Count;
            }
        }
    }

    public bool IsReferenced(string name)
    {
        lock (_lock)
        {
            return _counts.ContainsKey(name);
        }
    }

    /// <summary>Attaches a resource to a file and takes one reference to it.</summary>
    public void Register(string name, IDisposable resource)
    {
        lock (_lock)
        {
            _resources[name] = resource;
            Increment(name);
        }
    }

    public void IncRef(IEnumerable<string> names)
    {
        lock (_lock)
        {
            foreach (var name in names)
            {
                Increment(name);
            }
        }
    }

    public void DecRef(IEnumerable<string> names)
    {
        lock (_lock)
        {
            foreach (var name in names)
            {
                if (!_counts.TryGetValue(name, out var count))
                {
                    throw new InvalidOperationException($"File {name} released more times than referenced.");
                }

                if (count > 1)
                {
                    _counts[name] = count - 1;
                    continue;
                }

                _counts.Remove(name);
                if (_closed)
                {
                    continue;
                }

                if (_resources.Remove(name, out var resource))
                {
                    resource.Dispose();
                }

                TryDelete(name);
            }
        }
    }

    public void RetryPendingDeletes()
    {
        lock (_lock)
        {
            foreach (var name in _pendingDeletes.ToList())
            {
                if (!_counts.ContainsKey(name))
                {
                    TryDelete(name);
                }
            }
        }
    }

    /// <summary>Deletes a file nobody references (startup cleanup of orphans).</summary>
    public void DeleteUnreferenced(string name)
    {
        lock (_lock)
        {
            if (!_counts.ContainsKey(name))
            {
                TryDelete(name);
            }
        }
    }

    /// <summary>Closes resources without deleting anything (writer shutdown or crash).</summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            foreach (var resource in _resources.Values)
            {
                resource.Dispose();
            }

            _resources.Clear();
        }
    }

    private void Increment(string name) => _counts[name] = _counts.GetValueOrDefault(name) + 1;

    private void TryDelete(string name)
    {
        try
        {
            storage.Delete(name);
            _pendingDeletes.Remove(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file stays on disk unreferenced; it is retried after the next commit and removed as an
            // orphan on the next open. Not fatal: the index itself is already consistent.
            _pendingDeletes.Add(name);
        }
    }
}
