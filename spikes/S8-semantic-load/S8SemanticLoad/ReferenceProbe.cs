using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace S8SemanticLoad;

public sealed record ProbeResult(int Methods, double MaxSeconds, double MeanSeconds, double ZeroReferenceRatio);

/// <summary>Times find-references + find-implementations on a seeded sample of public methods. Reports numbers only.</summary>
public static class ReferenceProbe
{
    public static async Task<ProbeResult> RunAsync(Solution s, int max = 20, int seed = 20261024, CancellationToken ct = default)
    {
        var candidates = new List<(string Path, int Pos, IMethodSymbol Method)>();
        foreach (var project in s.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
            foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (m.MethodKind != MethodKind.Ordinary || m.DeclaredAccessibility != Accessibility.Public) continue;
                var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
                if (loc is null) continue;
                candidates.Add((loc.SourceTree!.FilePath ?? "", loc.SourceSpan.Start, m));
            }
        }
        var rnd = new Random(seed);
        var picked = candidates.OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Pos)
            .Select(c => (c, Key: rnd.Next())).OrderBy(x => x.Key).Take(max).Select(x => x.c.Method).ToList();

        var times = new List<double>();
        var zero = 0;
        foreach (var m in picked)
        {
            var sw = Stopwatch.StartNew();
            var refs = await SymbolFinder.FindReferencesAsync(m, s, ct);
            var impls = await SymbolFinder.FindImplementationsAsync(m, s, cancellationToken: ct);
            sw.Stop();
            times.Add(sw.Elapsed.TotalSeconds);
            if (!refs.SelectMany(r => r.Locations).Any() && !impls.Any()) zero++;
        }
        return times.Count == 0
            ? new ProbeResult(0, 0, 0, 0)
            : new ProbeResult(times.Count, times.Max(), times.Average(), (double)zero / times.Count);
    }

    internal static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var t in ns.GetTypeMembers())
        foreach (var x in WithNested(t)) yield return x;
        foreach (var child in ns.GetNamespaceMembers())
        foreach (var x in AllTypes(child)) yield return x;
    }

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol t)
    {
        yield return t;
        foreach (var n in t.GetTypeMembers())
        foreach (var x in WithNested(n)) yield return x;
    }
}
