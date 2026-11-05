using System.IO.Abstractions;
using System.Text.Json;
using Acornima;
using Acornima.Ast;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Web;

public sealed record JsIndexEntry(string Name, string RelativePath, int Line, string Declaration);

public sealed record WebSiteIndexResult(IReadOnlyList<JsIndexEntry> Entries, int Files, bool Truncated)
{
    private IReadOnlySet<string>? _names;
    public IReadOnlySet<string> Names => _names ??= new HashSet<string>(Entries.Select(e => e.Name), StringComparer.Ordinal);
}

/// <summary>Top-level function and variable names of the site's own JavaScript files, cached by file hash.</summary>
public static class WebSiteIndex
{
    private const int CacheVersion = 1;

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public Dictionary<string, CacheItem> Files { get; set; } = new();
    }

    private sealed class CacheItem
    {
        public string Sha256 { get; set; } = "";
        public List<CacheEntry> Entries { get; set; } = new();
    }

    private sealed class CacheEntry
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
        string cachePath, int maxFiles = 2000, long maxBytes = 1_048_576)
    {
        var old = ReadCache(fs, cachePath);
        var fresh = new CacheFile { Version = CacheVersion };
        var entries = new List<JsIndexEntry>();
        var files = 0;
        var truncated = false;

        var root = siteRoot.Replace('\\', '/').TrimEnd('/');
        List<(string Rel, string Full)> candidates;
        try
        {
            // A folder that cannot be read is skipped, not the whole index.
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            candidates = fs.Directory.EnumerateFiles(siteRoot, "*", options)
                .Where(p => p.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                .Select(p => (Rel: Relative(root, p), Full: p))
                .OrderBy(c => c.Rel, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new WebSiteIndexResult(entries, 0, false);
        }

        foreach (var (rel, full) in candidates)
        {
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
            if (filter.Classify(full, text).Verdict != WebFileVerdict.Ok) continue;
            if (files >= maxFiles) { truncated = true; break; }

            var sha = TextFileCodec.HashHex(bytes);
            List<CacheEntry> items;
            if (old.Files.TryGetValue(rel, out var hit) && hit.Sha256 == sha) items = hit.Entries;
            else items = Extract(text);
            fresh.Files[rel] = new CacheItem { Sha256 = sha, Entries = items };
            foreach (var it in items) entries.Add(new JsIndexEntry(it.Name, rel, it.Line, it.Declaration));
            files++;
        }

        WriteCache(fs, cachePath, fresh);
        return new WebSiteIndexResult(entries, files, truncated);
    }

    private static string Relative(string root, string path)
    {
        var p = path.Replace('\\', '/');
        return p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? p[(root.Length + 1)..] : p;
    }

    private static List<CacheEntry> Extract(string textLf)
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
            && item.Entries.All(e => e is { Name: not null, Declaration: not null }));

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
