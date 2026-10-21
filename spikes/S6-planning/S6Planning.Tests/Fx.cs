using S6Planning;

/// <summary>In-memory gold cases, indexes and score lists for scorer and report tests.</summary>
public static class Fx
{
    public static GoldCase Gold(char category, string tasks, string[]? optional = null,
        (string, string)[]? order = null, string[][]? groups = null, string[]? forbidden = null) =>
        new("fx", category, new[] { "Fx" }, "approved", "요청", PlanParser.Parse(tasks).Tasks,
            (optional ?? []).ToHashSet(), order ?? [], (groups ?? []).Select(g => (IReadOnlyList<string>)g).ToList(),
            forbidden ?? [], "");

    /// <summary>Files as "path:kind:Member1,Member2".</summary>
    public static CorpusIndex Index(params string[] files) =>
        new(files.Select(f =>
        {
            var parts = f.Split(':');
            var members = parts.Length > 2 && parts[2].Length > 0
                ? parts[2].Split(',').Select(m => new IndexMember("T", m, m)).ToList()
                : new List<IndexMember>();
            return new IndexFile(parts[0], parts[1], members);
        }).ToList());

    /// <summary>
    /// <paramref name="total"/> scores spread evenly over categories A-D. <paramref name="perCategory"/> overrides how
    /// many are usable per category; otherwise <paramref name="usable"/> is spread evenly. Hallucinating reps are
    /// taken from non-usable reps, each carrying an equal share of <paramref name="hallucTotal"/>.
    /// </summary>
    public static IReadOnlyList<RunScore> Scores(int usable, int total, int hallucTotal = 0, int hallucReps = 0, int[]? perCategory = null)
    {
        var per = total / 4;
        var usableBy = perCategory ?? Enumerable.Range(0, 4).Select(i => usable / 4 + (i < usable % 4 ? 1 : 0)).ToArray();
        var list = new List<RunScore>();
        var hallucLeft = hallucReps;
        for (var c = 0; c < 4; c++)
            for (var i = 0; i < per; i++)
            {
                var ok = i < usableBy[c];
                var halluc = 0;
                if (!ok && hallucLeft > 0)
                {
                    halluc = hallucTotal / hallucReps + (hallucLeft <= hallucTotal % hallucReps ? 1 : 0);
                    hallucLeft--;
                }
                list.Add(new RunScore($"c{c}", "ABCD"[c], "A", i + 1, true, ok ? 1 : 0.5, 1, halluc, 0, 0, true, true, 0, false, ok));
            }
        return list;
    }
}
