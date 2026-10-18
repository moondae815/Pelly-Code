using System.IO.Abstractions;
using System.Text;
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

/// <summary>SQL APPLY: the safety guard runs on the patched text before anything is written.</summary>
public sealed class SqlApplyFlowTests : IDisposable
{
    private const string Conn = "Server=sql1;Database=Shop;User Id=u;Password=Secret123;";
    private const string Search = "    SELECT Id FROM dbo.T WHERE Id = @Id;";
    private const string Replace = "    SELECT Id FROM dbo.T WHERE Id = @Id AND Id > 0;";

    private static readonly string[] Lines =
    [
        "SET ANSI_NULLS ON",
        "GO",
        "CREATE PROCEDURE dbo.usp_P @Id int",
        "AS",
        "BEGIN",
        Search,
        "END",
        "GO",
        "SELECT 1",
        "",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-sql-apply-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly FileGuard _guard;
    private readonly FakeSqlExecutor _sql = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly SqlOptions _options = new() { AllowedHosts = ["sql1"], AllowedDatabases = ["Shop"], ConnectionString = Conn };
    private readonly BudgetOptions _budget = new();

    public SqlApplyFlowTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        _guard = new FileGuard(_fs, TimeProvider.System);
        _sql.Schemas.Add(new SqlTableSchema(SqlObjectName.Parse("dbo.T"), "TABLE",
            [new("Id", "int", false, true)], []));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string PathA => Path.Combine(_root, "a.sql");

    private static byte[] Bytes(string newline) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(string.Join(newline, Lines))];

    private FeedbackBuilder Feedback => new(QwenTokenCounter.Shared, _budget.Feedback);

    private RunRequest Request(bool dryRun = false, int? maxRetries = null) =>
        new(PathA, null, null, null, "add a filter", true, dryRun, maxRetries, null, EditKind.Sql);

    private async Task<RunContext> Prepared(string newline = "\r\n", bool dryRun = false)
    {
        File.WriteAllBytes(PathA, Bytes(newline));
        var ctx = new RunContext("r1", Request(dryRun), new PellyOptions { Sql = _options })
        {
            FilePath = PathA,
            Workspace = new Workspace(_root, Path.Combine(_root, ".pelly")),
        };
        ctx.Original = _guard.Read(PathA);
        ctx.Current = ctx.Original;
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(PhaseOutcome.Ok, await new SqlLocatePhase(_ui, _options).ExecuteAsync(ctx, ct));
        Assert.Equal(PhaseOutcome.Ok, await new SqlSlicePhase(_guard, QwenTokenCounter.Shared, _budget, _sql).ExecuteAsync(ctx, ct));
        return ctx;
    }

    private Task<PhaseOutcome> Apply(RunContext ctx) =>
        new SqlApplyPhase(_guard, Feedback, _ui).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ValidEdit_IsWritten_OtherBatchesByteIdentical()
    {
        var ctx = await Prepared();
        var before = File.ReadAllBytes(PathA);
        ctx.Blocks = [new PatchBlock(Search, Replace)];

        var outcome = await Apply(ctx);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        var expected = Encoding.UTF8.GetBytes(string.Join("\r\n", Lines).Replace(Search, Replace));
        Assert.Equal([.. Encoding.UTF8.GetPreamble(), .. expected], File.ReadAllBytes(PathA));
        Assert.NotEqual(before, File.ReadAllBytes(PathA));
        Assert.NotNull(ctx.BackupPath);
        Assert.Equal(before, File.ReadAllBytes(ctx.BackupPath!));
        Assert.NotEmpty(ctx.MatchStages);
    }

    [Fact]
    public async Task GoInserted_IsSafetyRejected_NeverWritten()
    {
        var ctx = await Prepared();
        var before = File.ReadAllBytes(PathA);
        ctx.Blocks = [new PatchBlock(Search, Search + "\nGO")];

        var outcome = await Apply(ctx);

        Assert.Equal(PhaseOutcome.SafetyRejected, outcome);
        Assert.Equal("SqlSafetyRejected", ctx.FailureKind);
        Assert.StartsWith("SQL SAFETY REJECTED: the edit added", ctx.Feedback!.Text);
        Assert.Equal(before, File.ReadAllBytes(PathA));
        Assert.Null(ctx.BackupPath);
    }

    [Fact]
    public async Task ParseError_IsMatchFailed_WithSqlParseFeedback()
    {
        var ctx = await Prepared();
        var before = File.ReadAllBytes(PathA);
        ctx.Blocks = [new PatchBlock(Search, "    SELECT Id FROM dbo.T WHERE;")];

        var outcome = await Apply(ctx);

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal("SqlParseErrors", ctx.FailureKind);
        Assert.StartsWith("SQL PARSE FAILED (", ctx.Feedback!.Text);
        Assert.Contains("a.sql(", ctx.Feedback.Text);
        Assert.Contains(ctx.ErrorCodes, c => c.StartsWith("SQL", StringComparison.Ordinal) && c.Length > 3);
        Assert.Equal(before, File.ReadAllBytes(PathA));
    }

    [Fact]
    public async Task EditOutsideTargetBatch_IsOutsideRegion()
    {
        var ctx = await Prepared();
        var before = File.ReadAllBytes(PathA);
        ctx.Blocks = [new PatchBlock("SET ANSI_NULLS ON", "SET ANSI_NULLS OFF")];

        var outcome = await Apply(ctx);

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(PatchFailureKinds.OutsideRegion, ctx.FailureKind);
        Assert.Equal(before, File.ReadAllBytes(PathA));
    }

    [Fact]
    public async Task DryRun_ShowsDiff_WritesNothing()
    {
        var ctx = await Prepared(dryRun: true);
        var before = File.ReadAllBytes(PathA);
        ctx.Blocks = [new PatchBlock(Search, Replace)];

        var outcome = await Apply(ctx);

        Assert.Equal(PhaseOutcome.DryRunCompleted, outcome);
        var diff = Assert.Single(_ui.Diffs);
        Assert.Contains(Replace, diff.After);
        Assert.Equal(before, File.ReadAllBytes(PathA));
        Assert.Null(ctx.BackupPath);
    }

    [Fact]
    public async Task ForbiddenAdded_ThreeTimes_Exit5_Restored()
    {
        File.WriteAllBytes(PathA, Bytes("\r\n"));
        var before = File.ReadAllBytes(PathA);
        var forbidden = Search + "\n    EXEC xp_cmdshell 'dir';";
        for (var i = 0; i < 6; i++)
            _backend.Enqueue(Search + "\n=======\n" + forbidden + "\n>>>>>>> REPLACE\n");

        var tokens = QwenTokenCounter.Shared;
        var template = QwenChatTemplate.LoadDefault();
        var feedback = new FeedbackBuilder(tokens, _budget.Feedback);
        var logger = new RunLogger(_fs, TimeProvider.System);
        IPhase[] phases =
        [
            new SqlPreflightPhase(new PreflightPhase(_fs, _guard, _backend, new LlmOptions(), []), _sql, _options),
            new SqlLocatePhase(_ui, _options),
            new SqlSlicePhase(_guard, tokens, _budget, _sql),
            new EditPhase(new PromptBuilder(template, PromptResources.LoadDefault(), tokens, _budget),
                new QwenStreamClient(_backend, template, new LlmOptions(), _budget, new SamplingSet(), TimeProvider.System),
                feedback, logger),
            new SqlApplyPhase(_guard, feedback, _ui),
            new VerifyPhase([]),
            new ReviewPhase(_ui),
        ];
        var options = new PellyOptions { Sql = _options };
        var orchestrator = new FsmOrchestrator(phases, _guard, logger, _fs, options, _ui, TimeProvider.System);

        var exit = await orchestrator.RunAsync(Request(maxRetries: 2), TestContext.Current.CancellationToken);

        Assert.Equal(5, exit);
        Assert.Equal(before, File.ReadAllBytes(PathA));
        Assert.Equal(3, _ui.Statuses.Count(s => s.Phase == RunPhase.Apply));
    }
}
