using System.IO.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Impact;

/// <summary>
/// The site as the spec checks and the apply see it (W3 design §4.3–§4.5): its root (<c>asp.siteRoot</c>, else the workspace),
/// site-relative paths, decoded text, and the W2 site index with its cache.
/// </summary>
public sealed class WebSiteAccess(IFileSystem fs, AspOptions asp, WebOptions web, string workspaceRoot)
{
    private readonly WebCodecRouter _codec = new(new WebTextCodec(asp.Encoding), new AspTextCodec(asp.Encoding), asp.Extensions);

    public string SiteRoot { get; } = AspSiteRoot.Resolve(fs, asp.SiteRoot, workspaceRoot);

    public IReadOnlyList<string> AspExtensions => asp.Extensions;

    public bool Exists => fs.Directory.Exists(SiteRoot);

    /// <summary>A spec task's path relative to the site root, with '/'.</summary>
    public string SiteRelative(SpecTask task) =>
        fs.Path.GetRelativePath(SiteRoot, SpecTaskPaths.FullPath(fs.Path, workspaceRoot, task.Path)).Replace('\\', '/');

    /// <summary><see cref="Read"/> that reads each file once (for checks that look at many names over the same files).</summary>
    public Func<string, string?> CachedReader()
    {
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        return rel => cache.TryGetValue(rel, out var text) ? text : cache[rel] = Read(rel);
    }

    /// <summary>A site-relative file's text (LF); null when it cannot be read or decoded.</summary>
    public string? Read(string relativePath)
    {
        try
        {
            var full = fs.Path.Combine(SiteRoot, relativePath);
            return _codec.Decode(full, fs.File.ReadAllBytes(full)).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The site index, reusing <c>.pelly/cache/web-site-index.json</c> for files whose bytes did not change.</summary>
    public WebSiteIndexResult Index() =>
        WebSiteIndex.Load(fs, SiteRoot, new WebFileFilter(SiteRoot, web.VendorPaths), _codec,
            fs.Path.Combine(workspaceRoot, ".pelly", "cache", "web-site-index.json"),
            aspExtensions: asp.Extensions, spCallFunctions: asp.SpCallFunctions);
}
