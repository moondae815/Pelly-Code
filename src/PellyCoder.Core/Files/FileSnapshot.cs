using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace PellyCoder.Core.Files;

/// <summary>
/// 디스크 파일의 읽은 시점 상태. <see cref="Text"/>는 <c>\n</c>으로 정규화된 본문이고,
/// <see cref="Sha256"/>은 디스크 바이트(정규화 전)의 소문자 hex 해시입니다.
/// <see cref="Encoding"/>은 BOM을 스스로 내보내지 않으며 BOM 여부는 <see cref="HasBom"/>만 결정합니다.
/// </summary>
public sealed record FileSnapshot(
    string Path,
    Encoding Encoding,
    bool HasBom,
    string NewLine,
    bool EndsWithNewLine,
    string Sha256,
    string Text);

/// <summary>수정 텍스트에 파일 인코딩으로 표현할 수 없는 문자가 있을 때 던집니다. <see cref="Line"/>은 1부터 시작합니다.</summary>
public sealed class UnencodableTextException(int line, char character, string encodingName)
    : Exception($"Line {line}: character U+{(int)character:X4} cannot be written in file encoding {encodingName}.")
{
    public int Line { get; } = line;
    public char Character { get; } = character;
    public string EncodingName { get; } = encodingName;
}

public static class TextFileCodec
{
    private const string Crlf = "\r\n";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UnicodeEncoding Utf16Le = new(bigEndian: false, byteOrderMark: false);
    private static readonly UnicodeEncoding Utf16Be = new(bigEndian: true, byteOrderMark: false);
    private static readonly Encoding Cp949;

    static TextFileCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949);
    }

    public static FileSnapshot Read(IFileSystem fs, string path) => Decode(path, fs.File.ReadAllBytes(path));

    public static FileSnapshot Decode(string path, byte[] bytes)
    {
        Encoding encoding;
        bool hasBom = true;
        int offset;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = Utf8; offset = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = Utf16Le; offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = Utf16Be; offset = 2; }
        else
        {
            hasBom = false;
            offset = 0;
            encoding = IsValidUtf8(bytes) ? Utf8 : Cp949;
        }

        string raw;
        try
        {
            raw = Strict(encoding, decoder: true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"File cannot be decoded as {encoding.WebName}: {path}", ex);
        }

        int crlf = CountOf(raw, Crlf);
        int lf = raw.AsSpan().Count('\n') - crlf;
        string newLine = crlf > lf ? Crlf : "\n";
        string text = raw.Replace(Crlf, "\n");
        bool endsWithNewLine = text.EndsWith('\n');
        string sha = HashHex(bytes);
        return new FileSnapshot(path, encoding, hasBom, newLine, endsWithNewLine, sha, text);
    }

    /// <summary>스냅샷의 인코딩·BOM·줄바꿈·끝 줄바꿈 유무로 <paramref name="textLf"/>를 바이트로 만듭니다.</summary>
    public static byte[] Encode(FileSnapshot like, string textLf)
    {
        string text = NormalizeTail(like, textLf);
        string withNewLines = like.NewLine == "\n" ? text : text.Replace("\n", like.NewLine);

        var encoder = Strict(like.Encoding, decoder: false);
        byte[] body;
        try
        {
            body = encoder.GetBytes(withNewLines);
        }
        catch (EncoderFallbackException ex)
        {
            // 인덱스는 줄바꿈 변환 후 문자열 기준이므로 LF 기준 줄 번호는 개행 개수로 센다.
            int line = 1 + CountLinesBefore(withNewLines, ex.Index);
            char bad = ex.IsUnknownSurrogate() ? ex.CharUnknownHigh : ex.CharUnknown;
            throw new UnencodableTextException(line, bad, like.Encoding.WebName);
        }

        byte[] preamble = like.HasBom ? PreambleOf(like.Encoding) : [];
        if (preamble.Length == 0) return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>
    /// 모델이 낸 텍스트를 스냅샷 규약에 맞춘다: 잔여 CRLF를 LF로 통일하고,
    /// 원본이 끝 줄바꿈을 갖지 않았으면 제거, 가졌으면 보장한다.
    /// </summary>
    public static string NormalizeTail(FileSnapshot like, string textLf)
    {
        string text = textLf.Replace(Crlf, "\n");
        if (like.EndsWithNewLine)
        {
            if (text.Length > 0 && !text.EndsWith('\n')) text += "\n";
            else if (text.Length == 0) text = "\n";
        }
        else
        {
            text = text.TrimEnd('\n');
        }
        return text;
    }

    public static string HashHex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Encoding Strict(Encoding e, bool decoder)
    {
        var clone = (Encoding)e.Clone();
        if (decoder) clone.DecoderFallback = DecoderFallback.ExceptionFallback;
        else clone.EncoderFallback = EncoderFallback.ExceptionFallback;
        return clone;
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

    private static byte[] PreambleOf(Encoding e) => e.CodePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        _ => [],
    };

    private static int CountOf(string s, string needle)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    private static int CountLinesBefore(string s, int index)
    {
        int n = 0;
        for (int i = 0; i < index && i < s.Length; i++) if (s[i] == '\n') n++;
        return n;
    }
}
