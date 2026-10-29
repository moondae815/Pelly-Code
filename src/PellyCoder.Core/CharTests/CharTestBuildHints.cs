using System.Text.RegularExpressions;
using Diagnostic = PellyCoder.Core.Models.Diagnostic;

namespace PellyCoder.Core.CharTests;

/// <summary>
/// Extra lines for build feedback on the test file: a missing type (CS0246) declared in the workspace gets the <c>using</c> to add,
/// and a fake that misses interface members (CS0535, CS0738) gets that interface's full signature line from the dependencies.
/// </summary>
public static class CharTestBuildHints
{
    public const string ImplementAll = "unused ones: => throw new System.NotImplementedException()";

    private static readonly Regex MissingType = new(@"The type or namespace name '([A-Za-z_]\w*)", RegexOptions.CultureInvariant);
    private static readonly Regex MissingMember = new(@"does not implement interface member '([^'(]+)", RegexOptions.CultureInvariant);

    /// <param name="namespacesOf">The workspace namespaces that declare each name (names not found are absent).</param>
    public static IReadOnlyList<string> For(IReadOnlyList<Diagnostic> errors, string testRelativePath, string? dependencies,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, IReadOnlyList<string>>> namespacesOf)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inTest = errors.Where(e => string.Equals(e.File, testRelativePath, comparison)).ToList();
        var hints = new List<string>();

        var missing = inTest.Where(e => e.Code == "CS0246").Select(e => MissingType.Match(e.Message))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            var found = namespacesOf(missing);
            foreach (var name in missing)
                if (found.TryGetValue(name, out var namespaces) && namespaces.Count > 0)
                    hints.Add(name + ": add " + string.Join(" or ", namespaces.Select(ns => $"`using {ns};`")));
        }

        var lines = (dependencies ?? "").Split('\n');
        var interfaces = inTest.Where(e => e.Code is "CS0535" or "CS0738").Select(e => MissingMember.Match(e.Message))
            .Where(m => m.Success).Select(m => InterfaceName(m.Groups[1].Value)).OfType<string>().Distinct(StringComparer.Ordinal);
        foreach (var name in interfaces)
        {
            var line = lines.FirstOrDefault(l => Regex.IsMatch(l, @"\binterface\s+" + Regex.Escape(name) + @"\b"));
            if (line is not null) hints.Add($"Implement every member of {name} ({ImplementAll}): {line}");
        }
        return hints;
    }

    /// <summary>"IOrderService.SearchAsync" or "Shop.Api.Services.IRepo&lt;Order&gt;.Get" → the interface's simple name.</summary>
    private static string? InterfaceName(string member)
    {
        var text = Regex.Replace(member, "<[^<>]*>", "");
        text = Regex.Replace(text, "<[^<>]*>", "");
        var dot = text.LastIndexOf('.');
        if (dot <= 0) return null;
        var owner = text[..dot];
        var name = owner[(owner.LastIndexOf('.') + 1)..].Trim();
        return name.Length == 0 ? null : name;
    }
}
