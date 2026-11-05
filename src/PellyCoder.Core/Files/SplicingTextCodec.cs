using System.Text;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Files;

/// <summary>
/// Byte-splicing codec shared by the ASP and web (JS/CSS) editors. Writing keeps the original bytes of unchanged
/// leading and trailing lines (line endings included) and encodes only the changed middle, so mixed line endings
/// outside the edit survive byte for byte. Subclasses decide the encoding of a BOM-less file and the messages.
/// </summary>
public abstract class SplicingTextCodec : ITextFileCodec
{
    private const string Crlf = "\r\n";
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    protected static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    protected static readonly Encoding Cp949;

    static SplicingTextCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949);
    }

    /// <summary>The encoding for <c>cp949</c> or <c>utf-8</c> (case-insensitive); anything else is an <see cref="ArgumentException"/>.</summary>
    public static Encoding ForName(string name) => name.ToLowerInvariant() switch
    {
        "cp949" => Cp949,
        "utf-8" => Utf8,
        _ => throw new ArgumentException($"Unsupported encoding: {name}", nameof(name)),
    };

    /// <summary>Encoding of a file without BOM; throws <see cref="InvalidDataException"/> (Korean) when unsupported.</summary>
    protected abstract Encoding DetectWithoutBom(string path, byte[] bytes);

    protected abstract string Utf16Message(string path);

    protected abstract string StrictReadMessage(string path, Encoding encoding);

    public FileSnapshot Decode(string path, byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            throw new InvalidDataException(Utf16Message(path));

        bool hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
        int offset = hasBom ? Utf8Bom.Length : 0;
        Encoding encoding = hasBom ? Utf8 : DetectWithoutBom(path, bytes);

        string raw;
        try
        {
            var strict = (Encoding)encoding.Clone();
            strict.DecoderFallback = DecoderFallback.ExceptionFallback;
            raw = strict.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(StrictReadMessage(path, encoding), ex);
        }

        int crlf = CountOf(raw, Crlf);
        int lf = raw.AsSpan().Count('\n') - crlf;
        string text = raw.Replace(Crlf, "\n");
        return new FileSnapshot(path, encoding, hasBom, crlf > lf ? Crlf : "\n", text.EndsWith('\n'),
            TextFileCodec.HashHex(bytes), text, MixedNewLines: false, Raw: bytes);
    }

    public byte[] Encode(FileSnapshot like, string textLf)
    {
        byte[] raw = like.Raw ?? throw new ArgumentException("Snapshot has no raw bytes.", nameof(like));
        int offset = like.HasBom && raw.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;

        var oldLines = SplitText(like.Text);
        var newLines = SplitText(TextFileCodec.NormalizeTail(like, textLf));
        var rawLines = SplitRaw(raw, offset);
        if (rawLines.Count != oldLines.Count)
            throw new InvalidOperationException($"Snapshot text does not match its raw bytes: {like.Path}");

        int prefix = 0;
        int max = Math.Min(oldLines.Count, newLines.Count);
        while (prefix < max && oldLines[prefix] == newLines[prefix]) prefix++;
        int suffix = 0;
        while (suffix < max - prefix && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;

        string newLine = MajorityNewLine(raw, rawLines, prefix, rawLines.Count - suffix, like.NewLine);
        var strict = (Encoding)like.Encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;

        using var output = new MemoryStream(raw.Length + 64);
        output.Write(raw, 0, offset);
        int headEnd = prefix == 0 ? offset : rawLines[prefix - 1].End;
        output.Write(raw, offset, headEnd - offset);
        for (int i = prefix; i < newLines.Count - suffix; i++)
        {
            string line = newLines[i];
            bool terminated = line.EndsWith('\n');
            string content = terminated ? line[..^1] : line;
            try
            {
                output.Write(strict.GetBytes(content));
            }
            catch (EncoderFallbackException ex)
            {
                char bad = ex.IsUnknownSurrogate() ? ex.CharUnknownHigh : ex.CharUnknown;
                throw new UnencodableTextException(i + 1, bad, like.Encoding.WebName);
            }
            if (terminated) output.Write(Encoding.ASCII.GetBytes(newLine));
        }
        int tailStart = suffix == 0 ? raw.Length : rawLines[^suffix].Start;
        output.Write(raw, tailStart, raw.Length - tailStart);
        return output.ToArray();
    }

    public FileSnapshot AfterWrite(FileSnapshot current, string newTextLf, byte[] written) => Decode(current.Path, written);

    protected static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Lines of LF text, each with its own <c>\n</c>; the last one may lack it.</summary>
    private static List<string> SplitText(string text)
    {
        var lines = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            int end = nl < 0 ? text.Length : nl + 1;
            lines.Add(text[start..end]);
            start = end;
        }
        return lines;
    }

    /// <summary>Byte ranges of the raw lines (split on 0x0A, ending included), from <paramref name="offset"/>.</summary>
    private static List<(int Start, int End)> SplitRaw(byte[] raw, int offset)
    {
        var lines = new List<(int, int)>();
        int start = offset;
        while (start < raw.Length)
        {
            int nl = Array.IndexOf(raw, (byte)'\n', start);
            int end = nl < 0 ? raw.Length : nl + 1;
            lines.Add((start, end));
            start = end;
        }
        return lines;
    }

    /// <summary>Majority line ending of the replaced raw lines [from, to); <paramref name="fallback"/> when none or tied.</summary>
    private static string MajorityNewLine(byte[] raw, List<(int Start, int End)> lines, int from, int to, string fallback)
    {
        int crlf = 0, lf = 0;
        for (int i = from; i < to; i++)
        {
            var (start, end) = lines[i];
            if (raw[end - 1] != (byte)'\n') continue;
            if (end - 2 >= start && raw[end - 2] == (byte)'\r') crlf++;
            else lf++;
        }
        return crlf > lf ? Crlf : lf > crlf ? "\n" : fallback;
    }

    protected static string NameOf(Encoding e) => e.CodePage == 949 ? "CP949" : "UTF-8";

    private static int CountOf(string s, string needle)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
