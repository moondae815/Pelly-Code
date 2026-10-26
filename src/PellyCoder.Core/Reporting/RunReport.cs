using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Reporting;

public sealed record Spread(double Median, double P90);

public sealed record ParseRate(int Ok, int Total)
{
    public double? Rate => Total == 0 ? null : (double)Ok / Total;
}

/// <summary>Aggregated KPIs. Spreads are null when no run has a value.</summary>
public sealed record RunReportData(
    int RunCount,
    IReadOnlyDictionary<EditKind, int> RunsByKind,
    int SuccessCount,
    double SuccessRate,
    IReadOnlyDictionary<int, int> ExitCodes,
    Spread? Ttft,
    int TtftSamples,
    Spread? Queue,
    Spread? TaskSeconds,
    ParseRate Parse,
    int ThinkMax,
    int ThinkTruncatedCount,
    IReadOnlyDictionary<string, int> FailureKinds,
    IReadOnlyDictionary<RunPhase, double> MeanPhaseSeconds,
    IReadOnlyDictionary<int, int> AttemptDistribution,
    TestFirstCounts? TestFirst = null);

/// <summary>Test-first KPIs per run: Met of Judged (acceptance criteria met), RedWarned of RedChecked (baseline already green).</summary>
public sealed record TestFirstCounts(int Met, int Judged, int RedWarned, int RedChecked, int BreaksExcluded = 0);

public static class RunReport
{
    public static RunReportData Build(IReadOnlyList<RunRecord> runs)
    {
        var s = runs.Select(r => r.Summary).ToList();
        var success = s.Count(x => x.ExitCode == ExitCodes.Success);
        var withTtft = s.Where(x => x.TtftSeconds > 0).ToList();
        var (ok, total) = KpiMath.ParseSuccess(s.Select(x => (x.Attempts, x.FormatErrors)));

        var failures = s.Where(x => x.FailureKind is not null)
            .GroupBy(x => FailureKinds.IsKnown(x.FailureKind) ? x.FailureKind! : "Other")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        var phases = s.SelectMany(x => x.PhaseSeconds)
            .GroupBy(p => p.Key)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Average(p => p.Value));

        return new RunReportData(
            s.Count,
            s.GroupBy(x => x.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            success,
            s.Count == 0 ? 0 : (double)success / s.Count,
            s.GroupBy(x => x.ExitCode).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            SpreadOf(withTtft.Select(x => x.TtftSeconds)),
            withTtft.Count,
            SpreadOf(withTtft.Select(x => x.QueueSeconds)),
            SpreadOf(s.Select(x => x.PhaseSeconds.Values.Sum())),
            new ParseRate(ok, total),
            s.Count == 0 ? 0 : s.Max(x => x.ThinkTokens),
            s.Count(x => x.ThinkTruncated),
            failures,
            phases,
            s.GroupBy(x => x.Attempts).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            CountTestFirst(s));
    }

    private static TestFirstCounts? CountTestFirst(List<RunSummary> s)
    {
        // A run inside pelly apply is one task of an acceptance criterion; apply's own record counts those.
        var tf = s.Where(x => x.TestFirst is { InApply: false }).ToList();
        if (tf.Count == 0) return null;
        // Judged: passed RED and not declined by the human, so a run that never reached GREEN still counts in the denominator.
        var judged = tf.Where(x => x.TestFirst!.Red == RedState.Red && x.ExitCode != ExitCodes.UserCancelled).ToList();
        // ExitCode 0 also excludes a run whose test file changed mid-run (exit 4) even though Green/Regressions look fine.
        var met = judged.Count(x => x.TestFirst!.Green == GreenState.Passed && x.TestFirst.Regressions == 0 && x.ExitCode == ExitCodes.Success);
        return new TestFirstCounts(met, judged.Count,
            tf.Count(x => x.TestFirst!.Red == RedState.AlreadyPassed), tf.Count(x => x.TestFirst!.Red != RedState.NotChecked),
            tf.Sum(x => x.TestFirst!.BreaksExcluded));
    }

    private static Spread? SpreadOf(IEnumerable<double> values)
    {
        var list = values.ToList();
        return KpiMath.Percentile(list, 0.5) is { } median ? new Spread(median, KpiMath.Percentile(list, 0.9)!.Value) : null;
    }
}
