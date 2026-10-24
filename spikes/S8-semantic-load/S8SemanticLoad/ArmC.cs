using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using S6Planning;

namespace S8SemanticLoad;

/// <summary>One category-A case: S6 arm C by name (syntax) and by symbol (semantic), scored by the S6 scorer.</summary>
public sealed record ArmCRow(
    string CaseId, string Scope, bool Measured, double CallRate,
    RunScore Syntactic, RunScore? Semantic, string SyntacticPlan, string? SemanticPlan);

/// <summary>
/// S8 step 2: re-measure S6 arm C with semantic search on the corpus. A scope whose load has no real type information
/// (call rate below 99% or compile errors — e.g. Legacy without Visual Studio MSBuild) is not scored semantically.
/// </summary>
public static class ArmC
{
    public static async Task<IReadOnlyList<ArmCRow>> RunAsync(string repoRoot, CancellationToken ct)
    {
        var spike = Path.Combine(repoRoot, "spikes", "S6-planning");
        var cases = Path.Combine(spike, "cases");
        var corpus = Path.Combine(repoRoot, "eval", "corpus");
        var refusal = ApprovalGate.Check(cases, Runner.GoldCommit(spike), Runner.CasesDirty(spike));
        if (refusal is not null)
            throw new InvalidOperationException("gold not approved: " + refusal);

        var rows = new List<ArmCRow>();
        var loaded = new Dictionary<string, (Solution Solution, double Rate, bool Healthy, MSBuildWorkspace Workspace)>();
        try
        {
            foreach (var gold in GoldLoader.LoadAll(cases).Where(c => c.Category == 'A'))
            {
                var scope = gold.Scope[0];
                if (!loaded.TryGetValue(scope, out var entry))
                {
                    var sln = Path.Combine(corpus, scope, scope + ".sln");
                    var ws = MSBuildWorkspace.Create();
                    var solution = await ws.OpenSolutionAsync(sln, cancellationToken: ct);
                    var census = ProjectCensusReader.Read(sln);
                    var calls = await SemanticMetrics.MeasureCallsAsync(solution, ct: ct);
                    var health = await SemanticMetrics.MeasureHealthAsync(solution, census, ct);
                    var healthy = calls.Total > 0 && calls.Rate >= Verdict.MinCallRate && health.All(h => h.CompileErrors == 0);
                    entry = (solution, calls.Rate, healthy, ws);
                    loaded[scope] = entry;
                }

                var index = CorpusIndexBuilder.Build(corpus, gold.Scope);
                var syntacticPlan = CallerFinder.Plan(corpus, gold.Scope, gold.Tasks[0]);
                var syntactic = Scorer.Score(gold, index, "C", 1, syntacticPlan);
                string? semanticPlan = null;
                RunScore? semantic = null;
                if (entry.Healthy)
                {
                    semanticPlan = await SemanticCallerFinder.PlanAsync(entry.Solution, corpus, gold.Tasks[0], ct);
                    semantic = Scorer.Score(gold, index, "CS", 1, semanticPlan);
                }
                rows.Add(new ArmCRow(gold.Id, scope, entry.Healthy, entry.Rate, syntactic, semantic, syntacticPlan, semanticPlan));
            }
        }
        finally
        {
            foreach (var (_, _, _, ws) in loaded.Values) ws.Dispose();
        }
        return rows;
    }

    public static string Summary(IReadOnlyList<ArmCRow> rows)
    {
        static string S(RunScore? r) => r is null ? "측정 불가"
            : $"재현율 {r.Recall:F2} · 정밀도 {r.Precision:F2} · 금지 {r.Forbidden} · 지어냄 {r.Hallucinated} · {(r.Usable ? "쓸 만함" : "쓸 만하지 않음")}";
        var lines = new List<string> { "S6 범주 A 코드 비교군: 이름(구문) 대 심볼(의미)" };
        foreach (var r in rows)
        {
            lines.Add($"{r.CaseId} ({r.Scope}, 호출 해석률 {r.CallRate:P1})");
            lines.Add($"  이름: {S(r.Syntactic)}");
            lines.Add($"  심볼: {S(r.Semantic)}");
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
