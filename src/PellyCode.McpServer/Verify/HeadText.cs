using System.Text;
using PellyCode.Core.Files;

namespace PellyCode.McpServer.Verify;

/// <summary>
/// The 「수정 전」 text of a changed file (design §7): its HEAD bytes decoded the way the current file is, with LF line ends. The
/// same codec on both sides keeps a CP949 page from turning into mojibake names; LF on both sides keeps a HEAD blob stored with LF
/// comparable with a CRLF working tree.
/// </summary>
internal static class HeadText
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <param name="head">The HEAD bytes.</param>
    /// <param name="current">The current file as decoded; null when it could not be read.</param>
    /// <param name="currentBytes">The current file's bytes: a pure ASCII file says nothing about the encoding.</param>
    /// <param name="decode">The file's own codec, used when the current encoding does not fit (a BOM, a pure ASCII current file,
    /// bytes that are not valid in it).</param>
    public static string Decode(byte[] head, FileSnapshot? current, byte[]? currentBytes, Func<byte[], FileSnapshot> decode)
    {
        var ascii = currentBytes is null || currentBytes.AsSpan().IndexOfAnyExceptInRange((byte)0, (byte)0x7F) < 0;
        if (current is not null && !ascii && !head.AsSpan().StartsWith(Utf8Bom))
        {
            try
            {
                var strict = (Encoding)current.Encoding.Clone();
                strict.DecoderFallback = DecoderFallback.ExceptionFallback;
                return Lf(strict.GetString(head));
            }
            catch (DecoderFallbackException)
            {
            }
        }
        return Lf(decode(head).Text);
    }

    /// <summary>CRLF as LF, without a leading BOM character.</summary>
    public static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\uFEFF');
}
