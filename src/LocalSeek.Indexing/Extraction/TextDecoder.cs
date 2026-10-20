using System.Text;

namespace LocalSeek.Indexing.Extraction;

/// <summary>
/// Detects the encoding of a text file: BOM (UTF-8, UTF-16 LE/BE) → binary check (NUL ratio) →
/// strict UTF-8 → strict Big5 (code page 950) → give up. Strict decoders throw on any invalid
/// sequence, so a file is only accepted if it is entirely valid in the chosen encoding.
/// </summary>
internal static class TextDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictBig5;

    static TextDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        StrictBig5 = Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static ExtractionResult Decode(ReadOnlySpan<byte> bytes, ExtractionOptions options)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Strict(StrictUtf8, bytes[3..], "UTF-8 BOM");
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Strict(StrictUtf16Le, bytes[2..], "UTF-16 LE");
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Strict(StrictUtf16Be, bytes[2..], "UTF-16 BE");
        }

        var sample = bytes[..Math.Min(bytes.Length, options.BinarySampleBytes)];
        if (sample.Length > 0 && (double)sample.Count((byte)0) / sample.Length > options.BinaryNulRatio)
        {
            return new ExtractionResult.Skipped(SkipReason.Binary, "內容含大量 NUL 位元組，視為二進位檔");
        }

        if (TryDecode(StrictUtf8, bytes, out var text))
        {
            return new ExtractionResult.Success(text, "UTF-8");
        }

        if (IsWellFormedBig5(bytes) && TryDecode(StrictBig5, bytes, out text))
        {
            return new ExtractionResult.Success(text, "Big5");
        }

        return new ExtractionResult.Skipped(SkipReason.UnknownEncoding, "無法判定編碼（不是有效的 UTF-8 或 Big5）");
    }

    private static ExtractionResult Strict(Encoding encoding, ReadOnlySpan<byte> bytes, string name) =>
        TryDecode(encoding, bytes, out var text)
            ? new ExtractionResult.Success(text, name)
            : new ExtractionResult.Skipped(SkipReason.UnknownEncoding, $"有 {name} 標記但內容無效，無法判定編碼");

    /// <summary>
    /// Windows code page 950 maps stray single bytes 0x80 and 0xFF to characters, so structure is checked
    /// separately: every byte is ASCII or part of a lead (0x81–0xFE) + trail (0x40–0x7E, 0xA1–0xFE) pair.
    /// </summary>
    private static bool IsWellFormedBig5(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length;)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                i++;
            }
            else if (b is >= 0x81 and <= 0xFE && i + 1 < bytes.Length && bytes[i + 1] is (>= 0x40 and <= 0x7E) or (>= 0xA1 and <= 0xFE))
            {
                i += 2;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDecode(Encoding encoding, ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            // Expected for the wrong candidate encoding; the caller tries the next one.
            text = "";
            return false;
        }
    }
}
