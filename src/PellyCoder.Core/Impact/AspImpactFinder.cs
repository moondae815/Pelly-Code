using PellyCoder.Core.Asp;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Impact;

/// <summary>
/// One ASP call of a stored procedure a sql task changes (A2 design §4.3).
/// </summary>
/// <param name="TaskIds">The not-done sql tasks that change <paramref name="Object"/>, joined by <c>", "</c>.</param>
/// <param name="Object">The seed's stored procedure (the sql task's object), not the name as the page writes it.</param>
/// <param name="Covered">
/// An asp task of the same file edits the call — its procedure holds it, or its line range does. A done task counts too: it
/// already fixed the call, so <c>apply --resume</c> does not warn about it again.
/// </param>
/// <param name="Includers">Pages that include the call's file (directly or through other includes); 0 for a page nobody includes.</param>
public sealed record AspImpactHit(string TaskIds, SqlObjectName Object, AspSpCallEntry Call, bool Covered, int Includers);

/// <summary>
/// SP → ASP impact (A2 design §4.3): the site index's stored procedure calls whose name is a seed's object. Separate from
/// <see cref="SqlImpactFinder"/>: it reads only files, so it runs without any database.
/// </summary>
public static class AspImpactFinder
{
    /// <summary>
    /// Every call of a seed's object, in index order (path, then line), each once however many seeds name the object.
    /// Names compare as SQL names do: case-insensitive, brackets already stripped, an omitted schema is <c>dbo</c>.
    /// </summary>
    /// <param name="tasks">The spec's tasks, asp task paths relative to the site root (as the index's paths are).</param>
    /// <param name="pageOf">
    /// The parsed page of an asp task, for a procedure target's exact lines (the <c>pelly asp --proc</c> rule); null when it
    /// cannot be read, and the call's enclosing procedure name is compared instead.
    /// </param>
    public static IReadOnlyList<AspImpactHit> Find(IReadOnlyList<SqlImpactSeed> seeds, IReadOnlyList<SpecTask> tasks, WebSiteIndexResult index,
        Func<SpecTask, AspPage?> pageOf)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(pageOf);

        var groups = new List<(SqlObjectName Object, List<string> Ids)>();
        foreach (var seed in seeds)
        {
            var group = groups.FirstOrDefault(g => g.Object.SameAs(seed.Object));
            if (group.Ids is null) groups.Add((seed.Object, [seed.TaskId]));
            else if (!group.Ids.Contains(seed.TaskId, StringComparer.Ordinal)) group.Ids.Add(seed.TaskId);
        }
        if (groups.Count == 0) return [];

        var aspTasks = tasks.Where(t => t.Kind == TaskKind.Asp).ToList();
        var ranges = new Dictionary<string, IReadOnlyList<(int Start, int End)>?>(StringComparer.Ordinal);
        IReadOnlyList<(int Start, int End)>? ProcedureLines(SpecTask task)
        {
            if (ranges.TryGetValue(task.Id, out var known)) return known;
            var page = pageOf(task);
            return ranges[task.Id] = page is null
                ? null
                : SpecChecker.MatchingProcedures(page, task.Symbol!).Select(p => (p.DeclLine, p.EndLine)).ToList();
        }

        bool Covers(SpecTask task, AspSpCallEntry call)
        {
            if (!string.Equals(Normalize(task.Path), Normalize(call.RelativePath), StringComparison.OrdinalIgnoreCase)) return false;
            if (task.Lines is { } lines) return call.Line >= lines.StartLine && call.Line <= lines.EndLine;
            if (task.Symbol is null) return false;
            if (ProcedureLines(task) is { } found) return found.Any(r => call.Line >= r.Start && call.Line <= r.End);
            return call.Procedure is { } proc
                && (proc.Equals(task.Symbol, StringComparison.OrdinalIgnoreCase)
                    || (!task.Symbol.Contains('.') && proc.EndsWith("." + task.Symbol, StringComparison.OrdinalIgnoreCase)));
        }

        var includers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<AspImpactHit>();
        foreach (var call in index.SpCalls)
        {
            var group = groups.FirstOrDefault(g => g.Object.SameAs(call.Object));
            if (group.Ids is null) continue;
            if (!includers.TryGetValue(call.RelativePath, out var count))
                includers[call.RelativePath] = count = index.IncludersOf(call.RelativePath).Count;
            hits.Add(new AspImpactHit(string.Join(", ", group.Ids), group.Object, call, aspTasks.Any(t => Covers(t, call)), count));
        }
        return hits;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
