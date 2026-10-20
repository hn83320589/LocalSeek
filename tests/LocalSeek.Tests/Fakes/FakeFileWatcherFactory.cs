using LocalSeek.Indexing.Watching;

namespace LocalSeek.Tests.Fakes;

/// <summary>Watchers whose events are raised by the test.</summary>
public sealed class FakeFileWatcherFactory : IFileWatcherFactory
{
    public Dictionary<string, FakeWatcher> Watchers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Unwatchable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IFileWatcher Create(string root)
    {
        if (Unwatchable.Contains(root))
        {
            throw new IOException($"Cannot watch {root}.");
        }

        var watcher = new FakeWatcher();
        Watchers[root] = watcher;
        return watcher;
    }

    public sealed class FakeWatcher : IFileWatcher
    {
        public event EventHandler<FileChange>? Changed;

        public event EventHandler<Exception>? Failed;

        public bool Started { get; private set; }

        public void Raise(FileChange change) => Changed?.Invoke(this, change);

        public void Overflow() => Failed?.Invoke(this, new InternalBufferOverflowException("Too many changes at once."));

        public void Start() => Started = true;

        public void Dispose()
        {
        }
    }
}
