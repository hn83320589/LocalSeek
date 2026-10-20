using Microsoft.Win32.SafeHandles;

namespace LocalSeek.Core.Storage;

/// <summary>Index storage in a single directory on the local file system.</summary>
public sealed class FileSystemIndexStorage : IIndexStorage
{
    private const int WriteBufferSize = 1 << 16;

    private readonly string _directory;

    public FileSystemIndexStorage(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public IIndexOutput Create(string name) =>
        new Output(new FileStream(PathOf(name), FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, WriteBufferSize));

    public IIndexInput OpenRead(string name) =>
        new Input(File.OpenHandle(PathOf(name), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));

    public void AtomicRename(string source, string destination) =>
        File.Move(PathOf(source), PathOf(destination), overwrite: true);

    public void Delete(string name) => File.Delete(PathOf(name));

    public IReadOnlyList<string> List() =>
        Directory.EnumerateFiles(_directory).Select(p => Path.GetFileName(p)).ToList();

    private string PathOf(string name)
    {
        if (name.AsSpan().IndexOfAny('\\', '/') >= 0 || name is "." or "..")
        {
            throw new ArgumentException($"Invalid index file name '{name}'.", nameof(name));
        }

        return Path.Combine(_directory, name);
    }

    private sealed class Output(FileStream stream) : IIndexOutput
    {
        public long Position => stream.Position;

        public void Write(ReadOnlySpan<byte> data) => stream.Write(data);

        public void Flush() => stream.Flush(flushToDisk: true);

        public void Dispose() => stream.Dispose();
    }

    private sealed class Input(SafeFileHandle handle) : IIndexInput
    {
        public long Length { get; } = RandomAccess.GetLength(handle);

        public void ReadAt(long offset, Span<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                var read = RandomAccess.Read(handle, buffer, offset);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Read past end of index file at offset {offset}.");
                }

                buffer = buffer[read..];
                offset += read;
            }
        }

        public void Dispose() => handle.Dispose();
    }
}
