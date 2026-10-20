namespace LocalSeek.Indexing.FileSystem;

/// <summary>A file or directory as seen while crawling.</summary>
public sealed record SourceEntry(string FullPath, bool IsDirectory, long Size, DateTime LastWriteUtc, FileAttributes Attributes)
{
    public string Name => Path.GetFileName(FullPath);

    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);

    public bool IsHiddenOrSystem => (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
}

/// <summary>The user's documents. Injected so crawling and watching can be tested against a fake.</summary>
public interface ISourceFileSystem
{
    /// <summary>Direct children. Throws UnauthorizedAccessException, DirectoryNotFoundException or IOException.</summary>
    IReadOnlyList<SourceEntry> EnumerateDirectory(string path);

    /// <summary>Current state of a path, or null when it does not exist.</summary>
    SourceEntry? GetEntry(string path);

    /// <summary>Reads a whole file, sharing it with writers. Throws IOException (e.g. sharing violation), UnauthorizedAccessException, FileNotFoundException.</summary>
    byte[] ReadAllBytes(string path);

    /// <summary>Final target of a junction or symbolic link, or null when the path is not a link.</summary>
    string? GetLinkTarget(string path);

    /// <summary>Whether change notifications are unreliable for this root (network shares).</summary>
    bool IsNetworkPath(string path);
}
