using System.IO.Abstractions;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

/// <summary>
/// <c>pelly apply</c> with asp tasks (A2 design §4.1): the executor's asp branch, groups mixing cs·sql·asp (undo together,
/// build-verify only their cs tasks) and the mismatch warning for an ungrouped sql task applied before a failed asp task.
/// </summary>
public sealed class SpecRunnerAspTests : IDisposable
{
    private const string Page =
        "<%\nFunction GetOne()\n  GetOne = 1\nEnd Function\n\nSub DoIt()\nEnd Sub\n%>\n<html></html>\n";

    private const string Proc = "CREATE PROCEDURE dbo.usp_A AS SELECT 1;\n";

    // GetOrder spans lines 5-8.
    private const string OrderService =
        "namespace App;\n\npublic class OrderService\n{\n    public int GetOrder(int id)\n    {\n        return id;\n    }\n}\n";

    private readonly string _root = Directory.CreateTempSubdirectory("pelly-asp-apply-").FullName;
    private readonly FileSystem _fs = new();
    private readonly FileGuard _guard;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 9, 30, 0, TimeSpan.Zero));
    private readonly ScriptedUserInteraction _ui = new() { IsInteractive = true };
    private readonly FakeExecutor _exec;
    private readonly FakeGroupVerifier _builds = new();

    public SpecRunnerAspTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _guard = new FileGuard(_fs, TimeProvider.System);
        _exec = new FakeExecutor(_guard);
        _exec.Display["T1"] = "OrderService.GetOrder(int)";
        Make("App.sln", "");
        Make("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Make("src/Web/Web.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../App/App.csproj\" /></ItemGroup></Project>");
        Make("src/App/OrderService.cs", OrderService);
        // A project of its own next to the page: the mixed group must not build it.
        Make("site/Site.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Make("site/a.asp", Page);
        // No project anywhere above these.
        Make("web/a.asp", Page);
        Make("web/b.asp", Page);
        Make("db/a.sql", Proc);
        Make("db/b.sql", Proc);
        Directory.CreateDirectory(P("changes"));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Make(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), text, new UTF8Encoding(false));
    }

    private string Read(string rel) => File.ReadAllText(P(rel));

    private string SpecPath => P("changes/x.md");

    private string ArchivePath => P("changes/archive/2026-11-09-x.md");

    private void Spec(string tasks) =>
        Make("changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks);

    private static ApplyOptions Opts(bool cont = false, bool dryRun = false) => new(cont, false, false, true, dryRun, null, false);

    private Task<int> Run(ApplyOptions o, ITaskExecutor? executor = null, IGroupVerifier? groups = null) =>
        new SpecRunner(_fs, _guard, executor ?? _exec, _ui, _time, 160, groups: groups ?? _builds)
            .RunAsync(SpecPath, o, TestContext.Current.CancellationToken);

    /// <summary>The real executor over a run that only records the request.</summary>
    private static FsmTaskExecutor Recording(List<RunRequest> seen) => new((r, _) =>
    {
        seen.Add(r);
        return Task.FromResult(new RunResult(0, "R-" + seen.Count, r.FilePath, null, null, null));
    });

    [Fact]
    public async Task Asp_task_runs_with_lines_and_procedure()
    {
        Spec("- [ ] T1 asp web/a.asp#GetOne 값을 바꾼다\n- [ ] T2 asp web/b.asp#L2-4 줄을 바꾼다\n");
        var seen = new List<RunRequest>();
        var code = await Run(Opts(), Recording(seen));

        Assert.Equal(0, code);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, r =>
        {
            Assert.Equal(EditKind.Asp, r.Kind);
            Assert.Null(r.ClassName);
            Assert.Null(r.ObjectName);
            Assert.Null(r.Group);
            Assert.True(r.Yes);
            Assert.NotNull(r.Host);
        });
        Assert.Equal("GetOne", seen[0].MethodName);
        Assert.Null(seen[0].Lines);
        Assert.Equal("값을 바꾼다", seen[0].Request);
        Assert.Equal(P("web/a.asp"), seen[0].FilePath);
        Assert.Null(seen[1].MethodName);
        Assert.Equal(new TextSpanInfo(2, 4), seen[1].Lines);
        Assert.Contains("status: done", File.ReadAllText(ArchivePath));
    }

    [Fact]
    public async Task Group_asp_task_keeps_class_target_and_group_mode()
    {
        var seen = new List<RunRequest>();
        // A Class.Name target goes to --proc as written; the executor alone maps it.
        var exec = Recording(seen);
        var task = new SpecTask("T2", TaskKind.Asp, "web/a.asp", "Class1.GetOne", "바꾼다", [], "G1", false, null, null, 1);
        var ws = new Workspace(_root, Path.Combine(_root, ".pelly"));
        await exec.ExecuteAsync(task, ws, new TaskRunOptions(true, false, 2, false, new GroupRun(null)), TestContext.Current.CancellationToken);

        var r = Assert.Single(seen);
        Assert.Equal("Class1.GetOne", r.MethodName);
        Assert.NotNull(r.Group);
        Assert.Equal(2, r.MaxRetries);
    }

    [Fact]
    public async Task Group_sql_then_asp_both_succeed_commits()
    {
        Spec("- [ ] T1 sql db/a.sql 바꾼다 [G1]\n- [ ] T2 asp web/a.asp#GetOne 바꾼다 [G1]\n");
        var code = await Run(Opts());

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.All(_exec.Options, o => Assert.NotNull(o.Group));
        Assert.Empty(_builds.Calls);
        Assert.Contains("-- T1", Read("db/a.sql"));
        Assert.Contains("' T2", Read("web/a.asp"));
        var archived = File.ReadAllText(ArchivePath);
        Assert.Contains("- [x] T1 sql db/a.sql 바꾼다 [G1] <!-- run: R-T1 -->", archived);
        Assert.Contains("- [x] T2 asp web/a.asp#GetOne 바꾼다 [G1] <!-- run: R-T2 -->", archived);
    }

    [Fact]
    public async Task Group_sql_then_failing_asp_restores_sql_bytes()
    {
        // CP949 with CRLF: the undo writes back the backup's bytes, not a re-encoding of the text.
        var original = Encoding.GetEncoding(949).GetBytes("-- 주문 목록\r\nCREATE PROCEDURE dbo.usp_A AS SELECT 1;\r\n");
        File.WriteAllBytes(P("db/a.sql"), original);
        var page = File.ReadAllBytes(P("web/a.asp"));
        Spec("- [ ] T1 sql db/a.sql 바꾼다 [G1]\n- [ ] T2 asp web/a.asp#GetOne 바꾼다 [G1]\n");
        _exec.Exit["T2"] = 1;
        var code = await Run(Opts());

        Assert.Equal(1, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.Equal(original, File.ReadAllBytes(P("db/a.sql")));
        Assert.Equal(page, File.ReadAllBytes(P("web/a.asp")));
        var spec = Read("changes/x.md");
        Assert.Contains("- [ ] T1 sql db/a.sql 바꾼다 [G1] <!-- failed: R-T1 Verify -->", spec);
        Assert.Contains("- [ ] T2 asp web/a.asp#GetOne 바꾼다 [G1] <!-- failed: R-T2 Verify -->", spec);
        // Undone together: nothing is left out of step.
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("어긋나"));
    }

    [Fact]
    public async Task Group_without_cs_skips_build_verification()
    {
        // The page and the script belong to no project: a cs group here would be 「그룹 빌드 검증 불가」.
        Spec("- [ ] T1 sql db/a.sql 바꾼다 [G1]\n- [ ] T2 asp web/a.asp#GetOne 바꾼다 [G1]\n- [ ] T3 asp web/b.asp#DoIt 바꾼다 [G1]\n");
        var code = await Run(Opts());

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2", "T3"], _exec.Calls);
        Assert.Empty(_builds.Calls);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("그룹 빌드 검증 불가"));
    }

    [Fact]
    public async Task Group_without_cs_needs_no_group_build()
    {
        Spec("- [ ] T1 sql db/a.sql 바꾼다 [G1]\n- [ ] T2 asp web/a.asp#GetOne 바꾼다 [G1]\n");
        var code = await new SpecRunner(_fs, _guard, _exec, _ui, _time, 160)
            .RunAsync(SpecPath, Opts(), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("그룹 빌드 검증기"));
    }

    [Fact]
    public async Task Mixed_group_builds_only_cs_projects()
    {
        Spec("- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1]\n" +
             "- [ ] T2 asp site/a.asp#GetOne 바꾼다 [G1]\n- [ ] T3 sql db/a.sql 바꾼다 [G1]\n");
        var code = await Run(Opts());

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2", "T3"], _exec.Calls);
        Assert.Equal(2, _builds.Calls.Count);
        Assert.All(_builds.Calls, c =>
            Assert.Equal([Path.GetFullPath(P("src/App/App.csproj")), Path.GetFullPath(P("src/Web/Web.csproj"))], c));
    }

    [Fact]
    public async Task Mixed_group_reruns_only_cs_tasks_for_build_errors()
    {
        Spec("- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1]\n" +
             "- [ ] T2 asp site/a.asp#GetOne 바꾼다 [G1]\n");
        _builds.Results.Enqueue(Built());
        _builds.Results.Enqueue(Built(new Diagnostic("src/App/OrderService.cs", 7, 9, "error", "CS0029", "Cannot convert")));
        var code = await Run(Opts());

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2", "T1"], _exec.Calls);
        Assert.Contains("src/App/OrderService.cs(7): CS0029", _exec.Options[2].Group!.InitialFeedback!.Text);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("명세에 빠진 영향 대상"));
    }

    [Fact]
    public async Task Mixed_group_failing_asp_undoes_the_cs_task_too()
    {
        Spec("- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1]\n" +
             "- [ ] T2 asp site/a.asp#GetOne 바꾼다 [G1]\n");
        _exec.Exit["T2"] = 1;
        var code = await Run(Opts());

        Assert.Equal(1, code);
        Assert.Equal(OrderService, Read("src/App/OrderService.cs"));
        Assert.Equal(Page, Read("site/a.asp"));
        Assert.Single(_builds.Calls); // the baseline; no verification build after a failed member
    }

    [Fact]
    public async Task No_group_sql_applied_asp_failed_warns_mismatch()
    {
        Spec("- [ ] T1 sql db/a.sql 바꾼다\n- [ ] T2 asp web/a.asp#GetOne 바꾼다\n" +
             "- [ ] T3 asp web/b.asp#GetOne 바꾼다 [G1]\n- [ ] T4 sql db/b.sql 바꾼다 [G3]\n");
        _exec.Exit["T2"] = 1;
        var code = await Run(Opts());

        Assert.Equal(1, code);
        Assert.Contains("-- T1", Read("db/a.sql"));
        var warning = Assert.Single(_ui.Warnings, w => w.Contains("어긋나"));
        Assert.Equal("T1(sql)은 적용됐고 T2(asp)는 실패했습니다. SP와 페이지가 어긋나 있을 수 있습니다. 함께 되돌리려면 [G2]로 묶으십시오", warning);
        Assert.Equal(warning, _ui.Warnings[^1]); // at the end of the run
    }

    [Fact]
    public async Task Mismatch_warning_per_failed_asp_names_the_latest_applied_sql()
    {
        Spec("- [ ] T1 sql db/a.sql 바꾼다\n- [ ] T2 asp web/a.asp#GetOne 바꾼다\n" +
             "- [ ] T3 sql db/b.sql 바꾼다\n- [ ] T4 asp web/b.asp#GetOne 바꾼다\n");
        _exec.Exit["T2"] = 1;
        _exec.Exit["T4"] = 1;
        var code = await Run(Opts(cont: true));

        Assert.Equal(1, code);
        var warnings = _ui.Warnings.Where(w => w.Contains("어긋나")).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.StartsWith("T1(sql)은 적용됐고 T2(asp)는 실패했습니다.", warnings[0]);
        Assert.StartsWith("T3(sql)은 적용됐고 T4(asp)는 실패했습니다.", warnings[1]);
        Assert.EndsWith("[G1]로 묶으십시오", warnings[1]);
    }

    [Fact]
    public async Task No_mismatch_warning_without_an_applied_sql_task()
    {
        Spec("- [ ] T1 sql db/a.sql 바꾼다\n- [ ] T2 asp web/a.asp#GetOne 바꾼다\n");
        _exec.Exit["T1"] = 1;
        _exec.Exit["T2"] = 1;
        var code = await Run(Opts(cont: true));

        Assert.Equal(1, code);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("어긋나"));
    }

    // ── SP → ASP impact before the apply (A2 design §4.3) ──

    private static ApplyOptions Ask() => new(false, false, false, false, false, null, false);

    [Fact]
    public async Task Apply_without_production_still_prompts_for_asp_callers()
    {
        Make("web/c.asp", "<%\nrs.Open \"EXEC usp_A 1\", conn\n%>\n");
        Spec("- [ ] T1 sql db/a.sql 바꾼다\n");
        _ui.ConfirmAnswers.Enqueue(false);

        var code = await Run(Ask());

        Assert.Equal(2, code);
        Assert.Equal(["경고가 있습니다. 계속할까요?"], _ui.ConfirmMessages);
        Assert.Contains(_ui.Warnings, w => w.EndsWith("작업 T1의 영향 대상이 명세에 없습니다: web/c.asp:2 (usp_A 호출, 인수 1개)", StringComparison.Ordinal));
        Assert.Equal(Proc, Read("db/a.sql"));
        var list = Assert.Single(Directory.GetFiles(P(".pelly/runs"), "impact-sql.json", SearchOption.AllDirectories));
        Assert.Contains("web/c.asp", File.ReadAllText(list));
    }

    [Fact]
    public async Task Apply_records_asp_only_list_path_in_apply_json()
    {
        Make("web/c.asp", "<%\nrs.Open \"EXEC usp_A 1\", conn\n%>\n");
        Spec("- [ ] T1 sql db/a.sql 바꾼다\n");
        _ui.ConfirmAnswers.Enqueue(true);

        var code = await Run(Ask());

        Assert.Equal(0, code);
        var applyJson = Assert.Single(Directory.GetFiles(P(".pelly/runs"), "apply.json", SearchOption.AllDirectories));
        var runDir = Path.GetFileName(Path.GetDirectoryName(applyJson)!);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(applyJson));
        Assert.Equal($".pelly/runs/{runDir}/impact-sql.json", json.RootElement.GetProperty("sqlImpact").GetProperty("listPath").GetString());
    }

    [Fact]
    public async Task No_asp_pages_no_asp_warnings_and_no_new_prompt()
    {
        foreach (var page in new[] { "site/a.asp", "web/a.asp", "web/b.asp" }) File.Delete(P(page));
        Spec("- [ ] T1 sql db/a.sql 바꾼다\n");

        // No answer is queued: any confirmation fails the test.
        var code = await Run(Ask());

        Assert.Equal(0, code);
        Assert.Empty(_ui.ConfirmMessages);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("영향 대상"));
    }

    [Fact]
    public async Task Dry_run_asp_task()
    {
        Spec("- [ ] T1 asp web/a.asp#L2-4 줄을 바꾼다\n- [ ] T2 sql db/a.sql 바꾼다\n");
        var before = Read("changes/x.md");
        var seen = new List<RunRequest>();
        var code = await Run(Opts(dryRun: true), Recording(seen));

        Assert.Equal(0, code);
        var r = Assert.Single(seen);
        Assert.Equal(EditKind.Asp, r.Kind);
        Assert.True(r.DryRun);
        Assert.Equal(new TextSpanInfo(2, 4), r.Lines);
        Assert.Equal(before, Read("changes/x.md"));
        Assert.Equal(Page, Read("web/a.asp"));
    }

    private static GroupBuild Built(params Diagnostic[] errors) => new(true, null, null, errors);

    private sealed class FakeGroupVerifier : IGroupVerifier
    {
        public Queue<GroupBuild> Results { get; } = new();
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct)
        {
            Calls.Add(projects);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Built());
        }
    }

    /// <summary>
    /// Edits the task's file through FileGuard (a comment line with the task ID appended) and reports a scripted exit code.
    /// Run ids are <c>R-&lt;id&gt;</c>, then <c>R-&lt;id&gt;-2</c>, …
    /// </summary>
    private sealed class FakeExecutor(FileGuard guard) : ITaskExecutor
    {
        public List<string> Calls { get; } = [];
        public List<TaskRunOptions> Options { get; } = [];
        public Dictionary<string, int> Exit { get; } = [];
        public Dictionary<string, string> Display { get; } = [];

        public async Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct)
        {
            Calls.Add(task.Id);
            Options.Add(o);
            int n = Calls.Count(c => c == task.Id);
            string runId = n == 1 ? "R-" + task.Id : $"R-{task.Id}-{n}";
            var path = Path.Combine(ws.Root, task.Path.Replace('/', Path.DirectorySeparatorChar));
            var original = guard.Read(path);
            int exit = Exit.GetValueOrDefault(task.Id);
            string? display = Display.GetValueOrDefault(task.Id);
            if (exit != 0 || o.DryRun)
                return new RunResult(exit, runId, path, original, original, exit == 0 ? null : "Verify", display);
            var comment = task.Kind switch { TaskKind.Sql => "-- ", TaskKind.Asp => "' ", _ => "// " };
            var written = await guard.WriteAsync(original, original.Text + comment + task.Id + "\n", CancellationToken.None);
            return new RunResult(0, runId, path, original, written.Snapshot, null, display);
        }
    }
}
