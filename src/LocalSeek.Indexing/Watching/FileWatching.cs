namespace LocalSeek.Indexing.Watching;

public enum FileChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
}

/// <summary>A change notification. For <see cref="FileChangeKind.Renamed"/>, OldPath is set.</summary>
public sealed record FileChange(FileChangeKind Kind, string Path, string? OldPath = null);

/// <summary>Watches one root (recursively). Injected so tests can drive events directly.</summary>
public interface IFileWatcher : IDisposable
{
    event EventHandler<FileChange>? Changed;

    /// <summary>Raised when events were lost (buffer overflow) or watching broke; a difference scan is needed.</summary>
    event EventHandler<Exception>? Failed;

    void Start();
}

public interface IFileWatcherFactory
{
    /// <summary>Creates a watcher; throws when the root cannot be watched.</summary>
    IFileWatcher Create(string root);
}

/// <summary>FileSystemWatcher with a 64 KB buffer over a whole root.</summary>
public sealed class PhysicalFileWatcherFactory : IFileWatcherFactory
{
    public IFileWatcher Create(string root) => new PhysicalFileWatcher(root);

    private sealed class PhysicalFileWatcher : IFileWatcher
    {
        private readonly FileSystemWatcher _watcher;

        public PhysicalFileWatcher(string root)
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Created += (_, e) => Changed?.Invoke(this, new FileChange(FileChangeKind.Created, e.FullPath));
            _watcher.Changed += (_, e) => Changed?.Invoke(this, new FileChange(FileChangeKind.Changed, e.FullPath));
            _watcher.Deleted += (_, e) => Changed?.Invoke(this, new FileChange(FileChangeKind.Deleted, e.FullPath));
            _watcher.Renamed += (_, e) => Changed?.Invoke(this, new FileChange(FileChangeKind.Renamed, e.FullPath, e.OldFullPath));
            _watcher.Error += (_, e) => Failed?.Invoke(this, e.GetException());
        }

        public event EventHandler<FileChange>? Changed;

        public event EventHandler<Exception>? Failed;

        public void Start() => _watcher.EnableRaisingEvents = true;

        public void Dispose() => _watcher.Dispose();
    }
}
