namespace LocalSeek.Indexing.FileSystem;

/// <summary>Result of one attempt to read a source file.</summary>
public abstract record ReadOutcome
{
    private ReadOutcome()
    {
    }

    public sealed record Success(byte[] Bytes, SourceEntry Entry) : ReadOutcome;

    /// <summary>Another program holds a lock; try again later.</summary>
    public sealed record Locked(string Message) : ReadOutcome;

    /// <summary>The file changed while it was being read; read it again after it settles.</summary>
    public sealed record Changed : ReadOutcome;

    public sealed record Missing : ReadOutcome;

    public sealed record AccessDenied(string Message) : ReadOutcome;

    public sealed record Failed(string Message) : ReadOutcome;
}

public static class SourceFileReader
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    /// <summary>Reads the whole file and verifies size and modification time did not change meanwhile.</summary>
    public static ReadOutcome Read(ISourceFileSystem fileSystem, string path)
    {
        var before = fileSystem.GetEntry(path);
        if (before is null || before.IsDirectory)
        {
            return new ReadOutcome.Missing();
        }

        byte[] bytes;
        try
        {
            bytes = fileSystem.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ReadOutcome.Missing();
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ReadOutcome.AccessDenied(ex.Message);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return new ReadOutcome.Locked(ex.Message);
        }
        catch (IOException ex)
        {
            return new ReadOutcome.Failed(ex.Message);
        }

        var after = fileSystem.GetEntry(path);
        if (after is null)
        {
            return new ReadOutcome.Missing();
        }

        if (after.Size != before.Size || after.LastWriteUtc != before.LastWriteUtc || bytes.LongLength != after.Size)
        {
            return new ReadOutcome.Changed();
        }

        return new ReadOutcome.Success(bytes, after);
    }
}
