namespace PellyCoder.Core.Web;

public sealed record JsUnknownName(string Name, int Line);

/// <summary>Warns about free names the site does not define. Counts only what is new compared with the original.</summary>
public static class JsNameCheck
{
    public static IReadOnlyList<JsUnknownName> NewUnknown(JsDocument original, JsDocument patched, Func<string, bool> isKnown)
    {
        var before = original.FreeNames.Where(u => !isKnown(u.Name))
            .GroupBy(u => u.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var result = new List<JsUnknownName>();
        foreach (var g in patched.FreeNames.Where(u => !isKnown(u.Name)).GroupBy(u => u.Name, StringComparer.Ordinal))
        {
            var extra = g.Count() - before.GetValueOrDefault(g.Key);
            if (extra <= 0) continue;
            foreach (var u in g.OrderBy(u => u.Line).TakeLast(extra)) result.Add(new JsUnknownName(u.Name, u.Line));
        }
        return result.OrderBy(r => r.Line).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    public static Func<string, bool> KnownSet(WebBuiltins b, bool jquery, IReadOnlySet<string> siteNames, IReadOnlyList<string> globals)
    {
        var extra = new HashSet<string>(globals, StringComparer.Ordinal);
        return name => b.IsGlobal(name) || (jquery && (name == "$" || name == "jQuery"))
            || siteNames.Contains(name) || extra.Contains(name);
    }
}
