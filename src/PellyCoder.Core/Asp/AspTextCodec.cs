using System.Text;
using System.Text.RegularExpressions;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Asp;

/// <summary>
/// File codec of Classic ASP pages (design §4.3). Encoding: BOM → <c>&lt;%@ … CodePage=N %&gt;</c> (65001, 949) →
/// strict UTF-8 if any non-ASCII byte else CP949 → <paramref name="defaultEncoding"/> for pure ASCII.
/// Writing splices bytes: unchanged leading and trailing lines keep their original bytes (line endings included),
/// only the changed middle is encoded, so mixed line endings outside the edit survive byte for byte.
/// </summary>
public sealed partial class AspTextCodec : ITextFileCodec
{
    private const string Crlf = "\r\n";
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Cp949;

    private readonly Encoding _default;

    static AspTextCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949);
    }

    /// <param name="defaultEncoding"><c>cp949</c> or <c>utf-8</c> (<c>asp.encoding</c>), used for pure ASCII files.</param>
    public AspTextCodec(string defaultEncoding)
    {
        _default = defaultEncoding.ToLowerInvariant() switch
        {
            "cp949" => Cp949,
            "utf-8" => Utf8,
            _ => throw new ArgumentException($"Unsupported ASP encoding: {defaultEncoding}", nameof(defaultEncoding)),
        };
    }

    [GeneratedRegex("""CodePage\s*=\s*"?(\d+)"?""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodePagePattern();

    public FileSnapshot Decode(string path, byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            throw new InvalidDataException($"UTF-16 ASP 파일은 지원하지 않습니다: {path}");

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
            throw new InvalidDataException($"파일을 {NameOf(encoding)}(으)로 읽을 수 없습니다(CodePage 지시문과 실제 바이트가 어긋날 수 있습니다): {path}", ex);
        }

        int crlf = CountOf(raw, Crlf);
        int lf = raw.AsSpan().Count('\n') - crlf;
        string text = raw.Replace(Crlf, "\n");
        return new FileSnapshot(path, encoding, hasBom, crlf > lf ? Crlf : "\n", text.EndsWith('\n'),
            TextFileCodec.HashHex(bytes), text, MixedNewLines: false, Raw: bytes);
    }

    public byte[] Encode(FileSnapshot like, string textLf)
    {
        byte[] raw = like.Raw ?? throw new ArgumentException("ASP snapshot has no raw bytes.", nameof(like));
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

    private Encoding DetectWithoutBom(string path, byte[] bytes)
    {
        int? codePage = DirectiveCodePage(bytes);
        if (codePage is { } cp)
        {
            return cp switch
            {
                65001 => Utf8,
                949 => Cp949,
                _ => throw new InvalidDataException($"지원하지 않는 CodePage입니다: {cp} (65001 또는 949만 지원합니다): {path}"),
            };
        }
        if (bytes.AsSpan().IndexOfAnyExceptInRange((byte)0, (byte)0x7F) < 0) return _default;
        return IsValidUtf8(bytes) ? Utf8 : Cp949;
    }

    /// <summary>CodePage of the first <c>&lt;%@ … %&gt;</c> directive, read from the bytes as ASCII before decoding.</summary>
    private static int? DirectiveCodePage(byte[] bytes)
    {
        string ascii = Encoding.Latin1.GetString(bytes);
        int start = ascii.IndexOf("<%@", StringComparison.Ordinal);
        if (start < 0) return null;
        int end = ascii.IndexOf("%>", start + 3, StringComparison.Ordinal);
        if (end < 0) return null;
        var m = CodePagePattern().Match(ascii, start + 3, end - start - 3);
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].ValueSpan, out int value) ? value : -1;
    }

    private static bool IsValidUtf8(byte[] bytes)
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

    private static string NameOf(Encoding e) => e.CodePage == 949 ? "CP949" : "UTF-8";

    private static int CountOf(string s, string needle)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
