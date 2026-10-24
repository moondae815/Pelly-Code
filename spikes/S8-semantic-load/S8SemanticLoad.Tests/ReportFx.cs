using S8SemanticLoad;

/// <summary>Builds LoadReport values for verdict/report tests with clean defaults.</summary>
public static class ReportFx
{
    public static LoadReport Clean(double open = 6, double compile = 4, double rate = 1.0, int csharp = 4,
        IReadOnlyList<IssueCount>? issues = null, int errorsCounted = 0, int errorsExcluded = 0, int missingRefs = 0,
        double probeMax = 0.5, A3Result? a3 = null, string mode = "field", int callsTotal = 100, int? measured = null, int probeMethods = 20, int extra = 0,
        IReadOnlyList<string>? messages = null, IReadOnlyList<ProjectHealth>? projects = null) =>
        new(mode, "osx", "dev", "5.9.0", null,
            new CensusCounts(csharp, 0, 0, 0, csharp, extra),
            issues ?? [],
            open, compile, open + compile + 1, 500_000_000,
            new CallResolution(callsTotal, (int)Math.Round(rate * callsTotal), 0, callsTotal - (int)Math.Round(rate * callsTotal)),
            new HealthTotals(errorsCounted, errorsExcluded, missingRefs, measured ?? csharp),
            new ProbeResult(probeMethods, probeMethods == 0 ? 0 : probeMax, probeMethods == 0 ? 0 : probeMax / 2, 0.1),
            a3, messages, projects);
}
