using System.IO.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Web;

/// <param name="VisibleNames">Top-level names a script of the page can see besides the <c>.js</c> index, built-ins and <c>web.globals</c>.</param>
/// <param name="ScriptSources">Site-relative paths of the local <c>&lt;script src&gt;</c> files the page (and its includes) load.</param>
/// <param name="Jquery">The jQuery baseline; null when no setting, page or site tells.</param>
/// <param name="JquerySource"><c>setting</c>, <c>page</c> or <c>site</c>.</param>
public sealed record PageScriptContext(IReadOnlySet<string> VisibleNames, IReadOnlyList<string> ScriptSources, Version? Jquery, string JquerySource);

/// <summary>What a script in a page can see (design §4.6): other blocks, include blocks and the <c>.js</c> files the page loads.</summary>
public static class PageScripts
{
    private const int MaxIncludeDepth = 5;
    private const int MaxIncludeFiles = 40;
    private const int MaxIncluders = 200;
    private const long MaxBytes = 1_048_576;

    /// <summary>
    /// Collects the names and jQuery of the target page. When other pages include the target (a shared <c>.inc</c>), the names
    /// are the union over those pages and jQuery is the lowest version any of them loads; without includers only the target's
    /// own page and includes count. <paramref name="pageTextLf"/> is read from the file when not given.
    /// </summary>
    public static PageScriptContext Collect(
        IFileSystem fs, string siteRoot, string pagePath, AspPage page, int targetBlockIndex, ITextFileCodec codec,
        WebSiteIndexResult index, IReadOnlyList<JqueryFound> siteJquery, string? configuredJquery, string? pageTextLf = null)
    {
        var root = siteRoot.Replace('\\', '/').TrimEnd('/');
        var rel = WebSiteIndex.Relative(root, pagePath);
        var walker = new Walker(fs, siteRoot, codec, index);
        var text = pageTextLf ?? walker.ReadText(pagePath) ?? "";
        var names = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<string>();

        walker.Visible(rel, page, text, targetBlockIndex, names, sources);
        foreach (var includer in index.IncludersOf(rel).Take(MaxIncluders))
            if (walker.Load(includer) is { } parent)
                walker.Visible(includer, parent.Page, parent.Text, -1, names, sources);

        var versions = sources.Select(s => walker.JqueryVersion(s)).OfType<Version>().ToList();
        Version? jquery;
        string source;
        if (!string.IsNullOrWhiteSpace(configuredJquery) && Version.TryParse(configuredJquery.Trim(), out var set))
            (jquery, source) = (set, "setting");
        else if (versions.Count > 0)
            (jquery, source) = (versions.Min(), "page");
        else
            (jquery, source) = (JqueryVersions.Baseline(siteJquery, null), "site");
        return new PageScriptContext(names, sources, jquery, source);
    }

    private sealed record Loaded(AspPage Page, string Text);

    private sealed class Walker(IFileSystem fs, string siteRoot, ITextFileCodec codec, WebSiteIndexResult index)
    {
        private readonly Dictionary<string, Loaded?> _pages = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> _jsNames = new(StringComparer.OrdinalIgnoreCase);
        private ILookup<string, string>? _indexed;

        private string Full(string rel) => fs.Path.Combine(siteRoot, rel);

        public string? ReadText(string full)
        {
            try
            {
                if (!fs.File.Exists(full) || fs.FileInfo.New(full).Length > MaxBytes) return null;
                return codec.Decode(full, fs.File.ReadAllBytes(full)).Text;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return null;
            }
        }

        public Loaded? Load(string rel)
        {
            if (_pages.TryGetValue(rel, out var hit)) return hit;
            var text = ReadText(Full(rel));
            return _pages[rel] = text is null ? null : new Loaded(AspPage.Parse(text), text);
        }

        /// <summary>Adds what the page sees: its other blocks, its <c>src</c> files, and the same for its includes (depth and count limits).</summary>
        public void Visible(string rel, AspPage page, string text, int skipBlock, HashSet<string> names, List<string> sources)
        {
            AddOwn(rel, page, text, skipBlock, names, sources);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rel };
            var files = 0;
            void Walk(string fromRel, AspPage from, int depth)
            {
                foreach (var include in from.Includes)
                {
                    if (PagePaths.ResolveInclude(PagePaths.Directory(fromRel), include) is not { } target || !visited.Add(target)) continue;
                    if (depth > MaxIncludeDepth || files >= MaxIncludeFiles) continue;
                    if (Load(target) is not { } loaded) continue;
                    files++;
                    AddOwn(target, loaded.Page, loaded.Text, -1, names, sources);
                    Walk(target, loaded.Page, depth + 1);
                }
            }
            Walk(rel, page, 1);
        }

        private void AddOwn(string rel, AspPage page, string text, int skipBlock, HashSet<string> names, List<string> sources)
        {
            foreach (var e in WebSiteIndex.PageDeclarations(page, text, skipBlock)) names.Add(e.Name);
            var dir = PagePaths.Directory(rel);
            foreach (var block in page.ClientBlocks)
            {
                if (block.Kind != AspClientBlockKind.Script || block.Src is null) continue;
                if (PagePaths.ResolveScriptSrc(dir, block.Src) is not { } src) continue;
                if (!sources.Contains(src, StringComparer.OrdinalIgnoreCase)) sources.Add(src);
                names.UnionWith(NamesOfJs(src));
            }
        }

        /// <summary>Top-level names of a loaded <c>.js</c>: from the index, else by reading it (a library path is not indexed).</summary>
        private IReadOnlyList<string> NamesOfJs(string rel)
        {
            if (_jsNames.TryGetValue(rel, out var hit)) return hit;
            _indexed ??= index.Entries.Where(e => e.Source == JsIndexSource.Js).ToLookup(e => e.RelativePath, e => e.Name, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> result = _indexed.Contains(rel)
                ? _indexed[rel].ToList()
                : ReadText(Full(rel)) is { } text ? WebSiteIndex.Extract(text).Select(e => e.Name).ToList() : [];
            return _jsNames[rel] = result;
        }

        public Version? JqueryVersion(string rel)
        {
            var name = fs.Path.GetFileName(rel);
            if (!name.StartsWith("jquery", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                return null;
            return JqueryVersions.VersionOf(fs, Full(rel));
        }
    }
}
