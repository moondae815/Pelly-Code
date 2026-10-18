using System.Text;

namespace PellyCoder.Core.Files;

/// <summary>
/// 텍스트 파일 디코딩 규칙 한 벌: BOM(UTF-8/UTF-16) → 엄격한 UTF-8 → CP949.
/// <see cref="TextFileCodec"/>과 DTO 수집기가 같은 규칙을 씁니다.
/// </summary>
public static class TextDecoding
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UnicodeEncoding Utf16Le = new(bigEndian: false, byteOrderMark: false);
    private static readonly UnicodeEncoding Utf16Be = new(bigEndian: true, byteOrderMark: false);
    private static readonly Encoding Cp949;

    static TextDecoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949);
    }

    internal readonly record struct Detected(Encoding Encoding, bool HasBom, int Offset);

    internal static Detected Detect(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return new Detected(Utf8, true, 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return new Detected(Utf16Le, true, 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return new Detected(Utf16Be, true, 2);
        return new Detected(IsValidUtf8(bytes) ? Utf8 : Cp949, false, 0);
    }

    /// <summary>Decodes strictly; throws <see cref="DecoderFallbackException"/> on bytes invalid for the detected encoding.</summary>
    internal static string DecodeStrict(Detected detected, byte[] bytes)
    {
        var clone = (Encoding)detected.Encoding.Clone();
        clone.DecoderFallback = DecoderFallback.ExceptionFallback;
        return clone.GetString(bytes, detected.Offset, bytes.Length - detected.Offset);
    }

    /// <summary>Text without BOM and with the file's own line breaks. Throws <see cref="DecoderFallbackException"/> when undecodable.</summary>
    public static string Decode(byte[] bytes) => DecodeStrict(Detect(bytes), bytes);

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
}
