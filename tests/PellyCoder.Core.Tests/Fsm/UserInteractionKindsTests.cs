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

/// <summary>The final approval question and server-wait updates have their own interface members (V2-0).</summary>
public sealed class UserInteractionKindsTests : IDisposable
{
    private const string Proc = "CREATE PROCEDURE dbo.usp_P @Id int\nAS\nBEGIN\n    SELECT Id FROM dbo.T WHERE Id = @Id;\nEND\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-kinds-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();

    public UserInteractionKindsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Overrides both new members and records which member received what.</summary>
    private sealed class KindRecordingUi : IUserInteraction
    {
        public bool IsInteractive => true;
        public List<string> Approvals { get; } = [];
        public List<string> Confirms { get; } = [];
        public List<string> Waitings { get; } = [];
        public List<string> Statuses { get; } = [];

        public Task<bool> ApproveAsync(string message, CancellationToken ct) { Approvals.Add(message); return Task.FromResult(true); }
        public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct) { Confirms.Add(message); return Task.FromResult(false); }
        public Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct) => throw new NotSupportedException();
        public void ShowDiff(string path, string before, string after) { }
        public void ShowStatus(RunPhase phase, int attempt, string message) => Statuses.Add(message);
        public void ShowWaiting(RunPhase phase, int attempt, string message) => Waitings.Add(message);
        public void Warn(string message) { }
        public void ShowRunSummary(RunSummary summary, string? runDirectory) { }
    }

    /// <summary>Overrides neither new member, so the defaults apply.</summary>
    private sealed class DefaultsOnlyUi : IUserInteraction
    {
        public bool IsInteractive => true;
        public List<(string Message, bool Default)> Confirms { get; } = [];
        public List<string> Statuses { get; } = [];

        public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct) { Confirms.Add((message, defaultValue)); return Task.FromResult(true); }
        public Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct) => throw new NotSupportedException();
        public void ShowDiff(string path, string before, string after) { }
        public void ShowStatus(RunPhase phase, int attempt, string message) => Statuses.Add(message);
        public void Warn(string message) { }
        public void ShowRunSummary(RunSummary summary, string? runDirectory) { }
    }

    [Fact]
    public async Task Review_AsksThroughApproveAsync()
    {
        var path = Path.Combine(_root, "B.cs");
        var ui = new KindRecordingUi();
        var ctx = new RunContext("r1", new RunRequest(path, null, "M", null, "do it", false, false, null), new PellyOptions())
        {
            FilePath = path,
        };
        ctx.Original = new FileSnapshot(path, System.Text.Encoding.UTF8, false, "\n", true, "", "class A { }\n");
        ctx.Current = ctx.Original with { Text = "class B { }\n" };

        var outcome = await new ReviewPhase(ui).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Approved, outcome);
        Assert.Equal(["적용할까요?"], ui.Approvals);
        Assert.Empty(ui.Confirms);
    }

    [Fact]
    public async Task Edit_ServerWait_UsesShowWaiting()
    {
        var guard = new FileGuard(_fs, TimeProvider.System);
        var path = Path.Combine(_root, "a.sql");
        File.WriteAllText(path, Proc);
        var sqlOptions = new SqlOptions { AllowedHosts = ["sql1"], AllowedDatabases = ["Shop"] };
        var sql = new FakeSqlExecutor();
        sql.Schemas.Add(new SqlTableSchema(SqlObjectName.Parse("dbo.T"), "TABLE",
            [new("Id", "int", false, true), new("Name", "nvarchar(50)", true, false)], []));
        var budget = new BudgetOptions();
        var request = new RunRequest(path, null, null, null, "only Id 1", true, false, null, null, EditKind.Sql);
        var ctx = new RunContext("r1", request, new PellyOptions { Sql = sqlOptions })
        {
            FilePath = path,
            Workspace = new Workspace(_root, Path.Combine(_root, ".pelly")),
        };
        ctx.Original = guard.Read(path);
        ctx.Current = ctx.Original;
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(PhaseOutcome.Ok, await new SqlLocatePhase(new ScriptedUserInteraction(), sqlOptions).ExecuteAsync(ctx, ct));
        await new SqlSlicePhase(guard, QwenTokenCounter.Shared, budget, sql).ExecuteAsync(ctx, ct);

        var backend = new ScriptedLlmBackend();
        backend.Enqueue("    SELECT 1;\n=======\n    SELECT 2;\n>>>>>>> REPLACE\n");
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        backend.BeforeRespond = _ =>
        {
            for (var i = 0; i < 4; i++)
                time.Advance(TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        };
        var tokens = QwenTokenCounter.Shared;
        var template = QwenChatTemplate.LoadDefault();
        var ui = new KindRecordingUi();
        var edit = new EditPhase(new PromptBuilder(template, PromptResources.LoadDefault(), tokens, budget),
            new QwenStreamClient(backend, template, new LlmOptions(), budget, new SamplingSet(), time),
            new FeedbackBuilder(tokens, budget.Feedback), new RunLogger(_fs, time), ui);

        await edit.ExecuteAsync(ctx, ct);

        const string wait = "서버 응답 대기 중 (3초) — 다른 요청을 처리 중이거나 모델을 불러오는 중일 수 있습니다";
        Assert.Contains(wait, ui.Waitings);
        Assert.DoesNotContain(ui.Statuses, s => s.StartsWith("서버 응답 대기 중"));
    }

    [Fact]
    public async Task DefaultImplementations_ForwardToExistingMembers()
    {
        var ui = new DefaultsOnlyUi();
        IUserInteraction asInterface = ui;

        var approved = await asInterface.ApproveAsync("적용할까요?", TestContext.Current.CancellationToken);
        asInterface.ShowWaiting(RunPhase.Edit, 2, "기다리는 중");

        Assert.True(approved);
        Assert.Equal([("적용할까요?", false)], ui.Confirms);
        Assert.Equal(["기다리는 중"], ui.Statuses);
    }
}
