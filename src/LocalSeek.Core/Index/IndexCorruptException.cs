namespace LocalSeek.Core.Index;

/// <summary>An index file failed validation (bad magic, checksum or structure).</summary>
public class IndexCorruptException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An index file was written by a different format version.</summary>
public sealed class IndexFormatVersionException(string message) : IndexCorruptException(message);
