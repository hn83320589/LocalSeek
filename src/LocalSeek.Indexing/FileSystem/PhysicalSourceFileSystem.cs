namespace LocalSeek.Indexing.FileSystem;

/// <summary><see cref="ISourceFileSystem"/> over the real disk. .NET handles paths longer than 260 chars itself.</summary>
public sealed class PhysicalSourceFileSystem : ISourceFileSystem
{
    private static readonly EnumerationOptions Children = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public IReadOnlyList<SourceEntry> EnumerateDirectory(string path) =>
        new DirectoryInfo(path).EnumerateFileSystemInfos("*", Children).Select(ToEntry).ToList();

    public SourceEntry? GetEntry(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists ? ToEntry(info) : null;
    }

    public byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        var length = stream.Length;
        if (length > int.MaxValue)
        {
            throw new IOException($"File too large to read: {path}");
        }

        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public string? GetLinkTarget(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.LinkTarget;
        }
        catch (IOException)
        {
            // Cyclic or otherwise unresolvable link chain; report the immediate target so the caller treats it as broken.
            return info.LinkTarget ?? path;
        }
    }

    public bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
    }

    private static SourceEntry ToEntry(FileSystemInfo info) => new(
        info.FullName,
        info is DirectoryInfo,
        info is FileInfo file ? file.Length : 0,
        info.LastWriteTimeUtc,
        info.Attributes);
}
