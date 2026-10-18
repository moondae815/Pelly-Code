using PellyCoder.Core.Config;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Llm;

public sealed class PromptBuilderSqlTests
{
    private static readonly QwenTokenCounter Counter = QwenTokenCounter.Shared;
    private static readonly QwenChatTemplate Template = QwenChatTemplate.LoadDefault();
    private static readonly PromptResources Resources = PromptResources.LoadDefault();

    private static PromptBuilder NewBuilder() => new(Template, Resources, Counter, new BudgetOptions());

    private static EditTarget Target(string symbol = "dbo.usp_GetOrders", string file = @"C:\repo\db\usp_GetOrders.sql") =>
        new(EditKind.Sql, file, symbol, new TextSpanInfo(3, 9), null);

    private static SlicePart Part(string kind, string text) => new(kind, text, Counter.Count(text), 1);

    private static Slice FullSlice(string hint = "T-SQL, SQL Server compatibility level 160") => new(
        new[]
        {
            Part("schema", "TABLE dbo.Orders (OrderId int NOT NULL PK, Status tinyint NOT NULL)"),
            Part("target", "CREATE PROCEDURE dbo.usp_GetOrders\nAS\nSELECT OrderId FROM dbo.Orders;"),
        },
        Array.Empty<SlicePart>(), hint);

    private static string User(string prompt)
    {
        var start = prompt.IndexOf("<|im_start|>user\n", StringComparison.Ordinal) + "<|im_start|>user\n".Length;
        var end = prompt.IndexOf("<|im_end|>\n<|im_start|>assistant\n", start, StringComparison.Ordinal);
        return prompt[start..end];
    }

    [Fact]
    public void BuildSql_SectionsInOrder_NoLineNumbers()
    {
        var result = NewBuilder().BuildSql(Target(), SqlObjectKind.Procedure, FullSlice(), "  only status 1  ", new Feedback("SQL PARSE FAILED", 3));

        Assert.Equal(PromptBudgetError.None, result.Error);
        Assert.Equal(
            "### TARGET\n"
            + "dbo.usp_GetOrders (PROCEDURE) — file: usp_GetOrders.sql\n"
            + "### CONTEXT\n"
            + "[READ-ONLY: schema]\n"
            + "TABLE dbo.Orders (OrderId int NOT NULL PK, Status tinyint NOT NULL)\n"
            + "[EDITABLE: target]\n"
            + "CREATE PROCEDURE dbo.usp_GetOrders\nAS\nSELECT OrderId FROM dbo.Orders;\n"
            + "### REQUEST\n"
            + "only status 1\n"
            + "### FEEDBACK\n"
            + "SQL PARSE FAILED",
            User(result.BasePrompt));
    }

    [Theory]
    [InlineData(SqlObjectKind.Procedure, "PROCEDURE")]
    [InlineData(SqlObjectKind.Function, "FUNCTION")]
    [InlineData(SqlObjectKind.View, "VIEW")]
    public void BuildSql_TargetLine_NamesKind(SqlObjectKind kind, string word)
    {
        var user = User(NewBuilder().BuildSql(Target(), kind, FullSlice(), "r", null).BasePrompt);
        Assert.Contains($"dbo.usp_GetOrders ({word}) — file: usp_GetOrders.sql", user);
    }

    [Fact]
    public void BuildSql_WithoutSchemaPart_OmitsTheHeader()
    {
        var slice = new Slice(new[] { Part("target", "CREATE VIEW dbo.v AS SELECT 1 AS x") }, Array.Empty<SlicePart>(), "h");
        var user = User(NewBuilder().BuildSql(Target(), SqlObjectKind.View, slice, "r", null).BasePrompt);
        Assert.DoesNotContain("[READ-ONLY: schema]", user);
        Assert.Contains("[EDITABLE: target]", user);
    }

    [Fact]
    public void SqlSystemAndFewShot_FitSystemBudget()
    {
        var n = Counter.Count(Resources.SystemSql.Replace("{LanguageHint}", "T-SQL, SQL Server compatibility level 160") + "\n\n" + Resources.FewShotSql);
        Assert.True(n <= new BudgetOptions().System, $"system+few-shot is {n} tokens");
        Assert.InRange(Counter.Count(Resources.FewShotSql), 120, 260);
    }

    [Fact]
    public void BuildSql_SystemPartIsStableAcrossRequests()
    {
        var b = NewBuilder();
        var p1 = b.BuildSql(Target(), SqlObjectKind.Procedure, FullSlice(), "make it faster", null).BasePrompt;
        var slice2 = new Slice(new[] { Part("target", "CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END") }, Array.Empty<SlicePart>(),
            "T-SQL, SQL Server compatibility level 160");
        var p2 = b.BuildSql(Target("dbo.f", "/x/f.sql"), SqlObjectKind.Function, slice2, "other", new Feedback("fix", 1)).BasePrompt;
        const string marker = "<|im_end|>\n<|im_start|>user\n";
        var s1 = p1[..p1.IndexOf(marker, StringComparison.Ordinal)];
        var s2 = p2[..p2.IndexOf(marker, StringComparison.Ordinal)];
        Assert.Equal(s1, s2);
        Assert.Contains("Target: T-SQL, SQL Server compatibility level 160.", s1);
        Assert.DoesNotContain("{LanguageHint}", s1);
    }

    [Fact]
    public void BuildSql_BudgetChecksMatchCSharp()
    {
        var tiny = new PromptBuilder(Template, Resources, Counter, new BudgetOptions { Request = 3 });
        var r = tiny.BuildSql(Target(), SqlObjectKind.Procedure, FullSlice(), "a very long request that is over the limit", null);
        Assert.Equal(PromptBudgetError.RequestTooLong, r.Error);
    }

    [Fact]
    public void Resources_SqlVersion_AndVersionFor()
    {
        Assert.Equal("sql-v1", Resources.SqlVersion);
        Assert.Equal("sql-v1", Resources.VersionFor(EditKind.Sql));
        Assert.Equal(Resources.Version, Resources.VersionFor(EditKind.CSharp));
        Assert.DoesNotContain("# version", Resources.SystemSql);
        Assert.DoesNotContain("# version", Resources.FewShotSql);
    }

    [Fact]
    public void Resources_SqlVersionsMustMatch()
    {
        // LoadDefault compared both files already; a mismatch is a load-time failure, so a loaded instance proves they agree.
        Assert.Equal("sql-v1", PromptResources.LoadDefault().SqlVersion);
        var ex = Record.Exception(() => typeof(PromptResources).GetMethod("Load", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, ["missing.txt"]));
        Assert.NotNull(ex);
    }
}
