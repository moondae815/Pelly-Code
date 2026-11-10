using PellyCoder.Core.Models;

namespace PellyCoder.Core.Web;

/// <summary>A JS function whose name or parameters an applied task changed (W3 design §4.5).</summary>
public sealed record JsSignatureChange(JsSignature Before, JsSignature After)
{
    public bool Renamed => Before.Name != After.Name;

    public bool CountChanged => Before.Parameters.Count != After.Parameters.Count;

    /// <summary>The old parameters are kept in order and new ones only follow them: old calls pass <c>undefined</c> for those.</summary>
    public bool OnlyAppended => After.Parameters.Count > Before.Parameters.Count && After.Parameters.Take(Before.Parameters.Count).SequenceEqual(Before.Parameters);

    public string Display => $"{Before.Display} → {After.Display}";
}

/// <summary>Compares a file's (or page's) JS functions before and after an edit.</summary>
public static class JsSignatureChanges
{
    /// <summary>The named target function's change, found again by <see cref="JsTargetMatch.Find"/> with renames allowed; empty when it kept its signature.</summary>
    public static IReadOnlyList<JsSignatureChange> ForFunction(string beforeTextLf, string afterTextLf, bool isPage, string name)
    {
        var originals = All(beforeTextLf, isPage);
        var targets = originals.Where(f => f.QualifiedName == name || f.Name == name).ToList();
        if (targets.Count != 1) return [];
        var now = JsTargetMatch.Find(originals, All(afterTextLf, isPage), targets[0], allowRename: true);
        return now is null ? [] : Changed(targets[0], now);
    }

    /// <summary>
    /// The changes of the functions declared in <paramref name="lines"/> (original lines). Only functions outside any other
    /// function are compared. The ones between the unchanged functions before and after the range are paired by qualified name
    /// first; what is left is paired as a rename only when exactly one function is left on each side, else not compared — a
    /// helper added or removed never reads as a rename.
    /// </summary>
    public static IReadOnlyList<JsSignatureChange> InLines(string beforeTextLf, string afterTextLf, bool isPage, TextSpanInfo lines)
    {
        var before = Outermost(All(beforeTextLf, isPage));
        var after = Outermost(All(afterTextLf, isPage));
        var inside = before.Where(f => f.DeclLine >= lines.StartLine && f.DeclLine <= lines.EndLine).ToList();
        if (inside.Count == 0) return [];
        var head = before.Count(f => f.DeclLine < lines.StartLine);
        var tail = before.Count(f => f.DeclLine > lines.EndLine);
        var middle = after.Skip(head).Take(Math.Max(0, after.Count - head - tail)).ToList();

        var result = new List<JsSignatureChange>();
        var leftBefore = new List<JsFunction>();
        foreach (var was in inside)
        {
            var same = middle.FirstOrDefault(f => f.QualifiedName == was.QualifiedName);
            if (same is null) { leftBefore.Add(was); continue; }
            middle.Remove(same);
            result.AddRange(Changed(was, same));
        }
        if (leftBefore.Count == 1 && middle.Count == 1) result.AddRange(Changed(leftBefore[0], middle[0]));
        return result;
    }

    /// <summary>The functions no other function of the list encloses.</summary>
    private static List<JsFunction> Outermost(List<JsFunction> all) =>
        all.Where(f => !all.Any(g => g != f && g.StartLine <= f.StartLine && g.EndLine >= f.EndLine
            && (g.StartLine < f.StartLine || g.EndLine > f.EndLine))).ToList();

    private static IReadOnlyList<JsSignatureChange> Changed(JsFunction was, JsFunction now) =>
        was.QualifiedName == now.QualifiedName && was.Parameters.SequenceEqual(now.Parameters)
            ? []
            : [new JsSignatureChange(JsSignature.Of(was), JsSignature.Of(now))];

    private static List<JsFunction> All(string textLf, bool isPage) =>
        WebTargetLookup.JsPieces(textLf, isPage).SelectMany(WebTargetLookup.Functions).OrderBy(f => f.DeclLine).ToList();
}
