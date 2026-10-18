using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>SQL SLICE and the SQL branch of EDIT run phase by phase (the orchestrator is wired for SQL in a later task).</summary>
public sealed class SqlSliceFlowTests : IDisposable
{
    private const string Proc = "CREATE PROCEDURE dbo.usp_P @Id int\nAS\nBEGIN\n    SELECT Id FROM dbo.T WHERE Id = @Id;\nEND\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-sql-slice-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly FileGuard _guard;
    private readonly FakeSqlExecutor _sql = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly SqlOptions _options = new() { AllowedHosts = ["sql1"], AllowedDatabases = ["Shop"] };
    private readonly BudgetOptions _budget = new();

    public SqlSliceFlowTests()
    {
        Directory.CreateDirectory(_root);
        _guard = new FileGuard(_fs, TimeProvider.System);
        _sql.Schemas.Add(new SqlTableSchema(SqlObjectName.Parse("dbo.T"), "TABLE",
            [new("Id", "int", false, true), new("Name", "nvarchar(50)", true, false)], []));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string PathA => Path.Combine(_root, "a.sql");

    private SqlSlicePhase Slice(BudgetOptions? budget = null) => new(_guard, QwenTokenCounter.Shared, budget ?? _budget, _sql);

    private async Task<RunContext> Located(string text = Proc)
    {
        File.WriteAllText(PathA, text);
        var request = new RunRequest(PathA, null, null, null, "only Id 1", true, false, null, null, EditKind.Sql);
        var ctx = new RunContext("r1", request, new PellyOptions { Sql = _options })
        {
            FilePath = PathA,
            Workspace = new Workspace(_root, Path.Combine(_root, ".pelly")),
        };
        ctx.Original = _guard.Read(PathA);
        ctx.Current = ctx.Original;
        var outcome = await new SqlLocatePhase(new ScriptedUserInteraction(), _options).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PhaseOutcome.Ok, outcome);
        return ctx;
    }

    [Fact]
    public async Task SqlSlice_BuildsSchemaAndTarget()
    {
        var ctx = await Located();
        var outcome = await Slice().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(["schema", "target"], ctx.SliceResult.Slice.Included.Select(p => p.Kind));
        Assert.Contains("TABLE dbo.T (Id int NOT NULL PK, Name nvarchar(50) NULL)", ctx.SliceResult.Slice.Included[0].Text);
        Assert.Equal(Proc, ctx.SliceResult.Slice.Included[1].Text);
    }

    [Fact]
    public async Task SqlSlice_ExternalChange_IsEnvError()
    {
        var ctx = await Located();
        File.AppendAllText(PathA, "-- someone else\n");

        var outcome = await Slice().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Equal("FileChangedExternally", ctx.FailureKind);
        Assert.Contains($"pelly 실행 중에 파일이 다른 곳에서 바뀌었습니다. 더 진행하지 않습니다: {PathA}", ctx.Messages);
    }

    [Fact]
    public async Task SqlSlice_SecondAttempt_UsesEditedTargetBatch()
    {
        var ctx = await Located();
        await Slice().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        // What APPLY would have written after attempt 1.
        var edited = Proc.Replace("WHERE Id = @Id;", "WHERE Id = @Id AND Name IS NOT NULL;");
        File.WriteAllText(PathA, edited);
        ctx.Current = _guard.Read(PathA);

        var outcome = await Slice().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(edited, ctx.SliceResult.Slice.Included.Single(p => p.Kind == "target").Text);
        Assert.Equal(edited, ctx.Sql!.CurrentTarget.Text);
    }

    [Fact]
    public async Task SqlSlice_TargetBatchBroken_IsNotFound()
    {
        var ctx = await Located();
        File.WriteAllText(PathA, Proc.Replace("WHERE Id = @Id;", "WHERE;"));
        ctx.Current = _guard.Read(PathA);

        var outcome = await Slice().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal("TargetNotFound", ctx.FailureKind);
    }

    [Fact]
    public async Task SqlSlice_BatchCountChanged_IsNotFound()
    {
        var ctx = await Located();
        File.WriteAllText(PathA, "GO\n" + Proc + "GO\nSELECT 1\n");
        ctx.Current = _guard.Read(PathA);

        var outcome = await Slice().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal("TargetNotFound", ctx.FailureKind);
    }

    [Fact]
    public async Task SqlSlice_ExecutorUnavailable_IsEnvError()
    {
        var ctx = await Located();
        var sql = new ThrowingExecutor();

        var outcome = await new SqlSlicePhase(_guard, QwenTokenCounter.Shared, _budget, sql).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Contains(ctx.Messages, m => m.Contains("SQL 서버를 사용할 수 없습니다"));
    }

    [Fact]
    public async Task SqlSlice_UnexpectedExecutorFailure_IsEnvErrorWithMaskedMessage()
    {
        var ctx = await Located();
        _sql.ReferencedFailure = new InvalidOperationException("boom Server=sql1;Password=Secret123;");

        var outcome = await Slice().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.EnvError, outcome);
        var message = Assert.Single(ctx.Messages);
        Assert.StartsWith("스키마 정보를 가져오지 못했습니다: ", message);
        Assert.DoesNotContain("Secret123", message);
    }

    [Fact]
    public async Task SqlSlice_Cancellation_Propagates()
    {
        var ctx = await Located();
        _sql.ReferencedFailure = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Slice().ExecuteAsync(ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SqlSlice_TargetTooLarge_IsBudgetExceeded()
    {
        var ctx = await Located();
        var outcome = await Slice(new BudgetOptions { Context = 5 }).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.BudgetExceeded, outcome);
        var message = Assert.Single(ctx.Messages);
        Assert.StartsWith("대상 객체가 문맥 예산보다 큽니다 (", message);
        Assert.EndsWith(" > budget.context 5 토큰). 객체를 나누거나 예산을 늘리세요.", message);
    }

    [Fact]
    public async Task EditPhase_SqlTarget_UsesSqlPrompt()
    {
        var ctx = await Located();
        await Slice().ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        _backend.Enqueue("    SELECT Id FROM dbo.T WHERE Id = @Id;\n=======\n    SELECT Id FROM dbo.T WHERE Id = @Id AND Id = 1;\n>>>>>>> REPLACE\n");

        var tokens = QwenTokenCounter.Shared;
        var template = QwenChatTemplate.LoadDefault();
        var edit = new EditPhase(new PromptBuilder(template, PromptResources.LoadDefault(), tokens, _budget),
            new QwenStreamClient(_backend, template, new LlmOptions(), _budget, new SamplingSet(), TimeProvider.System),
            new FeedbackBuilder(tokens, _budget.Feedback), new RunLogger(_fs, TimeProvider.System));

        var outcome = await edit.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        var prompt = Assert.Single(_backend.Requests).RawPrompt;
        Assert.Contains("[READ-ONLY: schema]", prompt);
        Assert.Contains("dbo.usp_P (PROCEDURE) — file: a.sql", prompt);
        Assert.Contains("Target: T-SQL, SQL Server compatibility level 160.", prompt);
        Assert.DoesNotContain("[READ-ONLY: fields]", prompt);
        Assert.DoesNotContain("[READ-ONLY: signatures]", prompt);
        Assert.DoesNotContain("[EDITABLE: usings]", prompt);
        Assert.Single(ctx.Blocks);
    }

    private sealed class ThrowingExecutor : ISqlExecutor
    {
        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => throw new SqlUnavailableException("down");
        public Task<IReadOnlyList<SqlObjectName>> FindMissingAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct) => throw new SqlUnavailableException("down");
        public Task<IReadOnlyList<SqlTableSchema>> GetSchemasAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct) => throw new SqlUnavailableException("down");
        public Task<IReadOnlyList<SqlObjectName>> GetReferencedEntitiesAsync(SqlObjectName obj, CancellationToken ct) => throw new SqlUnavailableException("down");
        public Task<IReadOnlyList<SqlCompileError>> CompileWithNoExecAsync(string batch, SqlSessionSettings settings, CancellationToken ct) => throw new SqlUnavailableException("down");
    }
}
