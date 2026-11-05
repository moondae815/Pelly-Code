using System.Text;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Web;

/// <summary>
/// File codec of client JS/CSS files. Encoding: BOM → leading <c>@charset "x";</c> (utf-8, euc-kr, ks_c_5601-1987,
/// cp949, windows-949) → strict UTF-8 if any non-ASCII byte else CP949 → <paramref name="defaultEncoding"/> for pure ASCII.
/// </summary>
public sealed class WebTextCodec(string defaultEncoding) : SplicingTextCodec
{
    private readonly Encoding _default = ForName(defaultEncoding);

    protected override string Utf16Message(string path) => $"UTF-16 파일은 지원하지 않습니다: {path}";

    protected override string StrictReadMessage(string path, Encoding encoding) =>
        $"파일을 {NameOf(encoding)}(으)로 읽을 수 없습니다(@charset과 실제 바이트가 어긋날 수 있습니다): {path}";

    protected override Encoding DetectWithoutBom(string path, byte[] bytes)
    {
        if (Charset(bytes) is { } name)
        {
            return name.ToLowerInvariant() switch
            {
                "utf-8" => Utf8,
                "euc-kr" or "ks_c_5601-1987" or "cp949" or "windows-949" => Cp949,
                _ => throw new InvalidDataException($"@charset \"{name}\"은 지원하지 않습니다: {path}"),
            };
        }
        if (bytes.AsSpan().IndexOfAnyExceptInRange((byte)0, (byte)0x7F) < 0) return _default;
        return IsValidUtf8(bytes) ? Utf8 : Cp949;
    }

    /// <summary>The value of <c>@charset "x";</c> at the very start of the file (ASCII read), or null.</summary>
    private static string? Charset(byte[] bytes)
    {
        const string Prefix = "@charset \"";
        int n = Math.Min(bytes.Length, 128);
        string head = Encoding.Latin1.GetString(bytes, 0, n);
        if (!head.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        int end = head.IndexOf("\";", Prefix.Length, StringComparison.Ordinal);
        return end < 0 ? null : head[Prefix.Length..end];
    }
}
