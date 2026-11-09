using System.IO.Abstractions;
using System.Text.Json;
using Acornima;
using Acornima.Ast;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Web;

/// <summary>Where an indexed declaration lives: a <c>.js</c> file or a client block of a page.</summary>
public enum JsIndexSource { Js, Page }

public sealed record JsIndexEntry(string Name, string RelativePath, int Line, string Declaration, JsIndexSource Source = JsIndexSource.Js);

/// <summary>A stored procedure call found in a page or include. <see cref="Procedure"/> is the enclosing Function or Sub, null outside one.</summary>
public sealed record AspSpCallEntry(string RelativePath, int Line, SqlObjectName Object, int? Arguments, string? Procedure);

public sealed record WebSiteIndexResult(IReadOnlyList<JsIndexEntry> Entries, int Files, bool Truncated)
{
    private IReadOnlySet<string>? _allNames;
    private IReadOnlySet<string>? _jsNames;
    private IReadOnlyDictionary<string, List<string>>? _includers;

    /// <summary>Pages scanned (a separate limit from <see cref="Files"/>, which counts <c>.js</c> files).</summary>
    public int PageFiles { get; init; }

    /// <summary>Stored procedure calls in pages and includes (<c>asp.extensions</c> files only).</summary>
    public IReadOnlyList<AspSpCallEntry> SpCalls { get; init; } = [];

    /// <summary>Page (site-relative, '/') to the files it includes directly (site-relative, '/').</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Includes { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Top-level names of the <c>.js</c> files only.</summary>
    public IReadOnlySet<string> JsNames => _jsNames ??= new HashSet<string>(
        Entries.Where(e => e.Source == JsIndexSource.Js).Select(e => e.Name), StringComparer.Ordinal);

    /// <summary>Top-level names of the <c>.js</c> files and of every page's client blocks.</summary>
    public IReadOnlySet<string> AllNames => _allNames ??= new HashSet<string>(Entries.Select(e => e.Name), StringComparer.Ordinal);

    /// <summary>Alias of <see cref="AllNames"/>.</summary>
    public IReadOnlySet<string> Names => AllNames;

    /// <summary>The pages that include the file, directly or through other includes (depth limit 5), sorted.</summary>
    public IReadOnlyList<string> IncludersOf(string relativePath)
    {
        _includers ??= BuildReverse();
        var start = relativePath.Replace('\\', '/');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var frontier = new List<string> { start };
        var result = new List<string>();
        for (var depth = 0; depth < 5 && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var f in frontier)
                if (_includers.TryGetValue(f, out var parents))
                    foreach (var p in parents)
                        if (seen.Add(p)) { result.Add(p); next.Add(p); }
            frontier = next;
        }
        return result.OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    private Dictionary<string, List<string>> BuildReverse()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (page, targets) in Includes)
            foreach (var t in targets)
            {
                if (!map.TryGetValue(t, out var list)) map[t] = list = [];
                list.Add(page);
            }
        return map;
    }
}

/// <summary>Top-level function and variable names of the site's own JavaScript files, cached by file hash.</summary>
public static class WebSiteIndex
{
    private const int CacheVersion = 3;

    private sealed class CacheFile
    {
        public int Version { get; set; }
        /// <summary>The wrapper functions the cached calls were found with; a change refreshes the pages.</summary>
        public string SpKey { get; set; } = "";
        public Dictionary<string, CacheItem> Files { get; set; } = new();
    }

    private sealed class CacheItem
    {
        public string Sha256 { get; set; } = "";
        public List<CacheEntry> Entries { get; set; } = new();
        public List<string> Includes { get; set; } = new();
        public List<CacheSpCall> SpCalls { get; set; } = new();
    }

    internal sealed class CacheSpCall
    {
        public int Line { get; set; }
        public string? Schema { get; set; }
        public string Name { get; set; } = "";
        public int? Arguments { get; set; }
        public string? Procedure { get; set; }
    }

    internal sealed class CacheEntry
    {
        public string Name { get; set; } = "";
        public int Line { get; set; }
        public string Declaration { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static WebSiteIndexResult Load(IFileSystem fs, string siteRoot, WebFileFilter filter, ITextFileCodec codec,
        string cachePath, int maxFiles = 2000, long maxBytes = 1_048_576, IReadOnlyList<string>? aspExtensions = null,
        IReadOnlyList<AspSpCallFunction>? spCallFunctions = null)
    {
        var old = ReadCache(fs, cachePath);
        var wrappers = spCallFunctions ?? [];
        var spKey = string.Join("|", wrappers.Select(w => $"{w.Name.ToLowerInvariant()}:{w.Argument}"));
        var fresh = new CacheFile { Version = CacheVersion, SpKey = spKey };
        var spCalls = new List<AspSpCallEntry>();
        var entries = new List<JsIndexEntry>();
        var includes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        var pageFiles = 0;
        var truncated = false;
        var extensions = aspExtensions ?? [".asp", ".inc"];

        var root = siteRoot.Replace('\\', '/').TrimEnd('/');
        List<(string Rel, string Full, bool Page)> candidates;
        try
        {
            // A folder that cannot be read is skipped, not the whole index.
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            candidates = fs.Directory.EnumerateFiles(siteRoot, "*", options)
                .Select(p => (Rel: Relative(root, p), Full: p, Page: !p.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && WebPages.IsPage(p, extensions)))
                .Where(c => c.Page || c.Full.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Rel, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new WebSiteIndexResult(entries, 0, false);
        }

        foreach (var (rel, full, isPage) in candidates)
        {
            // Each kind has its own limit, so many pages never cut the .js files off.
            if (isPage ? pageFiles >= maxFiles : files >= maxFiles)
            {
                truncated = true;
                continue;
            }
            byte[] bytes;
            string text;
            try
            {
                if (fs.FileInfo.New(full).Length > maxBytes) continue;
                bytes = fs.File.ReadAllBytes(full);
                text = codec.Decode(full, bytes).Text;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                continue;
            }
            if (!isPage && filter.Classify(full, text).Verdict != WebFileVerdict.Ok) continue;

            var sha = TextFileCodec.HashHex(bytes);
            CacheItem item;
            var reusable = !isPage || old.SpKey == spKey;
            if (reusable && old.Files.TryGetValue(rel, out var hit) && hit.Sha256 == sha) item = hit;
            else item = isPage ? ExtractPage(rel, sha, text, extensions.Any(e => rel.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? wrappers : null) : new CacheItem { Sha256 = sha, Entries = Extract(text) };
            fresh.Files[rel] = item;
            var source = isPage ? JsIndexSource.Page : JsIndexSource.Js;
            foreach (var it in item.Entries) entries.Add(new JsIndexEntry(it.Name, rel, it.Line, it.Declaration, source));
            if (isPage)
            {
                if (item.Includes.Count > 0) includes[rel] = item.Includes;
                foreach (var c in item.SpCalls)
                    spCalls.Add(new AspSpCallEntry(rel, c.Line, new SqlObjectName(c.Schema, c.Name), c.Arguments, c.Procedure));
                pageFiles++;
            }
            else files++;
        }

        WriteCache(fs, cachePath, fresh);
        return new WebSiteIndexResult(entries, files, truncated) { PageFiles = pageFiles, Includes = includes, SpCalls = spCalls };
    }

    private static CacheItem ExtractPage(string rel, string sha, string textLf, IReadOnlyList<AspSpCallFunction>? wrappers)
    {
        var page = AspPage.Parse(textLf);
        var item = new CacheItem { Sha256 = sha, Entries = PageDeclarations(page, textLf) };
        foreach (var call in wrappers is null ? [] : AspSpCallFinder.Find(page, wrappers))
        {
            var proc = page.Procedures.FirstOrDefault(p => call.Line >= p.DeclLine && call.Line <= p.EndLine);
            item.SpCalls.Add(new CacheSpCall
            {
                Line = call.Line, Schema = call.Name.Schema, Name = call.Name.Name, Arguments = call.ArgumentCount,
                Procedure = proc is null ? null : proc.ClassName is null ? proc.Name : proc.ClassName + "." + proc.Name,
            });
        }
        var dir = PagePaths.Directory(rel);
        foreach (var inc in page.Includes)
            if (PagePaths.ResolveInclude(dir, inc) is { } target && !item.Includes.Contains(target, StringComparer.OrdinalIgnoreCase))
                item.Includes.Add(target);
        return item;
    }

    /// <summary>Top-level declarations of every JavaScript script block of a page; a block that does not parse is skipped.</summary>
    internal static List<CacheEntry> PageDeclarations(AspPage page, string textLf, int skipBlock = -1)
    {
        var result = new List<CacheEntry>();
        for (var i = 0; i < page.ClientBlocks.Count; i++)
        {
            if (i == skipBlock || !PageBlockView.IsJavaScriptBlock(page.ClientBlocks[i])) continue;
            var view = PageBlockView.Build(page, textLf, i);
            if (JsDocument.Parse(view.Text, EcmaVersion.Latest, false).Error is not null) continue;
            result.AddRange(Extract(view.Text).Where(e => !PageBlockView.IsPlaceholder(e.Name)));
        }
        return result;
    }

    internal static string Relative(string root, string path)
    {
        var p = path.Replace('\\', '/');
        return p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? p[(root.Length + 1)..] : p;
    }

    internal static List<CacheEntry> Extract(string textLf)
    {
        var doc = JsDocument.Parse(textLf, EcmaVersion.Latest, false);
        if (doc.Error is not null)
            return JsTokenScan.FunctionDeclarations(textLf)
                .Where(f => f.Owner is null)
                .Select(f => new CacheEntry { Name = f.Name, Line = f.DeclLine, Declaration = f.Declaration })
                .GroupBy(e => e.Name).Select(g => g.First())
                .OrderBy(e => e.Line).ToList();

        // Declaration lines come from the top-level statements (functions are not entered).
        var lines = textLf.Split('\n');
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        if (JsDocument.TryParse(textLf, EcmaVersion.Latest, false, out var program) is null && program is not null)
            CollectDeclarations(program, found);

        var result = new List<CacheEntry>();
        foreach (var name in doc.TopLevelNames)
        {
            if (name == "arguments") continue;
            var fn = doc.Functions.FirstOrDefault(f => f.Owner is null && f.Name == name);
            int line;
            string decl;
            if (found.TryGetValue(name, out var l)) { line = l; decl = lines[l - 1].Trim(); }
            else if (fn is not null) { line = fn.DeclLine; decl = fn.Declaration; }
            else continue;
            result.Add(new CacheEntry { Name = name, Line = line, Declaration = decl });
        }
        return result.OrderBy(e => e.Line).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Finds declared names without entering functions or classes (var in blocks still hoists to top level).</summary>
    private static void CollectDeclarations(Node root, Dictionary<string, int> found)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            switch (n)
            {
                case FunctionDeclaration fd:
                    if (fd.Id is not null) found.TryAdd(fd.Id.Name, fd.Location.Start.Line);
                    continue;
                case ClassDeclaration cd:
                    if (cd.Id is not null) found.TryAdd(cd.Id.Name, cd.Location.Start.Line);
                    continue;
                case FunctionExpression or ArrowFunctionExpression or ClassExpression:
                    continue;
                case VariableDeclaration vd:
                    for (var i = 0; i < vd.Declarations.Count; i++)
                    {
                        var d = vd.Declarations[i];
                        var line = i == 0 ? vd.Location.Start.Line : d.Location.Start.Line;
                        foreach (var id in BoundNames(d.Id)) found.TryAdd(id, line);
                    }
                    continue;
            }
            var kids = n.ChildNodes.Where(c => c is not null).ToList();
            for (var i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
    }

    private static IEnumerable<string> BoundNames(Node? p)
    {
        switch (p)
        {
            case Identifier id: yield return id.Name; break;
            case ObjectPattern op:
                foreach (var prop in op.Properties)
                    foreach (var n in BoundNames(prop is ObjectProperty q ? q.Value : prop)) yield return n;
                break;
            case ArrayPattern ap:
                foreach (var el in ap.Elements) foreach (var n in BoundNames(el)) yield return n;
                break;
            case AssignmentPattern asp:
                foreach (var n in BoundNames(asp.Left)) yield return n;
                break;
            case RestElement re:
                foreach (var n in BoundNames(re.Argument)) yield return n;
                break;
        }
    }

    private static CacheFile ReadCache(IFileSystem fs, string path)
    {
        try
        {
            if (!fs.File.Exists(path)) return new CacheFile();
            var c = JsonSerializer.Deserialize<CacheFile>(fs.File.ReadAllText(path), Json);
            return c is { Version: CacheVersion } && Complete(c) ? c : new CacheFile();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new CacheFile();
        }
    }

    /// <summary>False when the JSON left a member null (a hand-edited or partly written cache); it is then rebuilt.</summary>
    private static bool Complete(CacheFile c) =>
        c.Files is not null && c.Files.Values.All(item => item is { Sha256: not null, Entries: not null }
            && item.Includes is not null && item.SpCalls is not null && item.SpCalls.All(x => x?.Name is not null) && item.Entries.All(e => e is { Name: not null, Declaration: not null }));

    private static void WriteCache(IFileSystem fs, string path, CacheFile cache)
    {
        try
        {
            var dir = fs.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) fs.Directory.CreateDirectory(dir);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            fs.File.WriteAllText(tmp, JsonSerializer.Serialize(cache, Json));
            fs.File.Move(tmp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation only.
        }
    }
}
