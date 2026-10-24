using System.IO.Abstractions;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

public sealed class SpecRunnerTests : IDisposable
{
    private const string Proc = "CREATE PROCEDURE dbo.usp_A AS SELECT 1;\n";

    private readonly string _root = Directory.CreateTempSubdirectory("pelly-apply-").FullName;
    private readonly FileSystem _fs = new();
    private readonly FileGuard _guard;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero));
    private readonly ScriptedUserInteraction _ui = new() { IsInteractive = true };
    private readonly FakeExecutor _exec;

    public SpecRunnerTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _guard = new FileGuard(_fs, TimeProvider.System);
        _exec = new FakeExecutor(_guard);
        File.WriteAllText(Path.Combine(_root, "App.sln"), "");
        Directory.CreateDirectory(Path.Combine(_root, "db"));
        Directory.CreateDirectory(Path.Combine(_root, "changes"));
        foreach (var name in new[] { "a", "b", "c" }) Make($"db/{name}.sql", Proc);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Make(string rel, string text) => File.WriteAllText(P(rel), text, new UTF8Encoding(false));

    private string Read(string rel) => File.ReadAllText(P(rel));

    private string SpecPath => P("changes/x.md");

    private string ArchivePath => P("changes/archive/2026-10-24-x.md");

    private void Spec(string tasks) =>
        Make("changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks);

    private const string ThreeTasks =
        "- [ ] T1 sql db/a.sql 첫째\n- [ ] T2 sql db/b.sql 둘째\n- [ ] T3 sql db/c.sql 셋째\n";

    private static ApplyOptions Opts(bool cont = false, bool resume = false, bool reviewAtEnd = false, bool yes = true, bool dryRun = false) =>
        new(cont, resume, reviewAtEnd, yes, dryRun, null, false);

    private SpecRunner Runner(Action<Workspace, string>? prune = null) => new(_fs, _guard, _exec, _ui, _time, 160, prune);

    private Task<int> Run(ApplyOptions o) => Runner().RunAsync(SpecPath, o, TestContext.Current.CancellationToken);

    private string[] ApplyDirs() =>
        Directory.Exists(P(".pelly/runs")) ? Directory.GetDirectories(P(".pelly/runs"), "apply-*") : [];

    [Fact]
    public async Task All_tasks_succeed_marks_done_archives_and_exits_0()
    {
        Spec(ThreeTasks);
        string? prunedWith = null;
        var code = await Runner((_, id) => prunedWith = id).RunAsync(SpecPath, Opts(), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2", "T3"], _exec.Calls);
        Assert.False(File.Exists(SpecPath));
        var archived = File.ReadAllText(ArchivePath);
        Assert.Contains("status: done", archived);
        Assert.Contains("- [x] T1 sql db/a.sql 첫째 <!-- run: R-T1 -->", archived);
        Assert.Contains("- [x] T3 sql db/c.sql 셋째 <!-- run: R-T3 -->", archived);
        Assert.Contains("-- T2", Read("db/b.sql"));

        var dir = Assert.Single(ApplyDirs());
        var id = Path.GetFileName(dir);
        Assert.Matches(@"^apply-\d{8}-\d{6}-[0-9a-f]{4}$", id);
        Assert.Equal(id, prunedWith);
        var json = File.ReadAllText(Path.Combine(dir, "apply.json"));
        Assert.Contains("\"archived\": true", json);
        Assert.Contains("\"R-T2\"", json);
        Assert.False(File.Exists(Path.Combine(dir, "summary.json")));
        // Released: we can take it again. Dispose ours, or Windows cannot delete the temp folder in Dispose().
        using var again = WorkspaceLock.TryAcquire(_fs, new Workspace(_root, P(".pelly")));
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Failure_stops_and_marks_failed()
    {
        Spec(ThreeTasks);
        _exec.Exit["T2"] = 1;
        var code = await Run(Opts());

        Assert.Equal(1, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        var spec = Read("changes/x.md");
        Assert.Contains("- [x] T1 sql db/a.sql 첫째 <!-- run: R-T1 -->", spec);
        Assert.Contains("- [ ] T2 sql db/b.sql 둘째 <!-- failed: R-T2 Verify -->", spec);
        Assert.Contains("- [ ] T3 sql db/c.sql 셋째\n", spec);
        Assert.Contains("status: in-progress", spec);
    }

    [Fact]
    public async Task Continue_runs_remaining_and_exits_1()
    {
        Spec(ThreeTasks);
        _exec.Exit["T2"] = 1;
        var code = await Run(Opts(cont: true));

        Assert.Equal(1, code);
        Assert.Equal(["T1", "T2", "T3"], _exec.Calls);
        var spec = Read("changes/x.md");
        Assert.Contains("<!-- failed: R-T2 Verify -->", spec);
        Assert.Contains("- [x] T3 sql db/c.sql 셋째 <!-- run: R-T3 -->", spec);
        Assert.False(File.Exists(ArchivePath));
    }

    [Fact]
    public async Task Continue_stops_on_env_error()
    {
        Spec(ThreeTasks);
        _exec.Exit["T2"] = 4;
        var code = await Run(Opts(cont: true));

        Assert.Equal(4, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
    }

    [Fact]
    public async Task Resume_starts_at_first_unchecked()
    {
        Spec("- [x] T1 sql db/a.sql 첫째 <!-- run: R-OLD -->\n- [ ] T2 sql db/b.sql 둘째 <!-- failed: R-F Verify -->\n- [ ] T3 sql db/c.sql 셋째\n");
        var code = await Run(Opts(resume: true));

        Assert.Equal(0, code);
        Assert.Equal(["T2", "T3"], _exec.Calls);
        var archived = File.ReadAllText(ArchivePath);
        Assert.Contains("<!-- run: R-OLD -->", archived);
        Assert.Contains("- [x] T2 sql db/b.sql 둘째 <!-- run: R-T2 -->", archived);
    }

    [Fact]
    public async Task Already_progressed_spec_without_resume_is_refused()
    {
        Spec("- [x] T1 sql db/a.sql 첫째 <!-- run: R-OLD -->\n- [ ] T2 sql db/b.sql 둘째\n");
        var before = Read("changes/x.md");
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        Assert.Equal(before, Read("changes/x.md"));
        Assert.Contains(_ui.Warnings, w => w.Contains("--resume"));
    }

    [Fact]
    public async Task Spec_errors_exit_4_without_running_tasks()
    {
        Spec("- [ ] T1 sql db/missing.sql 첫째\n- [ ] T2 sql db/b.sql 둘째\n");
        var before = Read("changes/x.md");
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        Assert.Equal(before, Read("changes/x.md"));
        Assert.Contains(_ui.Warnings, w => w.Contains("missing.sql"));
        Assert.Empty(ApplyDirs());
    }

    [Fact]
    public async Task Non_interactive_without_yes_refuses_before_any_task()
    {
        Spec(ThreeTasks);
        _ui.IsInteractive = false;
        var before = Read("changes/x.md");
        var code = await Run(Opts(yes: false));

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        Assert.Equal(before, Read("changes/x.md"));
        Assert.Equal(Proc, Read("db/a.sql"));
        Assert.False(Directory.Exists(P(".pelly")));
        Assert.Contains(_ui.Warnings, w => w.Contains("--yes"));
    }

    [Fact]
    public async Task Warnings_are_confirmed_interactively_and_rejection_exits_2()
    {
        Spec("- [ ] T1 sql db/a.sql `NopeName` 을 쓴다\n");
        _ui.ConfirmAnswers.Enqueue(false);
        var code = await Run(Opts(yes: false));

        Assert.Equal(2, code);
        Assert.Equal(["경고가 있습니다. 계속할까요?"], _ui.ConfirmMessages);
        Assert.Contains(_ui.Warnings, w => w.Contains("NopeName"));
        Assert.Empty(_exec.Calls);
        Assert.Contains("status: draft", Read("changes/x.md"));
    }

    [Fact]
    public async Task Dry_run_runs_first_task_only_and_leaves_spec_unchanged()
    {
        Spec(ThreeTasks);
        var before = Read("changes/x.md");
        _exec.Exit["T1"] = 1;
        var code = await Run(Opts(dryRun: true));

        Assert.Equal(1, code);
        Assert.Equal(["T1"], _exec.Calls);
        Assert.True(_exec.Options[0].DryRun);
        Assert.Equal(before, Read("changes/x.md"));
        Assert.Empty(ApplyDirs());
    }

    [Fact]
    public async Task Dry_run_in_a_non_interactive_run_does_not_need_yes()
    {
        Spec(ThreeTasks);
        _ui.IsInteractive = false;
        var before = Read("changes/x.md");
        var code = await Run(Opts(yes: false, dryRun: true));

        Assert.Equal(0, code);
        Assert.Equal(["T1"], _exec.Calls);
        Assert.Equal(Proc, Read("db/a.sql"));
        Assert.Equal(before, Read("changes/x.md"));
    }

    [Fact]
    public async Task Review_at_end_approve_keeps_changes()
    {
        Spec(ThreeTasks);
        _ui.ConfirmAnswers.Enqueue(true);
        var code = await Run(Opts(reviewAtEnd: true, yes: false));

        Assert.Equal(0, code);
        Assert.All(_exec.Options, o => Assert.True(o.Yes));
        Assert.Equal(["모두 적용할까요?"], _ui.ConfirmMessages);
        Assert.Equal(3, _ui.Diffs.Count);
        Assert.Contains("-- T1", Read("db/a.sql"));
        Assert.Contains("status: done", File.ReadAllText(ArchivePath));
    }

    [Fact]
    public async Task Review_at_end_reject_restores_originals_for_shared_file()
    {
        Spec("- [ ] T1 sql db/a.sql 첫째\n- [ ] T2 sql db/a.sql 둘째\n");
        var before = File.ReadAllBytes(P("db/a.sql"));
        _ui.ConfirmAnswers.Enqueue(false);
        var code = await Run(Opts(reviewAtEnd: true, yes: false));

        Assert.Equal(2, code);
        var diff = Assert.Single(_ui.Diffs);
        Assert.Equal(Proc, diff.Before);
        Assert.Contains("-- T1", diff.After);
        Assert.Contains("-- T2", diff.After);
        Assert.Equal(before, File.ReadAllBytes(P("db/a.sql")));
        var spec = Read("changes/x.md");
        Assert.Contains("status: draft", spec);
        Assert.Contains("- [ ] T1 sql db/a.sql 첫째\n", spec);
        Assert.Contains("- [ ] T2 sql db/a.sql 둘째\n", spec);
        Assert.DoesNotContain("<!--", spec);
        Assert.False(File.Exists(ArchivePath));
    }

    [Fact]
    public async Task Apply_stops_when_spec_file_changes_midway()
    {
        Spec(ThreeTasks);
        _exec.During["T1"] = () => File.AppendAllText(SpecPath, "\n사람이 적은 메모\n");
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Equal(["T1"], _exec.Calls);
        var spec = Read("changes/x.md");
        Assert.Contains("사람이 적은 메모", spec);
        Assert.DoesNotContain("[x]", spec);
        Assert.Contains("-- T1", Read("db/a.sql")); // the applied task stays
        Assert.Contains(_ui.Warnings, w => w.Contains("명세 파일이 실행 중에 바뀌었습니다"));
        Assert.Contains(_ui.Warnings, w => w.Contains("작업 T1은 적용됐지만 체크 표시를 남기지 못했습니다"));
    }

    [Fact]
    public async Task Review_at_end_reject_with_spec_changed_keeps_files_and_marks()
    {
        Spec("- [ ] T1 sql db/a.sql 첫째\n- [ ] T2 sql db/b.sql 둘째\n");
        _ui.ConfirmAnswers.Enqueue(false);
        var ui = new ConfirmHookUi(_ui, () => File.AppendAllText(SpecPath, "\n사람이 적은 메모\n"));
        var code = await new SpecRunner(_fs, _guard, _exec, ui, _time, 160)
            .RunAsync(SpecPath, Opts(reviewAtEnd: true, yes: false), TestContext.Current.CancellationToken);

        Assert.Equal(4, code);
        // Nothing is rolled back: the marks could not be cleared, so files and marks stay consistent.
        Assert.Contains("-- T1", Read("db/a.sql"));
        Assert.Contains("-- T2", Read("db/b.sql"));
        var spec = Read("changes/x.md");
        Assert.Contains("사람이 적은 메모", spec);
        Assert.Contains("- [x] T1", spec);
        Assert.Contains("- [x] T2", spec);
        Assert.Contains(_ui.Warnings, w => w.Contains("거부를 반영하지 못했습니다"));
    }

    [Fact]
    public async Task Review_at_end_reject_keeps_marks_for_files_changed_outside()
    {
        Spec("- [ ] T1 sql db/a.sql 첫째\n- [ ] T2 sql db/b.sql 둘째\n");
        _exec.During["T2"] = () => File.AppendAllText(P("db/a.sql"), "-- outside\n");
        _ui.ConfirmAnswers.Enqueue(false);
        var code = await Run(Opts(reviewAtEnd: true, yes: false));

        Assert.Equal(4, code);
        Assert.Equal(Proc, Read("db/b.sql"));
        Assert.Equal(Proc + "-- T1\n-- outside\n", Read("db/a.sql"));
        var spec = Read("changes/x.md");
        Assert.Contains("- [x] T1 sql db/a.sql 첫째 <!-- run: R-T1 -->", spec);
        Assert.Contains("- [ ] T2 sql db/b.sql 둘째\n", spec);
        Assert.Contains("status: in-progress", spec);
        var warning = Assert.Single(_ui.Warnings, w => w.Contains("되돌리지 못한"));
        Assert.Contains("a.sql", warning);
        Assert.Contains("T1", warning);
        Assert.DoesNotContain("b.sql", warning);
    }

    [Fact]
    public async Task Lock_held_by_another_run_exits_4()
    {
        Spec(ThreeTasks);
        using var held = WorkspaceLock.TryAcquire(_fs, new Workspace(_root, P(".pelly")));
        Assert.NotNull(held);
        var before = Read("changes/x.md");
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        Assert.Equal(before, Read("changes/x.md"));
    }

    [Fact]
    public async Task Cancellation_stops_with_2_and_keeps_completed_marks()
    {
        Spec(ThreeTasks);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _exec.During["T2"] = () => cts.Cancel();
        _exec.Exit["T2"] = 2;
        var code = await Runner().RunAsync(SpecPath, Opts(), cts.Token);

        Assert.Equal(2, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        var spec = Read("changes/x.md");
        Assert.Contains("- [x] T1 sql db/a.sql 첫째 <!-- run: R-T1 -->", spec);
        Assert.Contains("- [ ] T2 sql db/b.sql 둘째\n", spec);
    }

    [Fact]
    public async Task Executor_cancellation_exception_stops_with_2()
    {
        Spec(ThreeTasks);
        _exec.Throw["T2"] = new OperationCanceledException();
        var code = await Run(Opts());

        Assert.Equal(2, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.Contains("- [x] T1", Read("changes/x.md"));
    }

    [Fact]
    public async Task Review_at_end_reject_keeps_a_file_edited_between_two_tasks_on_it()
    {
        Spec("- [ ] T1 sql db/a.sql 첫째\n- [ ] T2 sql db/a.sql 둘째\n");
        _exec.During["T2"] = () => File.AppendAllText(P("db/a.sql"), "-- outside\n");
        _ui.ConfirmAnswers.Enqueue(false);
        var code = await Run(Opts(reviewAtEnd: true, yes: false));

        Assert.Equal(4, code);
        Assert.Equal(Proc + "-- T1\n-- outside\n-- T2\n", Read("db/a.sql"));
        var spec = Read("changes/x.md");
        Assert.Contains("- [x] T1 sql db/a.sql 첫째 <!-- run: R-T1 -->", spec);
        Assert.Contains("- [x] T2 sql db/a.sql 둘째 <!-- run: R-T2 -->", spec);
        Assert.Contains("status: in-progress", spec);
        var warning = Assert.Single(_ui.Warnings, w => w.Contains("되돌리지 못한"));
        Assert.Contains("a.sql", warning);
    }

    [Fact]
    public async Task Review_at_end_reject_keeps_a_file_edited_before_the_first_task_read_it()
    {
        Spec("- [ ] T1 sql db/a.sql 첫째\n- [ ] T2 sql db/b.sql 둘째\n");
        _exec.During["T1"] = () => File.AppendAllText(P("db/a.sql"), "-- outside\n");
        _ui.ConfirmAnswers.Enqueue(false);
        var code = await Run(Opts(reviewAtEnd: true, yes: false));

        Assert.Equal(4, code);
        Assert.Equal(Proc + "-- outside\n-- T1\n", Read("db/a.sql"));
        Assert.Equal(Proc, Read("db/b.sql"));
        var spec = Read("changes/x.md");
        Assert.Contains("- [x] T1 sql db/a.sql 첫째 <!-- run: R-T1 -->", spec);
        Assert.Contains("- [ ] T2 sql db/b.sql 둘째\n", spec);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Review_at_end_in_a_non_interactive_run_is_refused_up_front(bool yes)
    {
        Spec(ThreeTasks);
        _ui.IsInteractive = false;
        var before = Read("changes/x.md");
        var code = await Run(Opts(reviewAtEnd: true, yes: yes));

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        Assert.Equal(before, Read("changes/x.md"));
        Assert.False(Directory.Exists(P(".pelly")));
        Assert.Contains(_ui.Warnings, w => w.Contains("--review-at-end는 대화형 실행에서만 쓸 수 있습니다"));
    }

    [Fact]
    public async Task Cancellation_under_review_at_end_rolls_back_the_whole_apply()
    {
        Spec(ThreeTasks);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _exec.During["T2"] = () => cts.Cancel();
        _exec.Exit["T2"] = 2;
        var code = await Runner().RunAsync(SpecPath, Opts(reviewAtEnd: true, yes: false), cts.Token);

        Assert.Equal(2, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.Equal(Proc, Read("db/a.sql"));
        var spec = Read("changes/x.md");
        Assert.Contains("status: draft", spec);
        Assert.DoesNotContain("[x]", spec);
        Assert.DoesNotContain("<!--", spec);
        Assert.Empty(_ui.ConfirmMessages);
    }

    [Fact]
    public async Task Executor_cancellation_exception_under_review_at_end_rolls_back_the_whole_apply()
    {
        Spec(ThreeTasks);
        _exec.Throw["T3"] = new OperationCanceledException();
        var code = await Run(Opts(reviewAtEnd: true, yes: false));

        Assert.Equal(2, code);
        Assert.Equal(Proc, Read("db/a.sql"));
        Assert.Equal(Proc, Read("db/b.sql"));
        Assert.DoesNotContain("[x]", Read("changes/x.md"));
    }

    [Fact]
    public async Task Spec_write_failure_under_review_at_end_lists_the_unreviewed_files()
    {
        Spec(ThreeTasks);
        _exec.During["T2"] = () => File.AppendAllText(SpecPath, "\n사람이 적은 메모\n");
        var code = await Run(Opts(reviewAtEnd: true, yes: false));

        Assert.Equal(4, code);
        Assert.Empty(_ui.ConfirmMessages);
        Assert.Contains("-- T1", Read("db/a.sql"));
        Assert.Contains("-- T2", Read("db/b.sql"));
        var warning = Assert.Single(_ui.Warnings, w => w.Contains("검토하지 않은"));
        Assert.Contains("a.sql", warning);
        Assert.Contains("b.sql", warning);
        Assert.DoesNotContain("c.sql", warning);
    }

    [Fact]
    public async Task Review_at_end_reject_after_resume_keeps_in_progress_for_earlier_marks()
    {
        Spec("- [x] T1 sql db/a.sql 첫째 <!-- run: R-OLD -->\n- [ ] T2 sql db/b.sql 둘째\n");
        _ui.ConfirmAnswers.Enqueue(false);
        var code = await Run(Opts(resume: true, reviewAtEnd: true, yes: false));

        Assert.Equal(2, code);
        Assert.Equal(Proc, Read("db/b.sql"));
        var spec = Read("changes/x.md");
        Assert.Contains("- [x] T1 sql db/a.sql 첫째 <!-- run: R-OLD -->", spec);
        Assert.Contains("- [ ] T2 sql db/b.sql 둘째\n", spec);
        Assert.Contains("status: in-progress", spec);
    }

    /// <summary>Delegates to the scripted UI and runs <paramref name="onConfirm"/> just before each confirm.</summary>
    private sealed class ConfirmHookUi(ScriptedUserInteraction inner, Action onConfirm) : IUserInteraction
    {
        public bool IsInteractive => inner.IsInteractive;

        public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct)
        {
            onConfirm();
            return inner.ConfirmAsync(message, defaultValue, ct);
        }

        public Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct) =>
            inner.SelectAsync(title, choices, label, ct);

        public void ShowDiff(string path, string before, string after) => inner.ShowDiff(path, before, after);
        public void ShowStatus(RunPhase phase, int attempt, string message) => inner.ShowStatus(phase, attempt, message);
        public void Warn(string message) => inner.Warn(message);
        public void ShowRunSummary(PellyCoder.Core.Logging.RunSummary summary, string? runDirectory) => inner.ShowRunSummary(summary, runDirectory);
    }

    /// <summary>Appends <c>-- &lt;id&gt;</c> to the task's file through FileGuard and reports a scripted exit code.</summary>
    private sealed class FakeExecutor(FileGuard guard) : ITaskExecutor
    {
        public List<string> Calls { get; } = [];
        public List<TaskRunOptions> Options { get; } = [];
        public Dictionary<string, int> Exit { get; } = [];
        public Dictionary<string, Action> During { get; } = [];
        public Dictionary<string, Exception> Throw { get; } = [];

        public async Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct)
        {
            Calls.Add(task.Id);
            Options.Add(o);
            if (During.TryGetValue(task.Id, out var during)) during();
            if (Throw.TryGetValue(task.Id, out var ex)) throw ex;
            var path = Path.Combine(ws.Root, task.Path.Replace('/', Path.DirectorySeparatorChar));
            var original = guard.Read(path);
            int exit = Exit.GetValueOrDefault(task.Id);
            if (exit != 0 || o.DryRun)
                return new RunResult(exit, "R-" + task.Id, path, original, original, exit == 0 ? null : "Verify");
            var written = await guard.WriteAsync(original, original.Text + "-- " + task.Id + "\n", CancellationToken.None);
            return new RunResult(0, "R-" + task.Id, path, original, written.Snapshot, null);
        }
    }
}
