namespace PellyCoder.Core.Web;

/// <summary>A JS function's name and parameter list, shown as <c>fnSearch(f, page)</c>.</summary>
public sealed record JsSignature(string Name, IReadOnlyList<string> Parameters)
{
    public string Display => $"{Name}({string.Join(", ", Parameters)})";

    public static JsSignature Of(JsFunction f) => new(f.QualifiedName, f.Parameters);
}

/// <summary>
/// Finds the edited target function among the patched file's functions (W3 design §4.2, 「같은 자리」): by its qualified name
/// and its position among same-named ones, or — when a signature change is allowed — as the one function with a new name
/// and the same owner.
/// </summary>
public static class JsTargetMatch
{
    public static JsFunction? Find(IReadOnlyList<JsFunction> originals, IReadOnlyList<JsFunction> patched, JsFunction target, bool allowRename)
    {
        var was = originals.Where(f => f.QualifiedName == target.QualifiedName).ToList();
        var same = patched.Where(f => f.QualifiedName == target.QualifiedName).ToList();
        var (expected, index) = JsChecks.Occurrence(was.Count, was.FindIndex(f => f.DeclLine == target.DeclLine));
        if (same.Count == expected) return same[index];
        // A rename leaves one fewer of the old name.
        return allowRename && same.Count == expected - 1 ? Renamed(originals, patched, target) : null;
    }

    /// <summary>The only patched function whose qualified name no original has and whose owner is the target's; null when not exactly one.</summary>
    public static JsFunction? Renamed(IReadOnlyList<JsFunction> originals, IReadOnlyList<JsFunction> patched, JsFunction target)
    {
        var names = originals.Select(o => o.QualifiedName).ToHashSet(StringComparer.Ordinal);
        var fresh = patched.Where(f => !names.Contains(f.QualifiedName) && f.Owner == target.Owner).ToList();
        return fresh.Count == 1 ? fresh[0] : null;
    }
}
