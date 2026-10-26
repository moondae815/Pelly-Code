using System.IO.Abstractions;
using PellyCoder.Core.Specs;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

/// <summary>
/// Definition checks of the eight test-first apply cases (<c>eval/benchmark/apply-test-first/t1..t8</c>, design V2-2c §5.2) that
/// need no build: the case set, the expected criteria, and a clean <c>spec check</c> on the corpus with the case's <c>files/</c>.
/// The real-dotnet checks (RED really RED, breaks passing on the original) are in the integration tests.
/// </summary>
public sealed class ApplyTestFirstCasesTests : IDisposable
{
    private static string CasesDir => Path.Combine(TestPaths.Suite.Dir, "apply-test-first");

    private static string CorpusDir => Path.GetFullPath(Path.Combine(TestPaths.Suite.Dir, "..", "corpus"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-eval-tf-cases-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    private static IReadOnlyList<ApplyCase> Cases() => ApplyBenchmark.LoadTestFirstCases(CasesDir, null, ApplyBenchmark.LoadApproved(CasesDir));

    [Fact]
    public void Cases_AreEight_WithExpected()
    {
        var cases = Cases();
        Assert.Equal(["t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8"], cases.Select(c => c.Id));
        Assert.Equal(["t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8"],
            Directory.GetDirectories(CasesDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        // A human approves the cases: the file is never written by the implementer.
        Assert.False(File.Exists(Path.Combine(CasesDir, ApplyBenchmark.ApprovedFile)), "APPROVED.json은 사람이 만듭니다");

        foreach (var c in cases)
        {
            Assert.NotNull(c.FilesDir);
            var spec = ChangeSpecParser.Parse(c.SpecText);
            var expected = c.Expected ?? throw new InvalidDataException($"{c.Id}: expected.json이 없습니다");
            // One expected row per criterion with a test:, in spec order, with valid enum names.
            Assert.Equal(spec.Criteria.Where(a => a.Test is not null).Select(a => a.Id), expected.Select(e => e.AcId));
            Assert.All(expected, e => Assert.True(Enum.TryParse<PellyCoder.Core.Testing.RedState>(e.Red, out _), $"{c.Id} {e.AcId}: red {e.Red}"));
            Assert.All(expected, e => Assert.True(Enum.TryParse<AcOutcome>(e.Outcome, out _), $"{c.Id} {e.AcId}: outcome {e.Outcome}"));
        }

        // The table of design §5.2: scopes, the shapes each case covers.
        var byId = cases.ToDictionary(c => c.Id);
        Assert.Equal(["Modern"], byId["t1"].Scope);
        Assert.True(byId["t3"].HasGroup);
        Assert.Equal(["Modern", "Sql"], byId["t6"].Scope);
        Assert.True(byId["t6"].HasSql);
        Assert.Equal(["Legacy"], byId["t8"].Scope);
        Assert.NotNull(byId["t8"].TestCommand);
        Assert.Contains("{trx}", byId["t8"].TestCommand, StringComparison.Ordinal);
        Assert.All(cases.Where(c => c.Id != "t8"), c => Assert.Null(c.TestCommand));
        Assert.Equal("AlreadyPassed", Assert.Single(byId["t4"].Expected!).Red);
        Assert.Equal("RedCompile", Assert.Single(byId["t3"].Expected!).Red);
        Assert.Equal([("AC1", "Met"), ("AC2", "Unverified")], byId["t6"].Expected!.Select(e => (e.AcId, e.Outcome)));
        Assert.Equal(["AC1", "AC2"], byId["t5"].Expected!.Select(e => e.AcId));

        // t2 drops the pinned old behaviour test through breaks:.
        var t2 = ChangeSpecParser.Parse(byId["t2"].SpecText).Criteria.Single();
        Assert.Contains(t2.BreakNames, b => b.EndsWith("_CurrentlyCancels", StringComparison.Ordinal));

        // t5: one task is the last linked task of AC1 and the first of AC2.
        var t5 = TestFirstLinks.Build(ChangeSpecParser.Parse(byId["t5"].SpecText));
        Assert.Equal(t5[0].LinkedTaskIds[^1], t5[1].LinkedTaskIds[0]);
        Assert.True(t5[0].LinkedTaskIds.Count > 1 && t5[1].LinkedTaskIds.Count > 1);

        // t1: one criterion filled by two tasks (service, then controller).
        var t1 = Assert.Single(TestFirstLinks.Build(ChangeSpecParser.Parse(byId["t1"].SpecText)));
        Assert.Equal(2, t1.LinkedTaskIds.Count);
    }

    [Fact]
    public void Cases_SpecCheckHasNoErrors()
    {
        var fs = new FileSystem();
        foreach (var c in Cases())
        {
            // The work copy the runner builds: corpus, an empty .git marker, the spec in changes/, then files/ on top.
            var corpus = Path.Combine(_root, c.Id, "corpus");
            WorkCopy.CopyDirectory(CorpusDir, corpus);
            Directory.CreateDirectory(Path.Combine(corpus, ".git"));
            Directory.CreateDirectory(Path.Combine(corpus, "changes"));
            var specPath = Path.Combine(corpus, "changes", c.Id + ".md");
            File.WriteAllText(specPath, c.SpecText);
            WorkCopy.Overlay(c.FilesDir!, corpus);

            var check = SpecChecker.Check(fs, specPath, 160);

            var issues = string.Join("; ", check.Issues.Select(i => $"{i.Severity} {i.Code}: {i.Message}"));
            Assert.False(check.HasErrors, $"{c.Id}: {issues}");
            // The only warnings are the ones expected.json predicts: TestUnverified for each criterion expected Unverified.
            var warnings = check.Issues.Where(i => i.Severity == SpecIssueSeverity.Warning).Select(i => i.Code).ToList();
            var unverified = c.Expected!.Count(e => e.Outcome == nameof(AcOutcome.Unverified));
            Assert.True(Enumerable.Repeat("TestUnverified", unverified).SequenceEqual(warnings), $"{c.Id}: {issues}");
        }
    }

    [Fact]
    public void ModernTestProjectsInFiles_AreInTheSolution()
    {
        // A test project a case adds must be in Modern.sln, so the runner's restore covers it (else the re-run is unverifiable).
        foreach (var c in Cases())
        {
            var modern = Path.Combine(c.FilesDir!, "Modern");
            if (!Directory.Exists(modern)) continue;
            var projects = Directory.GetFiles(modern, "*.csproj", SearchOption.AllDirectories);
            var sln = Path.Combine(modern, "Modern.sln");
            var slnText = File.Exists(sln) ? File.ReadAllText(sln) : File.ReadAllText(Path.Combine(CorpusDir, "Modern", "Modern.sln"));
            foreach (var project in projects)
            {
                var relative = Path.GetRelativePath(modern, project).Replace('/', '\\');
                Assert.True(slnText.Contains($"\"{relative}\"", StringComparison.Ordinal), $"{c.Id}: {relative}가 Modern.sln에 없습니다");
            }
        }
    }
}
