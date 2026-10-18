using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public class TaskFilterTests
{
    private static readonly BenchmarkTask[] Tasks =
    [
        new() { Id = "cs-legacy-001", Kind = "cs" },
        new() { Id = "cs-modern-001", Kind = "cs" },
        new() { Id = "cs-modern-012", Kind = "cs" },
        new() { Id = "sql-001", Kind = "sql" },
    ];

    private static string[] Select(string? pattern, string? kind) =>
        [.. Tasks.Where(t => TaskFilter.Matches(t, pattern, kind)).Select(t => t.Id)];

    [Fact]
    public void TaskFilter_PrefixAndKind()
    {
        Assert.Equal(4, Select(null, null).Length);
        Assert.Equal(["cs-modern-001", "cs-modern-012"], Select("cs-modern", null));
        Assert.Equal(["cs-modern-001"], Select("cs-modern-001", null));
        Assert.Equal(["cs-legacy-001", "cs-modern-001", "sql-001"], Select("*-001", null));
        Assert.Equal(["cs-modern-012"], Select("cs-*-01?", null));
        Assert.Equal(["cs-legacy-001", "sql-001"], Select("cs-legacy,sql", null));
        Assert.Equal(["sql-001"], Select(null, "sql"));
        Assert.Equal(["cs-legacy-001", "cs-modern-001"], Select("*-001", "cs"));
        Assert.Empty(Select("sql", "cs"));
    }
}
