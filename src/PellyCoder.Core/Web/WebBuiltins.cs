using Acornima;

namespace PellyCoder.Core.Web;

/// <summary>
/// Built-in tables for JS/CSS checks, loaded from embedded resources: browser globals, ES API levels,
/// jQuery API added/removed versions and CSS property names. One entry per line, '#' comments.
/// </summary>
public sealed class WebBuiltins
{
    private const string Prefix = "PellyCoder.Core.Web.Resources.";

    private readonly HashSet<string> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EcmaVersion> _es = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Version? Added, Version? Removed)> _jq = new(StringComparer.Ordinal);
    private readonly HashSet<string> _css = new(StringComparer.Ordinal);

    public static WebBuiltins Default { get; } = Load();

    public bool IsGlobal(string name) => _globals.Contains(name);

    /// <summary>Level of an API key: identifier, "Obj.member" or ".member". Null when unknown.</summary>
    public EcmaVersion? EsApiLevel(string key) => _es.TryGetValue(key, out var v) ? v : null;

    public (Version? Added, Version? Removed)? Jquery(string member) =>
        _jq.TryGetValue(member, out var v) ? v : null;

    public bool IsCssProperty(string nameLower) => _css.Contains(nameLower);

    private static IEnumerable<string[]> Rows(string file)
    {
        var name = Prefix + file;
        using var stream = typeof(WebBuiltins).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            yield return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        }
    }

    private static Version? Ver(string s) => s == "-" ? null : Version.Parse(s);

    private static WebBuiltins Load()
    {
        var b = new WebBuiltins();
        foreach (var r in Rows("browser-globals.txt")) b._globals.Add(r[0]);
        foreach (var r in Rows("es-apis.txt"))
            b._es[r[0]] = JsLevels.FromSetting(r[1]) ?? throw new InvalidOperationException($"Bad ES level in es-apis.txt: {r[1]}");
        foreach (var r in Rows("jquery-apis.txt")) b._jq[r[0]] = (Ver(r[1]), Ver(r[2]));
        foreach (var r in Rows("css-properties.txt")) b._css.Add(r[0]);
        return b;
    }
}
