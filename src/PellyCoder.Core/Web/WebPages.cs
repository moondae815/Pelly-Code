using PellyCoder.Core.Files;

namespace PellyCoder.Core.Web;

/// <summary>Which paths are pages (design §2-1): the configured ASP extensions plus <c>.htm</c> and <c>.html</c>.</summary>
public static class WebPages
{
    public static IReadOnlyList<string> PageExtensions(IReadOnlyList<string> aspExtensions) =>
        [.. aspExtensions, ".htm", ".html"];

    public static bool IsPage(string path, IReadOnlyList<string> aspExtensions)
    {
        string ext = Path.GetExtension(path);
        return PageExtensions(aspExtensions).Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Picks the codec by file extension: pages go to <c>page</c> (the ASP codec), everything else to <c>file</c> (JS/CSS).</summary>
public sealed class WebCodecRouter(ITextFileCodec file, ITextFileCodec page, IReadOnlyList<string> aspExtensions) : ITextFileCodec
{
    private ITextFileCodec For(string path) => WebPages.IsPage(path, aspExtensions) ? page : file;

    public FileSnapshot Decode(string path, byte[] bytes) => For(path).Decode(path, bytes);

    public byte[] Encode(FileSnapshot like, string textLf) => For(like.Path).Encode(like, textLf);

    public FileSnapshot AfterWrite(FileSnapshot current, string newTextLf, byte[] written) =>
        For(current.Path).AfterWrite(current, newTextLf, written);
}
