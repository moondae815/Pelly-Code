using PellyCoder.Core.Specs;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

/// <summary>The apply benchmark's specs are the approved S6 gold specs, converted without touching a task line.</summary>
public class ApplyCasesTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(TestPaths.Suite.Dir, "..", ".."));

    private static string CasesDir => Path.Combine(TestPaths.Suite.Dir, "apply");

    private static string S6Cases => Path.Combine(RepoRoot, "spikes", "S6-planning", "cases");

    private static IEnumerable<string> TaskLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith("- [", StringComparison.Ordinal));

    [Fact]
    public void Cases_match_the_approved_S6_gold()
    {
        var files = Directory.GetFiles(CasesDir, "*.md").Where(f => !string.Equals(Path.GetFileName(f), "README.md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(12, files.Count);
        Assert.True(File.Exists(Path.Combine(S6Cases, "APPROVED.json")), "S6 APPROVED.json이 없습니다");

        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            var spec = ChangeSpecParser.Parse(text);
            Assert.True(spec.Issues.Count == 0, $"{id}: {string.Join("; ", spec.Issues.Select(i => i.Message))}");
            Assert.Equal(id, spec.Id);
            Assert.Equal(SpecStatus.Draft, spec.Status);

            var gold = File.ReadAllText(Path.Combine(S6Cases, id, "gold.md"));
            Assert.Equal(TaskLines(gold), TaskLines(text));
        }
    }

    [Fact]
    public void LoadCases_reads_scope_and_task_shape()
    {
        var cases = ApplyBenchmark.LoadCases(CasesDir, null);
        Assert.Equal(12, cases.Count);

        var a1 = Assert.Single(cases, c => c.Id == "a1-cancel-returns-refund");
        Assert.Equal(["Modern"], a1.Scope);
        Assert.Equal((3, false, true), (a1.TaskCount, a1.HasSql, a1.HasGroup));

        var b1 = Assert.Single(cases, c => c.Id == "b1-block-cancel-shipped");
        Assert.Equal(["Legacy", "Sql"], b1.Scope);
        Assert.Equal((3, true, false), (b1.TaskCount, b1.HasSql, b1.HasGroup));

        Assert.Equal(["d1-modern-cancel-shipped", "d2-legacy-pending-history", "d3-sql-order-detail-cancel-columns"],
            ApplyBenchmark.LoadCases(CasesDir, "d*").Select(c => c.Id));
        Assert.Equal(["a1-cancel-returns-refund", "c1-repository-command-timeout"],
            ApplyBenchmark.LoadCases(CasesDir, "a1,c1").Select(c => c.Id));
    }
}
