using PellyCoder.Core.Asp;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Impact;

/// <summary>A JS function a not-done js task targets by name (W3 design §4.4). <paramref name="RelativePath"/> is site-relative.</summary>
public sealed record JsImpactSeed(string TaskId, string RelativePath, string Function, int Line);

/// <summary>
/// One call or reference of a seed's function (W3 design §4.3·§4.4). <paramref name="CoveredBy"/> are the js and asp tasks of the
/// same file whose target holds it (an attribute call only an asp task can cover). <paramref name="PageDoesNotLoadSeedFile"/>:
/// the call is on a page that neither loads the seed's <c>.js</c> by <c>&lt;script src&gt;</c> nor through its includes.
/// </summary>
public sealed record JsImpactHit(JsImpactSeed Seed, string RelativePath, JsCallSite Call, IReadOnlyList<string> CoveredBy, bool PageDoesNotLoadSeedFile)
{
    public bool Covered => CoveredBy.Count > 0;
}

/// <summary>JS function → callers in the site index (W3 design §4.3), matched by name: a same-named unrelated function is an accepted false hit.</summary>
public static class JsImpactFinder
{
    /// <param name="tasks">The spec's tasks with site-relative paths.</param>
    /// <param name="readTextLf">A site-relative file's text (LF), null when it cannot be read.</param>
    public static IReadOnlyList<JsImpactHit> Find(IReadOnlyList<JsImpactSeed> seeds, IReadOnlyList<SpecTask> tasks, WebSiteIndexResult index,
        Func<string, string?> readTextLf)
    {
        var hits = new List<JsImpactHit>();
        var texts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Text(string rel) => texts.TryGetValue(rel, out var t) ? t : texts[rel] = readTextLf(rel);
        var ranges = new Dictionary<string, IReadOnlyList<(int Start, int End)>>(StringComparer.Ordinal);

        foreach (var seed in seeds)
        {
            var found = index.JsCalls.Where(c => c.Call.Name == seed.Function).ToList();
            found.AddRange(References(index, seed.Function, Text));
            foreach (var (rel, call) in found.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ThenBy(f => f.Call.Line))
            {
                if (!Same(rel, seed.RelativePath) && DeclaresOwn(index, rel, seed.Function, seed.RelativePath)) continue;
                var covered = tasks.Where(t => Covers(t, rel, call, Text, ranges)).Select(t => t.Id).ToList();
                hits.Add(new JsImpactHit(seed, rel, call, covered, NotLoaded(index, rel, seed.RelativePath)));
            }
        }
        return hits;
    }

    /// <summary>References (the name passed on without a call) in every indexed file whose text holds the name.</summary>
    internal static IEnumerable<(string RelativePath, JsCallSite Call)> References(WebSiteIndexResult index, string name, Func<string, string?> text)
    {
        foreach (var rel in Files(index))
        {
            if (text(rel) is not { } t || !t.Contains(name.Split('.')[^1], StringComparison.Ordinal)) continue;
            if (rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var r in JsCallFinder.References(t, name, JsCallSource.Js)) yield return (rel, r);
                continue;
            }
            var page = AspPage.Parse(t);
            for (var i = 0; i < page.ClientBlocks.Count; i++)
                if (PageBlockView.IsJavaScriptBlock(page.ClientBlocks[i]))
                    foreach (var r in JsCallFinder.References(PageBlockView.Build(page, t, i).Text, name, JsCallSource.PageScript))
                        yield return (rel, r);
            foreach (var (_, r) in JsCallFinder.InAttributes(t, name)) yield return (rel, r);
        }
    }

    /// <summary>Every file the index saw: <c>.js</c> files and pages, site-relative.</summary>
    internal static IEnumerable<string> Files(WebSiteIndexResult index) =>
        index.Entries.Select(e => e.RelativePath)
            .Concat(index.JsCalls.Select(c => c.RelativePath))
            .Concat(index.Includes.Keys).Concat(index.ScriptSources.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal);

    /// <summary>
    /// The file — or, for a page, the page and its includes — declares a top-level function of that name itself, other than in
    /// the seed's own file: a page that includes the <c>.inc</c> declaring the seed calls the seed, not a function of its own.
    /// </summary>
    internal static bool DeclaresOwn(WebSiteIndexResult index, string rel, string function, string seedPath)
    {
        var scope = rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? [rel] : WithIncludes(index, rel);
        return index.Entries.Any(e => e.Name == function && !Same(e.RelativePath, seedPath)
            && scope.Contains(e.RelativePath, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The page loads none of: its own, its includes' and its includers' <c>&lt;script src&gt;</c> files is the seed file.</summary>
    private static bool NotLoaded(WebSiteIndexResult index, string rel, string seedPath)
    {
        if (!seedPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) return false;
        var pages = WithIncludes(index, rel).Concat(index.IncludersOf(rel).SelectMany(p => WithIncludes(index, p)));
        return !pages.Any(p => index.ScriptSources.TryGetValue(p, out var srcs) && srcs.Any(s => Same(s, seedPath)));
    }

    /// <summary>The page and the files it includes, directly or through other includes (depth limit 5).</summary>
    private static List<string> WithIncludes(WebSiteIndexResult index, string rel)
    {
        var result = new List<string> { rel };
        var frontier = new List<string> { rel };
        for (var depth = 0; depth < 5 && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var f in frontier)
                if (index.Includes.TryGetValue(f, out var incs))
                    foreach (var inc in incs)
                        if (!result.Contains(inc, StringComparer.OrdinalIgnoreCase)) { result.Add(inc); next.Add(inc); }
            frontier = next;
        }
        return result;
    }

    private static bool Covers(SpecTask task, string rel, JsCallSite call, Func<string, string?> text,
        Dictionary<string, IReadOnlyList<(int Start, int End)>> ranges)
    {
        if (task.Kind is not (TaskKind.Js or TaskKind.Asp)) return false;
        if (!Same(task.Path, rel)) return false;
        if (call.Source == JsCallSource.Attribute && task.Kind != TaskKind.Asp) return false;
        if (task.Lines is { } lines) return call.Line >= lines.StartLine && call.Line <= lines.EndLine;
        if (task.Symbol is null) return false;
        if (!ranges.TryGetValue(task.Id, out var found))
        {
            found = text(rel) is not { } t ? []
                : task.Kind == TaskKind.Asp
                    ? SpecChecker.MatchingProcedures(AspPage.Parse(t), task.Symbol).Select(p => (p.DeclLine, p.EndLine)).ToList()
                    : FunctionRanges(t, !rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase), task.Symbol);
            ranges[task.Id] = found;
        }
        return found.Any(r => call.Line >= r.Start && call.Line <= r.End);
    }

    /// <summary>The line ranges of the functions named <paramref name="name"/> (qualified or bare), page lines for a page.</summary>
    internal static IReadOnlyList<(int Start, int End)> FunctionRanges(string textLf, bool isPage, string name) =>
        WebTargetLookup.JsPieces(textLf, isPage).SelectMany(WebTargetLookup.Functions)
            .Where(f => f.QualifiedName == name || f.Name == name).Select(f => (f.StartLine, f.EndLine)).ToList();

    private static bool Same(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
