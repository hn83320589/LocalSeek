using System.Text;
using LocalSeek.Indexing.FileSystem;

namespace LocalSeek.Tests.Fakes;

/// <summary>
/// In-memory directory tree with Windows-like behaviour: case-insensitive '\' paths, junctions
/// (directory links that may form cycles or dangle), access-denied entries, files locked by "another
/// program" for a number of reads, and files modified while being read. Independent of production code.
/// </summary>
public sealed class FakeSourceFileSystem : ISourceFileSystem
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public DateTime Now { get; set; } = new(2024, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    public int ReadCount { get; private set; }

    public HashSet<string> NetworkRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Node
    {
        public bool IsDirectory { get; init; }

        public byte[] Content { get; set; } = [];

        public DateTime LastWriteUtc { get; set; }

        public FileAttributes ExtraAttributes { get; set; }

        public string? LinkTarget { get; init; }

        public bool Denied { get; set; }

        public int LocksRemaining { get; set; }

        public byte[]? ChangeOnRead { get; set; }
    }

    public FakeSourceFileSystem Directory(string path, FileAttributes extra = 0)
    {
        lock (_lock)
        {
            EnsureParents(path);
            _nodes[Normalize(path)] = new Node { IsDirectory = true, LastWriteUtc = Now, ExtraAttributes = extra };
            return this;
        }
    }

    public FakeSourceFileSystem File(string path, string content, FileAttributes extra = 0) =>
        File(path, Encoding.UTF8.GetBytes(content), extra);

    public FakeSourceFileSystem File(string path, byte[] content, FileAttributes extra = 0)
    {
        lock (_lock)
        {
            EnsureParents(path);
            _nodes[Normalize(path)] = new Node { Content = content, LastWriteUtc = Now, ExtraAttributes = extra };
            return this;
        }
    }

    public FakeSourceFileSystem Junction(string path, string target)
    {
        lock (_lock)
        {
            EnsureParents(path);
            _nodes[Normalize(path)] = new Node { IsDirectory = true, LinkTarget = Normalize(target), LastWriteUtc = Now };
            return this;
        }
    }

    public void Deny(string path) => WithNode(path, n => n.Denied = true);

    public void Lock(string path, int reads = int.MaxValue) => WithNode(path, n => n.LocksRemaining = reads);

    public void ChangeDuringNextRead(string path, string newContent) => WithNode(path, n => n.ChangeOnRead = Encoding.UTF8.GetBytes(newContent));

    /// <summary>Overwrites a file's content and advances its modification time.</summary>
    public void Write(string path, string content)
    {
        lock (_lock)
        {
            Now = Now.AddSeconds(1);
            if (_nodes.TryGetValue(Normalize(path), out var node))
            {
                node.Content = Encoding.UTF8.GetBytes(content);
                node.LastWriteUtc = Now;
            }
            else
            {
                File(path, content);
            }
        }
    }

    public void Delete(string path)
    {
        lock (_lock)
        {
            var key = Normalize(path);
            foreach (var k in _nodes.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + '\\', StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _nodes.Remove(k);
            }
        }
    }

    /// <summary>Renames or moves a file or directory (with its subtree); timestamps are preserved.</summary>
    public void Move(string from, string to)
    {
        lock (_lock)
        {
            var source = Normalize(from);
            var target = Normalize(to);
            EnsureParents(target);
            foreach (var k in _nodes.Keys.Where(k => k.Equals(source, StringComparison.OrdinalIgnoreCase) || k.StartsWith(source + '\\', StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var node = _nodes[k];
                _nodes.Remove(k);
                _nodes[target + k[source.Length..]] = node;
            }
        }
    }

    // ------------------------------------------------------------------ ISourceFileSystem

    public IReadOnlyList<SourceEntry> EnumerateDirectory(string path)
    {
        lock (_lock)
        {
            var logical = Normalize(path);
            var resolved = Resolve(logical);
            if (!_nodes.TryGetValue(resolved, out var node) || !node.IsDirectory)
            {
                throw new DirectoryNotFoundException($"Fake directory not found: {path}");
            }

            if (node.Denied)
            {
                throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            }

            return _nodes
                .Where(kv => ParentOf(kv.Key).Equals(resolved, StringComparison.OrdinalIgnoreCase))
                .Select(kv => ToEntry(logical + '\\' + NameOf(kv.Key), kv.Value))
                .ToList();
        }
    }

    public SourceEntry? GetEntry(string path)
    {
        lock (_lock)
        {
            var logical = Normalize(path);
            var key = ResolveParent(logical);
            if (!_nodes.TryGetValue(key, out var node))
            {
                return null;
            }

            if (node.LinkTarget is not null && !_nodes.ContainsKey(Resolve(node.LinkTarget)))
            {
                return null;
            }

            return ToEntry(logical, node);
        }
    }

    public byte[] ReadAllBytes(string path)
    {
        lock (_lock)
        {
            ReadCount++;
            if (!_nodes.TryGetValue(Resolve(Normalize(path)), out var node) || node.IsDirectory)
            {
                throw new FileNotFoundException($"Fake file not found: {path}", path);
            }

            if (node.Denied)
            {
                throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            }

            if (node.LocksRemaining > 0)
            {
                node.LocksRemaining--;
                throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.") { HResult = unchecked((int)0x80070020) };
            }

            var content = node.Content.ToArray();
            if (node.ChangeOnRead is { } changed)
            {
                node.ChangeOnRead = null;
                node.Content = changed;
                node.LastWriteUtc = node.LastWriteUtc.AddSeconds(1);
            }

            return content;
        }
    }

    public string? GetLinkTarget(string path)
    {
        lock (_lock)
        {
            return _nodes.TryGetValue(ResolveParent(Normalize(path)), out var node) && node.LinkTarget is not null
                ? Resolve(node.LinkTarget)
                : null;
        }
    }

    public bool IsNetworkPath(string path) => NetworkRoots.Any(r => Normalize(path).StartsWith(Normalize(r), StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ helpers

    public static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('\\');
        return slash < 0 ? "" : path[..slash];
    }

    private static string NameOf(string path) => path[(path.LastIndexOf('\\') + 1)..];

    /// <summary>Resolves links in every component (with a hop limit, like the OS).</summary>
    private string Resolve(string path)
    {
        for (var hops = 0; hops < 32; hops++)
        {
            var parts = path.Split('\\');
            var current = parts[0];
            var changed = false;
            for (var i = 1; i < parts.Length; i++)
            {
                current += '\\' + parts[i];
                if (_nodes.TryGetValue(current, out var node) && node.LinkTarget is not null)
                {
                    path = node.LinkTarget + string.Concat(parts.Skip(i + 1).Select(p => '\\' + p));
                    changed = true;
                    break;
                }
            }

            if (!changed)
            {
                return path;
            }
        }

        throw new IOException($"Too many levels of links: {path}");
    }

    /// <summary>Resolves links in the parent only, so the final component itself is looked up as-is.</summary>
    private string ResolveParent(string path)
    {
        var parent = ParentOf(path);
        return parent.Length == 0 ? path : Resolve(parent) + '\\' + NameOf(path);
    }

    private SourceEntry ToEntry(string logicalPath, Node node)
    {
        var attributes = (node.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal) | node.ExtraAttributes;
        if (node.LinkTarget is not null)
        {
            attributes |= FileAttributes.ReparsePoint;
        }

        return new SourceEntry(logicalPath, node.IsDirectory, node.Content.LongLength, node.LastWriteUtc, attributes);
    }

    private void WithNode(string path, Action<Node> action)
    {
        lock (_lock)
        {
            action(_nodes[Resolve(Normalize(path))]);
        }
    }

    private void EnsureParents(string path)
    {
        var parent = ParentOf(Normalize(path));
        while (parent.Length > 0 && !_nodes.ContainsKey(parent))
        {
            _nodes[parent] = new Node { IsDirectory = true, LastWriteUtc = Now };
            parent = ParentOf(parent);
        }
    }
}
