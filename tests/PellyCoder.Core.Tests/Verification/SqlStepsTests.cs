using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Models;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Verification;

namespace PellyCoder.Core.Tests.Verification;

/// <summary>The two SQL VERIFY steps in isolation: baseline-diffed diagnostics, caching and environment failures.</summary>
public sealed class SqlStepsTests
{
    private const string Original = "SET ANSI_NULLS ON\nGO\nCREATE PROCEDURE dbo.usp_P @Id int\nAS\nBEGIN\n    SELECT Id FROM dbo.T WHERE Id = @Id;\nEND\nGO\n";

    private readonly FakeSqlExecutor _sql = new();
    private readonly FeedbackBuilder _feedback = new(QwenTokenCounter.Shared, new BudgetOptions().Feedback);

    private static RunContext Ctx(string originalText, string currentText, string path = "/work/proj/a.sql")
    {
        var script = SqlBatchSplitter.Split(originalText, 160);
        var index = script.Batches.ToList().FindIndex(b => b.ObjectName is not null);
        var target = script.Batches[index];
        var request = new RunRequest(path, null, null, null, "x", true, false, null, null, EditKind.Sql);
        var ctx = new RunContext("r1", request, new PellyOptions())
        {
            FilePath = path,
            Current = new FileSnapshot(path, System.Text.Encoding.UTF8, false, "\n", true, "sha", currentText),
        };
        var state = new SqlRunState(script, index, target.ObjectName!, target.ObjectKind!.Value, target.StatementType!,
            script.SettingsBefore(index), 160);
        state.CurrentTarget = SqlBatchSplitter.Split(currentText, 160).Batches[index];
        ctx.Sql = state;
        return ctx;
    }

    [Fact]
    public async Task ObjectStep_NewMissingTable_Is208_WithFileLine()
    {
        var current = Original.Replace("dbo.T WHERE", "dbo.NoSuch WHERE");
        _sql.Missing.Add(SqlObjectName.Parse("dbo.NoSuch"));
        var ctx = Ctx(Original, current);

        var result = await new SqlObjectStep(_sql, _feedback).VerifyAsync(ctx, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        var error = Assert.Single(result.NewErrors);
        Assert.Equal(("a.sql", 6, "Msg208", "Invalid object name 'dbo.NoSuch'."), (error.File, error.Line, error.Code, error.Message));
        Assert.StartsWith("SQL CHECK FAILED (1 error)", result.Feedback!.Text);
        Assert.DoesNotContain("/work/proj", result.Feedback.Text);
    }

    [Fact]
    public async Task ObjectStep_OriginalMissing_IsNotCounted_NoticeOnce_QueriedOnce()
    {
        _sql.Missing.Add(SqlObjectName.Parse("dbo.T"));
        var ctx = Ctx(Original, Original);
        var step = new SqlObjectStep(_sql, _feedback);

        Assert.True((await step.VerifyAsync(ctx, TestContext.Current.CancellationToken)).Passed);
        Assert.True((await step.VerifyAsync(ctx, TestContext.Current.CancellationToken)).Passed);

        Assert.Equal(["원본 객체가 이미 없는 객체(dbo.T)를 참조합니다 — 새로 생긴 것만 검사합니다"], ctx.Messages);
        Assert.Equal(3, _sql.Calls.Count(c => c == "FindMissing")); // original once, current twice
    }

    [Fact]
    public async Task CompileStep_NewError_IsMsgN_WithFileLine_OriginalErrorSubtracted()
    {
        var current = Original.Replace("Id = @Id;", "Id = @Id AND Bogus = 1;");
        _sql.Compile = batch => batch.Contains("Bogus")
            ? [new SqlCompileError(207, 4, "Invalid column name 'Bogus'."), new SqlCompileError(2812, 1, "Old problem.")]
            : [new SqlCompileError(2812, 2, "Old problem.")];
        var ctx = Ctx(Original, current);

        var result = await new SqlCompileStep(_sql, _feedback).VerifyAsync(ctx, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        var error = Assert.Single(result.NewErrors);
        Assert.Equal(("a.sql", 6, "Msg207"), (error.File, error.Line, error.Code)); // batch starts on line 3; 3 + 4 - 1
        Assert.Contains("원본 객체에 이미 컴파일 오류가 1개 있습니다 — 새로 생긴 오류만 검사합니다", ctx.Messages);
    }

    [Fact]
    public async Task CompileStep_Pass_ShowsNoticesOnce()
    {
        var text = "CREATE PROCEDURE dbo.usp_P\nAS\nBEGIN\n    EXEC('SELECT 1');\n    SELECT * INTO #x FROM dbo.T;\n    SELECT 1 FROM Other.dbo.U;\nEND\n";
        var ctx = Ctx(text, text);
        var step = new SqlCompileStep(_sql, _feedback);

        Assert.True((await step.VerifyAsync(ctx, TestContext.Current.CancellationToken)).Passed);
        Assert.True((await step.VerifyAsync(ctx, TestContext.Current.CancellationToken)).Passed);

        Assert.Contains("동적 SQL 안의 문장은 검증하지 않습니다 (설계 §8.10)", ctx.Messages);
        Assert.Contains("임시 테이블(#)의 컬럼은 검증하지 않습니다 (설계 §8.10)", ctx.Messages);
        Assert.Contains(ctx.Messages, m => m.StartsWith("다른 데이터베이스·연결된 서버 객체(", StringComparison.Ordinal));
        Assert.Equal(3, ctx.Messages.Count);
    }

    [Fact]
    public async Task Steps_ExecutorUnavailable_IsEnvironmentFailure()
    {
        _sql.Compile = _ => throw new SqlUnavailableException("connection lost");
        var ctx = Ctx(Original, Original);

        var result = await new SqlCompileStep(_sql, _feedback).VerifyAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(result.EnvironmentFailure);
        Assert.Contains("connection lost", result.Message);
    }

    [Fact]
    public async Task Steps_OtherExecutorException_IsEnvironmentFailure_NotThrown()
    {
        _sql.Compile = _ => throw new InvalidOperationException("query exploded");
        var ctx = Ctx(Original, Original);

        var result = await new SqlCompileStep(_sql, _feedback).VerifyAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(result.EnvironmentFailure);
        Assert.Contains("query exploded", result.Message);
    }
}
