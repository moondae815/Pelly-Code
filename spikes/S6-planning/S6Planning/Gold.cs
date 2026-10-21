using System.Text;
using System.Text.Json;

namespace S6Planning;

/// <summary>
/// Identity of a task target for scoring. Only two spellings are normalized (path separators / leading "./" and
/// member qualification / trailing parentheses); anything else, such as an "eval/corpus/" prefix, stays as written
/// and therefore counts as an invented target.
/// </summary>
public static class TargetKey
{
    public static string Of(PlanTask t) => Of(t.Kind, t.Path, t.Symbol);

    public static string Of(string kind, string path, string? symbol)
    {
        var p = path.Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        p = p.ToLowerInvariant();
        return kind == "sql" ? p : p + "#" + Member(symbol);
    }

    /// <summary>Last dotted segment with any trailing parameter list removed.</summary>
    public static string Member(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol))
            return "";
        var s = symbol;
        var paren = s.IndexOf('(');
        if (paren >= 0)
            s = s[..paren];
        var dot = s.LastIndexOf('.');
        return dot >= 0 ? s[(dot + 1)..] : s;
    }
}

public sealed record GoldCase(
    string Id, char Category, IReadOnlyList<string> Scope, string Status, string Request,
    IReadOnlyList<PlanTask> Tasks, IReadOnlySet<string> Optional,
    IReadOnlyList<(string Before, string After)> Order, IReadOnlyList<IReadOnlyList<string>> Groups,
    IReadOnlyList<string> Forbidden, string Dir)
{
    public IEnumerable<PlanTask> Required => Tasks.Where(t => !Optional.Contains(t.Id));

    /// <summary>Keys of <see cref="Forbidden"/> entries ("path#member", always C#).</summary>
    public IEnumerable<string> ForbiddenKeys => Forbidden.Select(f =>
    {
        var i = f.IndexOf('#');
        return TargetKey.Of("cs", i < 0 ? f : f[..i], i < 0 ? null : f[(i + 1)..]);
    });
}

public static class GoldLoader
{
    public static IReadOnlyList<GoldCase> LoadAll(string casesDir) =>
        Directory.GetDirectories(casesDir)
            .Where(d => File.Exists(Path.Combine(d, "gold.md")))
            .Order(StringComparer.Ordinal)
            .Select(Load)
            .ToList();

    public static GoldCase Load(string caseDir)
    {
        var text = File.ReadAllText(Path.Combine(caseDir, "gold.md"), Encoding.UTF8);
        var (front, body) = SplitFrontMatter(text, caseDir);
        var plan = PlanParser.Parse(body);
        if (plan.InvalidLines.Count > 0)
            throw new InvalidDataException($"{caseDir}: invalid task line: {plan.InvalidLines[0]}");

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseDir, "gold.constraints.json"), Encoding.UTF8));
        var root = json.RootElement;
        var optional = Strings(root, "optional").ToHashSet(StringComparer.Ordinal);
        var order = Arrays(root, "order").Select(a => (a[0], a[1])).ToList();
        var groups = Arrays(root, "groups").Select(a => (IReadOnlyList<string>)a).ToList();
        var forbidden = Strings(root, "forbidden").ToList();

        var ids = plan.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in optional.Concat(order.SelectMany(o => new[] { o.Item1, o.Item2 })).Concat(groups.SelectMany(g => g)))
            if (!ids.Contains(id))
                throw new InvalidDataException($"{caseDir}: constraint refers to missing task {id}");
        if (order.Any(o => o.Item1 == o.Item2))
            throw new InvalidDataException($"{caseDir}: order pair must name two tasks");

        string Field(string k) => front.TryGetValue(k, out var v) ? v : throw new InvalidDataException($"{caseDir}: missing '{k}'");
        var category = Field("category");
        if (category.Length != 1 || !"ABCD".Contains(category[0]))
            throw new InvalidDataException($"{caseDir}: category must be A-D");

        return new GoldCase(Field("id"), category[0], ParseList(Field("scope")), Field("status"), Unquote(Field("request")),
            plan.Tasks, optional, order, groups, forbidden, caseDir);
    }

    private static (Dictionary<string, string> Front, string Body) SplitFrontMatter(string text, string caseDir)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0] != "---")
            throw new InvalidDataException($"{caseDir}: gold.md must start with front matter");
        var end = Array.IndexOf(lines, "---", 1);
        if (end < 0)
            throw new InvalidDataException($"{caseDir}: unterminated front matter");
        var front = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in lines[1..end])
        {
            var i = l.IndexOf(':');
            if (i > 0)
                front[l[..i].Trim()] = l[(i + 1)..].Trim();
        }
        return (front, string.Join('\n', lines[(end + 1)..]));
    }

    private static IReadOnlyList<string> ParseList(string v) =>
        v.Trim().TrimStart('[').TrimEnd(']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Unquote(string v)
    {
        if (v.Length < 2 || v[0] != '"' || v[^1] != '"')
            return v;
        return v[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    private static IEnumerable<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) ? e.EnumerateArray().Select(x => x.GetString()!) : [];

    private static IEnumerable<string[]> Arrays(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) ? e.EnumerateArray().Select(a => a.EnumerateArray().Select(x => x.GetString()!).ToArray()) : [];
}
