namespace LocalSeek.Core.Storage;

/// <summary>
/// Every byte of index I/O goes through this interface so tests can inject faults and simulate
/// crashes at any operation. File names are flat (no directories).
/// </summary>
public interface IIndexStorage
{
    /// <summary>Creates (or truncates) a file for sequential writing.</summary>
    IIndexOutput Create(string name);

    IIndexInput OpenRead(string name);

    /// <summary>Atomically renames, replacing an existing destination.</summary>
    void AtomicRename(string source, string destination);

    void Delete(string name);

    IReadOnlyList<string> List();
}

public interface IIndexOutput : IDisposable
{
    long Position { get; }

    void Write(ReadOnlySpan<byte> data);

    /// <summary>Makes everything written so far durable.</summary>
    void Flush();
}

/// <summary>Random-access reader. ReadAt must be safe to call concurrently.</summary>
public interface IIndexInput : IDisposable
{
    long Length { get; }

    void ReadAt(long offset, Span<byte> buffer);
}
