namespace PellyCoder.Core.Reporting;

/// <summary>KPI arithmetic shared by <c>pelly report</c>, the sanitized report and the benchmark runner.</summary>
public static class KpiMath
{
    /// <summary>Nearest-rank percentile (<paramref name="p"/> in 0..1): the value at rank ⌈p·N⌉ of the sorted values; null when empty.</summary>
    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return null;
        var rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>Parse-success counts: EDIT attempts that were not format errors (Ok) out of all attempts (Total).</summary>
    public static (int Ok, int Total) ParseSuccess(IEnumerable<(int Attempts, int FormatErrors)> runs)
    {
        var attempts = 0;
        var formatErrors = 0;
        foreach (var (a, f) in runs)
        {
            attempts += a;
            formatErrors += f;
        }
        return (attempts - formatErrors, attempts);
    }
}
