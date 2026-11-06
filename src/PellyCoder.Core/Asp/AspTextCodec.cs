using System.Text;
using System.Text.RegularExpressions;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Asp;

/// <summary>
/// File codec of Classic ASP pages (design §4.3). Encoding: BOM → <c>&lt;%@ … CodePage=N %&gt;</c> (65001, 949) →
/// strict UTF-8 if any non-ASCII byte else CP949 → <paramref name="defaultEncoding"/> for pure ASCII.
/// Splicing of the written bytes is in <see cref="SplicingTextCodec"/>.
/// </summary>
public sealed partial class AspTextCodec : SplicingTextCodec
{
    private readonly Encoding _default;

    /// <param name="defaultEncoding"><c>cp949</c> or <c>utf-8</c> (<c>asp.encoding</c>), used for pure ASCII files.</param>
    public AspTextCodec(string defaultEncoding)
    {
        try { _default = ForName(defaultEncoding); }
        catch (ArgumentException) { throw new ArgumentException($"Unsupported ASP encoding: {defaultEncoding}", nameof(defaultEncoding)); }
    }

    [GeneratedRegex("""CodePage\s*=\s*"?(\d+)"?""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodePagePattern();

    [GeneratedRegex("""<meta\s[^>]*?charset\s*=\s*["']?([^\s"';>/]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharsetPattern();

    protected override string Utf16Message(string path) => $"UTF-16 ASP 파일은 지원하지 않습니다: {path}";

    protected override string StrictReadMessage(string path, Encoding encoding) =>
        $"파일을 {NameOf(encoding)}(으)로 읽을 수 없습니다(CodePage 지시문과 실제 바이트가 어긋날 수 있습니다): {path}";

    protected override Encoding DetectWithoutBom(string path, byte[] bytes)
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
        if (IsHtml(path) && MetaCharset(bytes) is { } charset) return EncodingOfCharset(charset, "meta charset", path);
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

    private static bool IsHtml(string path) =>
        path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

    /// <summary>Charset of the first <c>&lt;meta charset=…&gt;</c> or <c>http-equiv</c> content within the first 4 KiB (ASCII read), or null.</summary>
    private static string? MetaCharset(byte[] bytes)
    {
        string head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var m = MetaCharsetPattern().Match(head);
        return m.Success ? m.Groups[1].Value : null;
    }
}
