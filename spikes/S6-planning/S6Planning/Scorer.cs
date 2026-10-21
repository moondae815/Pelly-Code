namespace S6Planning;

/// <summary>Machine score of one output against one gold case (design §6.2).</summary>
public sealed record RunScore(
    string CaseId, char Category, string Arm, int Rep, bool Parsed, double Recall, double Precision,
    int Hallucinated, int Forbidden, int OrderViolations, bool GroupOk, bool KindOk, int Outside, bool ExampleCopy, bool Usable,
    int NearMiss = 0);

public static class Scorer
{
    public static RunScore Score(GoldCase gold, CorpusIndex index, string arm, int rep, string output)
    {
        var parsed = PlanParser.Parse(output);
        var tasks = parsed.Tasks;
        var keys = tasks.Select(TargetKey.Of).ToList();
        var outSet = keys.ToHashSet(StringComparer.Ordinal);
        var firstIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
            firstIndex.TryAdd(keys[i], i);

        var goldKey = gold.Tasks.ToDictionary(t => t.Id, TargetKey.Of);
        var required = gold.Required.Select(TargetKey.Of).ToHashSet(StringComparer.Ordinal);
        var accepted = gold.Tasks.Select(TargetKey.Of).ToHashSet(StringComparer.Ordinal);
        var forbidden = gold.ForbiddenKeys.ToHashSet(StringComparer.Ordinal);

        var recall = required.Count == 0 ? 1 : (double)required.Count(outSet.Contains) / required.Count;
        var precision = outSet.Count == 0 ? 0 : (double)outSet.Count(accepted.Contains) / outSet.Count;
        var hallucinated = tasks.Count(t => !index.Exists(t));
        var forbiddenHits = outSet.Count(forbidden.Contains);

        var orderViolations = gold.Order.Count(o =>
            firstIndex.TryGetValue(goldKey[o.Before], out var b) && firstIndex.TryGetValue(goldKey[o.After], out var a) && a < b);

        var groupOk = gold.Category != 'A' || gold.Groups.All(g =>
        {
            var groupKeys = g.Select(id => goldKey[id]).ToHashSet(StringComparer.Ordinal);
            var labels = tasks.Where(t => groupKeys.Contains(TargetKey.Of(t))).Select(t => t.Group).Distinct().ToList();
            return labels.Count <= 1 && labels.All(l => l is not null);
        });

        // Kind accuracy is judged per file: a task on a gold file with a kind no gold task on that file has.
        var goldKinds = gold.Tasks.GroupBy(t => FileKey(t.Path)).ToDictionary(g => g.Key, g => g.Select(t => t.Kind).ToHashSet(), StringComparer.Ordinal);
        var kindOk = tasks.All(t => !goldKinds.TryGetValue(FileKey(t.Path), out var kinds) || kinds.Contains(t.Kind));
        var nearMiss = tasks.Count(t => !index.Exists(t) && IsNearMiss(t, index));
        var outside = outSet.Count(k => !accepted.Contains(k));
        var exampleCopy = PlanPrompt.ExampleNames.Any(n => output.Contains(n, StringComparison.Ordinal));

        var usable = parsed.Parsed && recall >= 1 && hallucinated == 0 && forbiddenHits == 0 && orderViolations == 0
                     && groupOk && !exampleCopy && (gold.Category != 'D' || outside == 0);

        return new RunScore(gold.Id, gold.Category, arm, rep, parsed.Parsed, recall, precision, hallucinated, forbiddenHits,
            orderViolations, groupOk, kindOk, outside, exampleCopy, usable, nearMiss);
    }

    private static string FileKey(string path) => TargetKey.Of("sql", path, null);

    /// <summary>
    /// A missing target that exists after undoing one formatting slip: quotes/backticks, a space before '#', a generic
    /// argument list, or the wrong kind. Reported beside <see cref="RunScore.Hallucinated"/>; it does not change it.
    /// </summary>
    private static bool IsNearMiss(PlanTask t, CorpusIndex index)
    {
        static string Clean(string s) => s.Trim('`', '\'', '"');
        var path = Clean(t.Path);
        var symbol = t.Symbol is null ? null : Clean(t.Symbol);
        if (symbol is null && t.Request.StartsWith('#'))
            symbol = Clean(t.Request[1..].Split(' ', 2)[0]);
        if (symbol is not null)
        {
            var lt = symbol.IndexOf('<');
            if (lt > 0)
                symbol = symbol[..lt];
        }
        var other = t.Kind == "cs" ? "sql" : "cs";
        return index.Exists(t.Kind, path, symbol) || index.Exists(other, path, symbol);
    }
}

public enum VerdictKind { PremiseRejected, Partial, PremiseConfirmed }

/// <summary>Decision table fixed before the run (design §7), applied to one arm's scores.</summary>
public static class Verdict
{
    public static VerdictKind Decide(IReadOnlyList<RunScore> armScores)
    {
        if (armScores.Count == 0)
            throw new ArgumentException("No scores to judge.", nameof(armScores));
        var rate = (double)armScores.Count(s => s.Usable) / armScores.Count;
        var hallucRepRate = (double)armScores.Count(s => s.Hallucinated > 0) / armScores.Count;
        if (rate < 0.5 || hallucRepRate > 0.1)
            return VerdictKind.PremiseConfirmed;
        var everyCategory = armScores.GroupBy(s => s.Category).All(g => (double)g.Count(s => s.Usable) / g.Count() >= 0.6);
        return rate >= 0.8 && everyCategory && armScores.Sum(s => s.Hallucinated) == 0
            ? VerdictKind.PremiseRejected
            : VerdictKind.Partial;
    }
}
