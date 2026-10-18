using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;

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
    IReadOnlyDictionary<int, int> AttemptDistribution);

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
            s.GroupBy(x => x.Attempts).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()));
    }

    private static Spread? SpreadOf(IEnumerable<double> values)
    {
        var list = values.ToList();
        return KpiMath.Percentile(list, 0.5) is { } median ? new Spread(median, KpiMath.Percentile(list, 0.9)!.Value) : null;
    }
}
