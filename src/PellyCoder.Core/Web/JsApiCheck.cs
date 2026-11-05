using Acornima;

namespace PellyCoder.Core.Web;

/// <summary>Warns about ES APIs above the file's level and jQuery methods outside the site's jQuery version.</summary>
public static class JsApiCheck
{
    public static IReadOnlyList<string> NewWarnings(JsDocument original, JsDocument patched, EcmaVersion target, Version? jquery, WebBuiltins b)
    {
        var before = Collect(original, target, jquery, b).GroupBy(w => w.Text).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<string>();
        foreach (var g in Collect(patched, target, jquery, b).GroupBy(w => w.Text))
        {
            if (g.Count() <= before.GetValueOrDefault(g.Key)) continue;
            result.Add($"{g.Key}(줄 {g.Min(w => w.Line)})");
        }
        return result;
    }

    private static IEnumerable<(string Text, int Line)> Collect(JsDocument doc, EcmaVersion target, Version? jquery, WebBuiltins b)
    {
        var uses = new List<(string Callee, int Line, bool Free)>();
        foreach (var c in doc.Calls) uses.Add((c.Callee, c.Line, false));
        foreach (var n in doc.FreeNames) uses.Add((n.Name, n.Line, true));
        foreach (var (callee, line, free) in uses)
        {
            if (callee.Length == 0) continue;
            var dot = callee.LastIndexOf('.');
            var member = dot >= 0 ? callee[(dot + 1)..] : null;
            var keys = new List<string> { callee };
            if (dot > 0) keys.Add("." + member);
            foreach (var key in keys)
            {
                if (b.EsApiLevel(key) is { } level && JsLevels.Compare(level, target) > 0)
                {
                    yield return ($"`{key}`는 {JsLevels.Name(level)} 기능입니다(이 파일 기준 {JsLevels.Name(target)})", line);
                    break;
                }
            }
            if (free || jquery is null || member is null) continue;
            if (dot > 0 && callee[..dot] is not ("$" or "jQuery")) continue;
            if (b.Jquery(member) is not { } jq) continue;
            if (jq.Added is { } added && jquery < added)
                yield return ($"`.{member}`은 jQuery {added}부터 있습니다(사이트 기준 {jquery})", line);
            else if (jq.Removed is { } removed && jquery >= removed)
                yield return ($"`.{member}`은 jQuery {removed}에서 없어졌습니다(사이트 기준 {jquery})", line);
        }
    }
}
