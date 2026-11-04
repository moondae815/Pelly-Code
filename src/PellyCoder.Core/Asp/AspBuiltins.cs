namespace PellyCoder.Core.Asp;

/// <summary>
/// VBScript 5.8 built-in names plus the Classic ASP objects, loaded from the embedded <c>vbscript-names.txt</c>
/// (sections <c>[keywords]</c>, <c>[functions]</c>, <c>[constants]</c>, <c>[asp-objects]</c>, <c>[ado-constants]</c>).
/// </summary>
public sealed class AspBuiltins
{
    private const string ResourceName = "PellyCoder.Core.Asp.Resources.vbscript-names.txt";

    private readonly HashSet<string> _keywords = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ado = new(StringComparer.OrdinalIgnoreCase);

    public static AspBuiltins Default { get; } = Load();

    public bool IsKeyword(string name) => _keywords.Contains(name);

    /// <summary>True for keywords, functions, constants and ASP objects; ADO constants only when <paramref name="adoConstants"/>.</summary>
    public bool IsKnown(string name, bool adoConstants) =>
        _known.Contains(name) || (adoConstants && _ado.Contains(name));

    private static AspBuiltins Load()
    {
        using var stream = typeof(AspBuiltins).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}");
        using var reader = new StreamReader(stream);
        var result = new AspBuiltins();
        string section = "";
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line[0] == '[' && line[^1] == ']') { section = line[1..^1]; continue; }
            switch (section)
            {
                case "keywords": result._keywords.Add(line); result._known.Add(line); break;
                case "ado-constants": result._ado.Add(line); break;
                default: result._known.Add(line); break;
            }
        }
        return result;
    }
}
