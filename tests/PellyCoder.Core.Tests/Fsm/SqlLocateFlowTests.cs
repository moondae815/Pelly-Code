using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>SQL PREFLIGHT and LOCATE run phase by phase (the orchestrator is wired for SQL in a later task).</summary>
public sealed class SqlLocateFlowTests : IDisposable
{
    private const string Conn = "Server=sql1;Database=Shop;User Id=u;Password=Secret123;";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-sql-locate-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly FakeSqlExecutor _sql = new();
    private readonly SqlOptions _options = new()
    {
        AllowedHosts = ["sql1"], AllowedDatabases = ["Shop"], ConnectionString = Conn,
    };

    public SqlLocateFlowTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static string Corpus(string file) =>
        File.ReadAllText(CorpusLocation.Get("Sql/objects/" + file)).TrimStart('﻿').Replace("\r\n", "\n");

    private RunContext Ctx(string sqlText, int? line = null, string? obj = null)
    {
        var path = Path.Combine(_root, "a.sql");
        File.WriteAllText(path, sqlText);
        var request = new RunRequest(path, null, null, line, "do it", true, false, null, obj, EditKind.Sql);
        return new RunContext("r1", request, new PellyOptions { Sql = _options });
    }

    private async Task<(PhaseOutcome Outcome, RunContext Ctx)> Preflight()
    {
        var ctx = Ctx("SELECT 1\n");
        var inner = new PreflightPhase(_fs, new FileGuard(_fs, TimeProvider.System), _backend, new LlmOptions(), []);
        var outcome = await new SqlPreflightPhase(inner, _sql, _options).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Lock?.Dispose();
        return (outcome, ctx);
    }

    private async Task<(PhaseOutcome Outcome, RunContext Ctx)> Locate(string sqlText, int? line = null, string? obj = null)
    {
        var ctx = Ctx(sqlText, line, obj);
        ctx.FilePath = ctx.Request.FilePath;
        ctx.Original = new FileGuard(_fs, TimeProvider.System).Read(ctx.FilePath);
        ctx.Current = ctx.Original;
        var outcome = await new SqlLocatePhase(_ui, _options).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        return (outcome, ctx);
    }

    [Fact]
    public async Task NoConnection_Exit4()
    {
        _options.ConnectionString = null;
        var (outcome, ctx) = await Preflight();
        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Contains("SQL 연결 문자열이 없습니다. 비밀번호 없는 연결은 sql.connectionString, 비밀번호가 있는 연결은 환경 변수 PELLY_SQL_CONNECTION으로 지정하세요", ctx.Messages);
        Assert.Empty(_sql.Calls);
    }

    [Fact]
    public async Task NoDatabase_IsEnvError()
    {
        _options.ConnectionString = "Server=sql1;Integrated Security=true;";
        var (outcome, ctx) = await Preflight();
        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Contains("연결 문자열에 Database(Initial Catalog)가 없습니다", ctx.Messages);
    }

    [Fact]
    public async Task HostNotAllowed_Exit5_NoLlmCall()
    {
        _options.AllowedHosts.Clear();
        var (outcome, ctx) = await Preflight();
        Assert.Equal(PhaseOutcome.SafetyRejected, outcome);
        Assert.Equal("SqlTargetNotAllowed", ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("sql.allowedHosts"));
        Assert.Empty(_backend.Requests);
        Assert.Empty(_sql.Calls);
    }

    [Fact]
    public async Task NoViewDefinition_IsEnvError_BeforeAnyLlmCall()
    {
        _sql.Probe = new SqlServerProbe("v", "Shop", false);
        var (outcome, ctx) = await Preflight();
        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Equal("SqlPermission", ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("GRANT VIEW DEFINITION"));
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task ProbeThrows_IsEnvError_WithMaskedConnection()
    {
        _sql.ThrowOnProbe(new SqlUnavailableException("로그인 실패 (Server=sql1;Password=***)"));
        var (outcome, ctx) = await Preflight();
        Assert.Equal(PhaseOutcome.EnvError, outcome);
        var message = Assert.Single(ctx.Messages);
        Assert.StartsWith("SQL 서버에 연결할 수 없습니다 (sql1/Shop): ", message);
        Assert.EndsWith(". pelly doctor로 확인하세요", message);
        Assert.DoesNotContain("Secret123", message);
    }

    [Fact]
    public async Task Preflight_AllChecksPass_IsOk()
    {
        var (outcome, _) = await Preflight();
        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(["Probe"], _sql.Calls);
    }

    [Fact]
    public async Task SingleObject_IsTarget_SpanMatchesBatch()
    {
        var (outcome, ctx) = await Locate(Corpus("usp_CancelOrder.sql"));
        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(EditKind.Sql, ctx.Target!.Kind);
        Assert.Equal("dbo.usp_CancelOrder", ctx.Target.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(5, 52), ctx.Target.MemberSpan);
        Assert.Equal("Sql", ctx.ProjectStyle);
        Assert.Equal(SqlObjectKind.Procedure, ctx.Sql!.Kind);
        Assert.Equal("CreateProcedureStatement", ctx.Sql.StatementType);
        Assert.Equal(2, ctx.Sql.TargetIndex);
    }

    [Fact]
    public async Task Object_SelectsAmongMany()
    {
        var (outcome, ctx) = await Locate(Corpus("Settlement_All.sql"), obj: "usp_SettleMonthly");
        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("dbo.usp_SettleMonthly", ctx.Target!.SymbolDisplay);
    }

    [Fact]
    public async Task Object_Missing_IsNotFound()
    {
        var (outcome, ctx) = await Locate(Corpus("Settlement_All.sql"), obj: "usp_Nope");
        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal("TargetNotFound", ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.StartsWith("대상 객체 'usp_Nope'을(를) 찾을 수 없습니다: "));
    }

    [Fact]
    public async Task Line_InsideSecondObject_SelectsIt()
    {
        var text = Corpus("Settlement_All.sql");
        var script = SqlBatchSplitter.Split(text, 160);
        var second = script.ObjectBatches[1];
        var (outcome, ctx) = await Locate(text, line: second.StartLine + 2);
        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("dbo.usp_SettleMonthly", ctx.Target!.SymbolDisplay);
    }

    [Fact]
    public async Task Line_OnGrantBatch_IsNotFound_WithCursorHint()
    {
        var text = Corpus("Settlement_All.sql");
        var grant = SqlBatchSplitter.Split(text, 160).Batches.First(b => b.ObjectKind is null && b.Text.Contains("GRANT"));
        var (outcome, ctx) = await Locate(text, line: grant.StartLine);
        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Contains($"{grant.StartLine}번 줄은 프로시저·함수·뷰 정의 안이 아닙니다. 정의 안에 커서를 두고 실행하세요", ctx.Messages);
    }

    [Fact]
    public async Task ManyObjects_NonInteractive_IsAmbiguous_ListsNames()
    {
        var (outcome, ctx) = await Locate(Corpus("Settlement_All.sql"));
        Assert.Equal(PhaseOutcome.Ambiguous, outcome);
        Assert.Contains("객체가 2개입니다. --object로 지정하세요: dbo.usp_SettleDaily, dbo.usp_SettleMonthly", ctx.Messages);
    }

    [Fact]
    public async Task ManyObjects_Interactive_UsesSelection()
    {
        _ui.IsInteractive = true;
        _ui.SelectIndex = _ => 1;
        var (outcome, ctx) = await Locate(Corpus("Settlement_All.sql"));
        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("dbo.usp_SettleMonthly", ctx.Target!.SymbolDisplay);
        Assert.Single(_ui.SelectLabels);
    }

    [Fact]
    public async Task NoDefinitions_IsNotFound()
    {
        var (outcome, ctx) = await Locate("SELECT 1\n");
        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Contains("파일에 프로시저·함수·뷰 정의가 없습니다", ctx.Messages);
    }

    [Fact]
    public async Task ForbiddenInOriginal_Exits5_WithList()
    {
        var (outcome, ctx) = await Locate("CREATE PROCEDURE dbo.p AS\nBEGIN\n    EXEC xp_cmdshell 'dir';\nEND\n");
        Assert.Equal(PhaseOutcome.SafetyRejected, outcome);
        Assert.Equal("SqlForbiddenInOriginal", ctx.FailureKind);
        Assert.Contains("대상 객체에 이미 금지 구문(xp_cmdshell)이 있어 pelly로 편집할 수 없습니다 (PRD FR-07)", ctx.Messages);
    }

    /// <summary>
    /// ScriptDom returns no tree for a batch with a syntax error, so such a batch has no ObjectKind and surfaces as
    /// "no definitions" with the broken batch's lines. (The R-T3 guard for a partial tree is defensive.)
    /// </summary>
    [Fact]
    public async Task TargetBatchWithParseError_IsNotFound_NamesTheBrokenBatch()
    {
        var (outcome, ctx) = await Locate("CREATE PROCEDURE dbo.p AS\nBEGIN\n    SELECT 1 FROM dbo.t WHERE;\nEND\n");
        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal("TargetNotFound", ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.StartsWith("파일에 프로시저·함수·뷰 정의가 없습니다 (구문 오류가 있는 배치: 줄 1-"));
    }

    [Fact]
    public async Task Line_InsideBrokenBatch_IsNotFound()
    {
        var (outcome, _) = await Locate("CREATE PROCEDURE dbo.p AS\nBEGIN\n    SELECT 1 FROM dbo.t WHERE;\nEND\n", line: 2);
        Assert.Equal(PhaseOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task LineWithObject_IsEnvError()
    {
        var (outcome, ctx) = await Locate(Corpus("Settlement_All.sql"), line: 10, obj: "usp_SettleDaily");
        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Contains("--line은 --object와 함께 쓸 수 없습니다", ctx.Messages);
    }
}
