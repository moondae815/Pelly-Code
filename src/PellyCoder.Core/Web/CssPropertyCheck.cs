using System.Text.RegularExpressions;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Web;

/// <summary>Warns about unknown CSS property names and about declarations a later rule overrides.</summary>
public static class CssPropertyCheck
{
    private static readonly Regex VendorPrefix = new(@"^-[a-z]+-", RegexOptions.Compiled);

    public static IReadOnlyList<string> NewUnknown(CssSheet original, CssSheet patched, WebBuiltins b)
    {
        var before = Unknown(original, original, b).GroupBy(u => u.Key).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<(string Text, int Line)>();
        foreach (var g in Unknown(patched, original, b).GroupBy(u => u.Key))
        {
            var extra = g.Count() - before.GetValueOrDefault(g.Key);
            if (extra <= 0) continue;
            foreach (var u in g.OrderBy(u => u.Line).TakeLast(extra)) result.Add(($"알 수 없는 CSS 속성: {u.Shown}", u.Line));
        }
        return result.OrderBy(r => r.Line).Select(r => $"{r.Text}(줄 {r.Line})").ToList();
    }

    private static IEnumerable<(string Key, string Shown, int Line)> Unknown(CssSheet sheet, CssSheet baseline, WebBuiltins b)
    {
        foreach (var rule in sheet.Rules)
        foreach (var d in rule.Declarations)
        {
            var name = d.Property.ToLowerInvariant();
            if (baseline.UsesIeHacks)
            {
                name = name.TrimStart('*', '_');
                if (name == "filter") continue;
            }
            if (name.StartsWith("--", StringComparison.Ordinal)) continue;
            if (baseline.UsesVendorPrefixes && VendorPrefix.IsMatch(name)) continue;
            if (b.IsCssProperty(name)) continue;
            yield return (name, d.Property, d.Line);
        }
    }

    public static IReadOnlyList<string> OverrideNotices(CssSheet patched, TextSpanInfo span)
    {
        var rules = patched.Rules;
        var result = new List<string>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.IsAtRule || rule.EndLine < span.StartLine || rule.StartLine > span.EndLine) continue;
            foreach (var d in rule.Declarations)
            {
                for (var j = i + 1; j < rules.Count; j++)
                {
                    var later = rules[j];
                    if (later.IsAtRule || later.Container != rule.Container) continue;
                    var shared = rule.Selectors.FirstOrDefault(s => later.Selectors.Contains(s));
                    if (shared is null) continue;
                    if (!later.Declarations.Any(x => string.Equals(x.Property, d.Property, StringComparison.OrdinalIgnoreCase))) continue;
                    result.Add($"줄 {d.Line}의 `{d.Property}`은 줄 {later.StartLine}의 `{shared}` 규칙이 덮어씁니다");
                }
            }
        }
        return result;
    }
}
