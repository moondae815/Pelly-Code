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

/// <summary><c>pelly apply</c> with task groups (design §6 "작업 그룹"), over a fake executor and a fake group build.</summary>
public sealed class SpecRunnerGroupTests : IDisposable
{
    private const string Proc = "CREATE PROCEDURE dbo.usp_A AS SELECT 1;\n";

    // GetOrder spans lines 5-8, Cancel 10-12.
    private const string OrderService =
        "namespace App;\n\npublic class OrderService\n{\n    public int GetOrder(int id)\n    {\n        return id;\n    }\n\n    public void Cancel()\n    {\n    }\n}\n";

    // Find spans lines 5-8.
    private const string Repo =
        "namespace App;\n\npublic class Repo\n{\n    public int Find(int id)\n    {\n        return id;\n    }\n}\n";

    // Detail spans lines 5-8.
    private const string Controller =
        "namespace App;\n\npublic class Controller\n{\n    public int Detail()\n    {\n        return 0;\n    }\n}\n";

    private const string Group =
        "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1]\n" +
        "- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1]\n";

    private readonly string _root = Directory.CreateTempSubdirectory("pelly-group-").FullName;
    private readonly FileSystem _fs = new();
    private readonly FileGuard _guard;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero));
    private readonly ScriptedUserInteraction _ui = new() { IsInteractive = true };
    private readonly FakeExecutor _exec;
    private readonly FakeGroupVerifier _builds = new();

    public SpecRunnerGroupTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _guard = new FileGuard(_fs, TimeProvider.System);
        _exec = new FakeExecutor(_guard);
        _exec.Display["T1"] = "OrderService.GetOrder(int)";
        _exec.Display["T2"] = "Repo.Find(int)";
        _builds.ExecutorCalls = () => _exec.Calls.Count;
        Make("App.sln", "");
        Make("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Make("src/Web/Web.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../App/App.csproj\" /></ItemGroup></Project>");
        Make("src/App/OrderService.cs", OrderService);
        Make("src/App/Repo.cs", Repo);
        Make("src/App/Controller.cs", Controller);
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

    private string ArchivePath => P("changes/archive/2026-10-25-x.md");

    private void Spec(string tasks) =>
        Make("changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks);

    private static ApplyOptions Opts(bool cont = false, bool resume = false, bool reviewAtEnd = false, bool yes = true, bool dryRun = false,
        int? maxRetries = null) =>
        new(cont, resume, reviewAtEnd, yes, dryRun, maxRetries, false);

    private SpecRunner Runner(IGroupVerifier? groups) => new(_fs, _guard, _exec, _ui, _time, 160, groups: groups);

    private Task<int> Run(ApplyOptions o, CancellationToken? ct = null) =>
        Runner(_builds).RunAsync(SpecPath, o, ct ?? TestContext.Current.CancellationToken);

    private static Diagnostic Error(string file, int line, string code = "CS0029") => new(file, line, 9, "error", code, "Cannot convert");

    private static GroupBuild Built(params Diagnostic[] errors) => new(true, null, null, errors);

    private void AssertGroupFilesUntouched()
    {
        Assert.Equal(OrderService, Read("src/App/OrderService.cs"));
        Assert.Equal(Repo, Read("src/App/Repo.cs"));
    }

    [Fact]
    public async Task Group_passes_marks_all_tasks_done_together()
    {
        Spec(Group);
        var code = await Run(Opts());

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.All(_exec.Options, o =>
        {
            Assert.True(o.Yes);
            Assert.Null(o.Group!.InitialFeedback);
            Assert.Null(o.MemberOverride);
        });
        // Baseline before the first task, verification after the last, over the group's project and its dependents.
        Assert.Equal(2, _builds.Calls.Count);
        Assert.Equal([Path.GetFullPath(P("src/App/App.csproj")), Path.GetFullPath(P("src/Web/Web.csproj"))], _builds.Calls[0]);
        Assert.Equal(0, _builds.ExecutorCallsAtBaseline);
        var archived = File.ReadAllText(ArchivePath);
        Assert.Contains("- [x] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1] <!-- run: R-T1 -->", archived);
        Assert.Contains("- [x] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1] <!-- run: R-T2 -->", archived);
        Assert.Contains("// T1", Read("src/App/OrderService.cs"));
        Assert.Contains("// T2", Read("src/App/Repo.cs"));
        Assert.Empty(_ui.ConfirmMessages);
    }

    [Fact]
    public async Task Group_rerun_targets_the_renamed_member()
    {
        Spec(Group);
        _exec.Edit["T2"] = text => text.Replace("Find(", "Lookup(");
        _exec.Display["T2"] = "Repo.Lookup(int)";
        _builds.Results.Enqueue(Built());
        _builds.Results.Enqueue(Built(Error("src/App/Repo.cs", 7)));
        var code = await Run(Opts());

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2", "T2"], _exec.Calls);
        var rerun = _exec.Options[2];
        Assert.Equal("Lookup", rerun.MemberOverride);
        Assert.Contains("BUILD FAILED", rerun.Group!.InitialFeedback!.Text);
        Assert.Contains("src/App/Repo.cs(7): CS0029", rerun.Group.InitialFeedback.Text);
        Assert.Equal(3, _builds.Calls.Count);
        Assert.Contains("- [x] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1] <!-- run: R-T2-2 -->", File.ReadAllText(ArchivePath));
    }

    [Fact]
    public async Task Group_error_outside_group_targets_rolls_back_and_names_missing_target()
    {
        Spec(Group);
        _builds.Results.Enqueue(Built());
        _builds.Results.Enqueue(Built(Error("src/App/Controller.cs", 7, "CS1501")));
        var code = await Run(Opts());

        Assert.Equal(1, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        AssertGroupFilesUntouched();
        var warning = Assert.Single(_ui.Warnings, w => w.Contains("명세에 빠진 영향 대상"));
        Assert.Contains("src/App/Controller.cs#Detail", warning);
        var spec = Read("changes/x.md");
        Assert.Contains("- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1] <!-- failed: R-T1 GroupMissingTarget -->", spec);
        Assert.Contains("- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1] <!-- failed: R-T2 GroupMissingTarget -->", spec);
        Assert.Contains("status: in-progress", spec);
    }

    [Fact]
    public async Task Error_inside_a_helper_the_task_added_reruns_that_task_with_the_helper_editable()
    {
        Spec(Group);
        // T1 adds a private helper after GetOrder (lines 10-13 once written); the group build finds an error inside it.
        _exec.Edit["T1"] = text => _exec.Calls.Count(c => c == "T1") > 1
            ? text
            : text.Replace("        return id;\n    }\n\n    public void Cancel()",
                "        return Half(id);\n    }\n\n    private int Half(int id)\n    {\n        return id / 2L;\n    }\n\n    public void Cancel()");
        _exec.Helpers["T1"] = ["OrderService.Half(int)"];
        _builds.Results.Enqueue(Built());
        _builds.Results.Enqueue(Built(Error("src/App/OrderService.cs", 12, "CS0266")));
        var code = await Run(Opts());

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2", "T1"], _exec.Calls);
        var rerun = _exec.Options[2];
        Assert.Equal(["OrderService.Half(int)"], rerun.Group!.EditableHelpers!);
        Assert.Contains("src/App/OrderService.cs(12): CS0266", rerun.Group.InitialFeedback!.Text);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("명세에 빠진 영향 대상"));
    }

    [Fact]
    public async Task Group_retry_budget_exhausted_rolls_back_exit_1()
    {
        Spec(Group);
        _builds.Results.Enqueue(Built());
        _builds.Fallback = Built(Error("src/App/Repo.cs", 7));
        var code = await Run(Opts(maxRetries: 1));

        Assert.Equal(1, code);
        Assert.Equal(["T1", "T2", "T2"], _exec.Calls);
        AssertGroupFilesUntouched();
        var spec = Read("changes/x.md");
        Assert.Contains("<!-- failed: R-T1 GroupBuildFailed -->", spec);
        Assert.Contains("<!-- failed: R-T2-2 GroupBuildFailed -->", spec);
        Assert.Contains(_ui.Warnings, w => w.Contains("G1") && w.Contains("CS0029"));
    }

    [Fact]
    public async Task Unverifiable_group_exits_4_before_any_edit()
    {
        Spec(Group);
        _builds.Results.Enqueue(new GroupBuild(false, "그룹 빌드 검증 불가 (src/App/App.csproj): 레거시", GroupVerifier.UnverifiableKind, []));
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        AssertGroupFilesUntouched();
        Assert.Contains(_ui.Warnings, w => w.Contains("그룹 빌드 검증 불가 (src/App/App.csproj): 레거시"));
        var spec = Read("changes/x.md");
        Assert.Matches(@"- \[ \] T1 cs \S+ 반환 형식을 바꾼다 \[G1\] <!-- failed: apply-\S+ GroupUnverifiable -->", spec);
        Assert.Matches(@"- \[ \] T2 cs \S+ 이름을 바꾼다 \[G1\] <!-- failed: apply-\S+ GroupUnverifiable -->", spec);
        Assert.False(Directory.Exists(P(".pelly/backup")) && Directory.EnumerateFiles(P(".pelly/backup"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Group_file_outside_any_project_is_unverifiable()
    {
        Make("Loose.cs", Repo);
        Spec("- [ ] T1 cs Loose.cs#Repo.Find 이름을 바꾼다 [G1]\n");
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        Assert.Empty(_builds.Calls);
        Assert.Contains(_ui.Warnings, w => w.Contains("그룹 빌드 검증 불가") && w.Contains("Loose.cs"));
        Assert.Contains("GroupUnverifiable -->", Read("changes/x.md"));
    }

    [Fact]
    public async Task Unknown_project_scope_is_unverifiable_before_any_edit()
    {
        // MSBuild properties in a ProjectReference: whether Tools depends on App cannot be read without evaluating MSBuild.
        Make("src/Tools/Tools.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"$(SolutionDir)src\\App\\App.csproj\" /></ItemGroup></Project>");
        Spec(Group);
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        Assert.Empty(_builds.Calls);
        AssertGroupFilesUntouched();
        Assert.Contains(_ui.Warnings, w => w.Contains("그룹 빌드 검증 불가") && w.Contains("src/Tools/Tools.csproj") && w.Contains("$(SolutionDir)"));
        Assert.Contains("GroupUnverifiable -->", Read("changes/x.md"));
    }

    [Fact]
    public async Task Missing_project_referenced_by_a_scope_project_is_unverifiable_before_any_edit()
    {
        Make("src/Web/Web.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../App/App.csproj\" /><ProjectReference Include=\"..\\Gone\\Gone.csproj\" /></ItemGroup></Project>");
        Spec(Group);
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        Assert.Empty(_builds.Calls);
        AssertGroupFilesUntouched();
        Assert.Contains(_ui.Warnings, w => w.Contains("그룹 빌드 검증 불가") && w.Contains("src/Web/Web.csproj") && w.Contains("src/Gone/Gone.csproj"));
        Assert.Contains("GroupUnverifiable -->", Read("changes/x.md"));
    }

    [Fact]
    public async Task Link_folder_pointing_outside_the_workspace_is_unverifiable_before_any_edit()
    {
        var outside = Directory.CreateTempSubdirectory("pelly-group-outside-").FullName;
        try
        {
            try { Directory.CreateSymbolicLink(P("shared"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip($"symbolic links cannot be created here ({ex.Message})");
            }
            Spec(Group);
            var code = await Run(Opts());

            Assert.Equal(4, code);
            Assert.Empty(_exec.Calls);
            Assert.Empty(_builds.Calls);
            AssertGroupFilesUntouched();
            Assert.Contains(_ui.Warnings, w => w.Contains("그룹 빌드 검증 불가") && w.Contains("링크 폴더") && w.Contains("shared"));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task Baseline_error_in_a_referenced_scope_project_is_unverifiable_before_any_edit()
    {
        // App does not compile, so the build of Web (which references App) never compiles Web: its breakage would be hidden.
        Spec(Group);
        _builds.Results.Enqueue(Built(Error("src/App/Controller.cs", 7, "CS0103")));
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        Assert.Single(_builds.Calls);
        AssertGroupFilesUntouched();
        Assert.Contains(_ui.Warnings, w => w.Contains("그룹 빌드 검증 불가") && w.Contains("참조 프로젝트 src/App/App.csproj에 이미 컴파일 오류가 있어 이를 참조하는 프로젝트를 검증할 수 없습니다"));
        Assert.Contains("GroupUnverifiable -->", Read("changes/x.md"));
    }

    [Fact]
    public async Task Baseline_error_in_a_scope_project_nothing_references_still_runs_the_group()
    {
        Make("src/Web/Page.cs", Controller);
        Spec(Group);
        _builds.Results.Enqueue(Built(Error("src/Web/Page.cs", 7, "CS0103")));
        _builds.Results.Enqueue(Built(Error("src/Web/Page.cs", 7, "CS0103")));
        var code = await Run(Opts());

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
    }

    [Fact]
    public async Task Unverifiable_group_build_after_edits_rolls_back_exit_4()
    {
        Spec(Group);
        _builds.Results.Enqueue(Built());
        _builds.Results.Enqueue(new GroupBuild(false, "그룹 빌드 검증 불가 (src/App/App.csproj): 시간 초과", GroupVerifier.UnverifiableKind, []));
        var code = await Run(Opts());

        Assert.Equal(4, code);
        AssertGroupFilesUntouched();
        Assert.Contains(_ui.Warnings, w => w.Contains("시간 초과"));
        var spec = Read("changes/x.md");
        Assert.Contains("<!-- failed: R-T1 GroupUnverifiable -->", spec);
        Assert.Contains("<!-- failed: R-T2 GroupUnverifiable -->", spec);
    }

    [Fact]
    public async Task Group_task_failure_rolls_back_whole_group()
    {
        Spec(Group);
        _exec.Exit["T2"] = 1;
        var code = await Run(Opts());

        Assert.Equal(1, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.Single(_builds.Calls); // the baseline only
        AssertGroupFilesUntouched();
        var spec = Read("changes/x.md");
        Assert.Contains("<!-- failed: R-T1 Verify -->", spec);
        Assert.Contains("<!-- failed: R-T2 Verify -->", spec);
    }

    [Fact]
    public async Task Group_rejection_rolls_back_exit_2()
    {
        Spec(Group);
        _ui.ConfirmAnswers.Enqueue(false);
        var code = await Run(Opts(yes: false));

        Assert.Equal(2, code);
        Assert.Equal(["이 그룹을 적용할까요?"], _ui.ConfirmMessages);
        Assert.Equal(2, _ui.Diffs.Count);
        Assert.Equal(OrderService, _ui.Diffs[0].Before);
        Assert.Contains("// T1", _ui.Diffs[0].After);
        AssertGroupFilesUntouched();
        var spec = Read("changes/x.md");
        Assert.Contains("<!-- failed: R-T1 GroupRejected -->", spec);
        Assert.Contains("<!-- failed: R-T2 GroupRejected -->", spec);
    }

    [Fact]
    public async Task Group_approval_keeps_changes()
    {
        Spec(Group);
        _ui.ConfirmAnswers.Enqueue(true);
        var code = await Run(Opts(yes: false));

        Assert.Equal(0, code);
        Assert.All(_exec.Options, o => Assert.True(o.Yes));
        Assert.Contains("// T1", Read("src/App/OrderService.cs"));
        Assert.Contains("status: done", File.ReadAllText(ArchivePath));
    }

    [Fact]
    public async Task Review_at_end_skips_group_question()
    {
        Spec("- [ ] T0 sql db/a.sql 첫째\n" + Group);
        _ui.ConfirmAnswers.Enqueue(true);
        var code = await Run(Opts(reviewAtEnd: true, yes: false));

        Assert.Equal(0, code);
        Assert.Equal(["모두 적용할까요?"], _ui.ConfirmMessages);
        Assert.Equal(3, _ui.Diffs.Count);
        Assert.Contains("status: done", File.ReadAllText(ArchivePath));
    }

    [Fact]
    public async Task Review_at_end_reject_rolls_back_the_group_too()
    {
        Spec(Group);
        _ui.ConfirmAnswers.Enqueue(false);
        var code = await Run(Opts(reviewAtEnd: true, yes: false));

        Assert.Equal(2, code);
        AssertGroupFilesUntouched();
        var spec = Read("changes/x.md");
        Assert.Contains("status: draft", spec);
        Assert.DoesNotContain("<!--", spec);
    }

    [Fact]
    public async Task Continue_after_group_failure_stops_when_later_task_targets_group_file()
    {
        Spec(Group + "- [ ] T3 cs src/App/OrderService.cs#OrderService.Cancel 바꾼다\n- [ ] T4 sql db/a.sql 넷째\n");
        _exec.Exit["T2"] = 1;
        var code = await Run(Opts(cont: true));

        Assert.Equal(1, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        var spec = Read("changes/x.md");
        Assert.Contains("- [ ] T3 cs src/App/OrderService.cs#OrderService.Cancel 바꾼다\n", spec);
        Assert.Contains(_ui.Warnings, w => w.Contains("T3"));
    }

    [Fact]
    public async Task Continue_after_group_failure_runs_later_tasks_on_other_files()
    {
        Spec(Group + "- [ ] T3 sql db/a.sql 셋째\n");
        _exec.Exit["T2"] = 1;
        var code = await Run(Opts(cont: true));

        Assert.Equal(1, code);
        Assert.Equal(["T1", "T2", "T3"], _exec.Calls);
        Assert.Contains("- [x] T3 sql db/a.sql 셋째 <!-- run: R-T3 -->", Read("changes/x.md"));
    }

    [Fact]
    public async Task Continue_stops_after_unverifiable_group()
    {
        Spec(Group + "- [ ] T3 sql db/a.sql 셋째\n");
        _builds.Results.Enqueue(new GroupBuild(false, "그룹 빌드 검증 불가 (src/App/App.csproj): 레거시", GroupVerifier.UnverifiableKind, []));
        var code = await Run(Opts(cont: true));

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
    }

    private System.Text.Json.JsonElement ReadApply()
    {
        var file = Directory.GetFiles(P(".pelly/runs"), "apply.json", SearchOption.AllDirectories).Single();
        return System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
    }

    [Fact]
    public async Task Apply_json_records_group_task_count_and_resumed()
    {
        Spec(Group + "- [ ] T3 sql db/a.sql 셋째\n");
        Assert.Equal(0, await Run(Opts()));

        var a = ReadApply();
        Assert.Equal(3, a.GetProperty("taskCount").GetInt32());
        Assert.False(a.GetProperty("resumed").GetBoolean());
        var tasks = a.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal("G1", tasks[0].GetProperty("group").GetString());
        Assert.Equal("G1", tasks[1].GetProperty("group").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, tasks[2].GetProperty("group").ValueKind);
    }

    [Fact]
    public async Task Apply_json_records_resume()
    {
        Spec("- [x] T0 sql db/a.sql 첫째 <!-- run: R-OLD -->\n" + Group);
        Assert.Equal(0, await Run(Opts(resume: true)));

        var a = ReadApply();
        Assert.True(a.GetProperty("resumed").GetBoolean());
        Assert.Equal(3, a.GetProperty("taskCount").GetInt32());
    }

    [Fact]
    public async Task Apply_json_marks_group_tasks_with_the_group_failure_kind()
    {
        Spec(Group);
        _builds.Results.Enqueue(Built());
        _builds.Results.Enqueue(Built(Error("src/App/Controller.cs", 7, "CS1501")));
        Assert.Equal(1, await Run(Opts()));

        var tasks = ReadApply().GetProperty("tasks").EnumerateArray().ToList();
        Assert.All(tasks, t => Assert.Equal("GroupMissingTarget", t.GetProperty("failureKind").GetString()));
        Assert.All(tasks, t => Assert.Equal("G1", t.GetProperty("group").GetString()));
    }

    [Fact]
    public async Task Apply_json_records_an_unverifiable_group_that_never_ran()
    {
        Spec(Group);
        _builds.Results.Enqueue(new GroupBuild(false, "그룹 빌드 검증 불가 (src/App/App.csproj): 레거시", GroupVerifier.UnverifiableKind, []));
        Assert.Equal(4, await Run(Opts()));

        var tasks = ReadApply().GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t =>
        {
            Assert.Equal("GroupUnverifiable", t.GetProperty("failureKind").GetString());
            Assert.Equal("G1", t.GetProperty("group").GetString());
        });
    }

    [Fact]
    public async Task Resume_after_failed_group_restarts_at_group_start()
    {
        Spec("- [x] T0 sql db/a.sql 첫째 <!-- run: R-OLD -->\n" +
             "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1] <!-- failed: R-X GroupBuildFailed -->\n" +
             "- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1] <!-- failed: R-Y GroupBuildFailed -->\n");
        var code = await Run(Opts(resume: true));

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        var archived = File.ReadAllText(ArchivePath);
        Assert.Contains("<!-- run: R-OLD -->", archived);
        Assert.Contains("[G1] <!-- run: R-T1 -->", archived);
        Assert.Contains("[G1] <!-- run: R-T2 -->", archived);
    }

    [Fact]
    public async Task Resume_after_a_renaming_group_runs_the_remaining_tasks()
    {
        // G1 renamed Repo.Find to Repo.Lookup and kept its [x]: the spec still names the old symbol.
        Make("src/App/Repo.cs", Repo.Replace("Find(", "Lookup("));
        Spec("- [x] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1] <!-- run: R-A -->\n" +
             "- [x] T2 cs src/App/Repo.cs#Repo.Find 이름을 Lookup으로 바꾼다 [G1] <!-- run: R-B -->\n" +
             "- [ ] T3 sql db/a.sql 셋째\n");
        var code = await Run(Opts(resume: true));

        Assert.Equal(0, code);
        Assert.Equal(["T3"], _exec.Calls);
        Assert.Contains("- [x] T3 sql db/a.sql 셋째 <!-- run: R-T3 -->", File.ReadAllText(ArchivePath));
    }

    [Fact]
    public async Task Non_group_tasks_before_and_after_group_still_run()
    {
        Spec("- [ ] T0 sql db/a.sql 첫째\n" + Group + "- [ ] T3 sql db/b.sql 셋째\n");
        var code = await Run(Opts());

        Assert.Equal(0, code);
        Assert.Equal(["T0", "T1", "T2", "T3"], _exec.Calls);
        Assert.Null(_exec.Options[0].Group);
        Assert.NotNull(_exec.Options[1].Group);
        Assert.NotNull(_exec.Options[2].Group);
        Assert.Null(_exec.Options[3].Group);
        var archived = File.ReadAllText(ArchivePath);
        Assert.Contains("- [x] T0 sql db/a.sql 첫째 <!-- run: R-T0 -->", archived);
        Assert.Contains("- [x] T3 sql db/b.sql 셋째 <!-- run: R-T3 -->", archived);
    }

    [Fact]
    public async Task Group_rollback_with_outside_change_keeps_marks_for_unrestored_files_exit_4()
    {
        Spec(Group);
        // Saved in an IDE after T1 wrote it: the group rollback must not overwrite it.
        _exec.During["T2"] = () => File.AppendAllText(P("src/App/OrderService.cs"), "// outside\n");
        _builds.Results.Enqueue(Built());
        _builds.Results.Enqueue(Built(Error("src/App/Controller.cs", 7, "CS1501")));
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Equal(OrderService + "// T1\n// outside\n", Read("src/App/OrderService.cs"));
        Assert.Equal(Repo, Read("src/App/Repo.cs"));
        var spec = Read("changes/x.md");
        Assert.Contains("- [x] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1] <!-- run: R-T1 -->", spec);
        Assert.Contains("- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1] <!-- failed: R-T2 GroupMissingTarget -->", spec);
        var warning = Assert.Single(_ui.Warnings, w => w.Contains("그룹 일부가 적용된 채 남아 빌드가 깨져 있을 수 있습니다"));
        Assert.Contains("OrderService.cs", warning);
        Assert.Contains("T1", warning);
        Assert.DoesNotContain("Repo.cs", warning);
    }

    [Fact]
    public async Task Group_rollback_names_unrestored_files_even_when_the_marks_cannot_be_written()
    {
        Spec(Group);
        _exec.Exit["T2"] = 1;
        _exec.During["T2"] = () =>
        {
            File.AppendAllText(P("src/App/OrderService.cs"), "// outside\n");
            File.AppendAllText(SpecPath, "\n메모\n"); // the spec is edited too, so writing the marks fails
        };
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Equal(OrderService + "// T1\n// outside\n", Read("src/App/OrderService.cs"));
        var warning = Assert.Single(_ui.Warnings, w => w.Contains("그룹 일부가 적용된 채 남아 빌드가 깨져 있을 수 있습니다"));
        Assert.Contains("OrderService.cs", warning);
        Assert.Contains(_ui.Warnings, w => w.Contains("명세 파일이 실행 중에 바뀌었습니다"));
    }

    [Fact]
    public async Task Failure_while_undoing_after_an_unexpected_exception_keeps_the_original_exception()
    {
        Spec(Group);
        _exec.Throw["T2"] = new InvalidOperationException("boom");
        _exec.During["T2"] = () =>
        {
            File.AppendAllText(P("src/App/OrderService.cs"), "// outside\n");
            _ui.WarnThrows = new FormatException("markup"); // the undo's warning about that file throws
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Opts()));

        Assert.Equal("boom", ex.Message);
        Assert.Equal(Repo, Read("src/App/Repo.cs"));
    }

    [Fact]
    public async Task Cancellation_inside_a_group_rolls_the_group_back()
    {
        Spec("- [ ] T0 sql db/a.sql 첫째\n" + Group);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _exec.During["T2"] = () => cts.Cancel();
        _exec.Exit["T2"] = 2;
        var code = await Run(Opts(), cts.Token);

        Assert.Equal(2, code);
        Assert.Equal(["T0", "T1", "T2"], _exec.Calls);
        AssertGroupFilesUntouched();
        Assert.Contains("-- T0", Read("db/a.sql"));
        var spec = Read("changes/x.md");
        Assert.Contains("- [x] T0 sql db/a.sql 첫째 <!-- run: R-T0 -->", spec);
        Assert.Contains("반환 형식을 바꾼다 [G1]\n", spec);
        Assert.Contains("이름을 바꾼다 [G1]\n", spec);
    }

    [Fact]
    public async Task Executor_cancellation_exception_inside_a_group_rolls_the_group_back()
    {
        Spec(Group);
        _exec.Throw["T2"] = new OperationCanceledException();
        var code = await Run(Opts());

        Assert.Equal(2, code);
        AssertGroupFilesUntouched();
        Assert.DoesNotContain("<!--", Read("changes/x.md"));
    }

    [Fact]
    public async Task Unexpected_exception_inside_a_group_rolls_the_group_back_and_propagates()
    {
        Spec(Group);
        _exec.Throw["T2"] = new InvalidOperationException("boom");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Opts()));

        Assert.Equal("boom", ex.Message);
        AssertGroupFilesUntouched();
    }

    [Fact]
    public async Task Rerun_feedback_quotes_the_file_as_the_verification_build_saw_it()
    {
        Spec("- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1]\n" +
             "- [ ] T2 cs src/App/OrderService.cs#OrderService.Cancel 매개변수를 더한다 [G1]\n");
        _exec.Display["T2"] = "OrderService.Cancel()";
        // T1's re-run shifts every line down by two before T2's re-run.
        _exec.Edit["T1"] = text => _exec.Calls.Count(c => c == "T1") > 1 ? "// a\n// b\n" + text : text;
        _builds.Results.Enqueue(Built());
        _builds.Results.Enqueue(Built(Error("src/App/OrderService.cs", 7), Error("src/App/OrderService.cs", 11)));
        var code = await Run(Opts());

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2", "T1", "T2"], _exec.Calls);
        var t2 = _exec.Options[3].Group!.InitialFeedback!.Text;
        Assert.Contains("src/App/OrderService.cs(11): CS0029", t2);
        Assert.Contains("> 11 |     {", t2);
    }

    [Fact]
    public async Task Group_without_verifier_exits_4_before_any_edit()
    {
        Spec(Group);
        var before = Read("changes/x.md");
        var code = await Runner(null).RunAsync(SpecPath, Opts(), TestContext.Current.CancellationToken);

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        Assert.Equal(before, Read("changes/x.md"));
        Assert.Contains(_ui.Warnings, w => w.Contains("그룹 빌드 검증기를 사용할 수 없습니다"));
    }

    [Fact]
    public async Task Dry_run_of_a_group_task_runs_it_alone_in_group_mode()
    {
        Spec(Group);
        var before = Read("changes/x.md");
        var code = await Run(Opts(dryRun: true));

        Assert.Equal(0, code);
        Assert.Equal(["T1"], _exec.Calls);
        Assert.True(_exec.Options[0].DryRun);
        Assert.NotNull(_exec.Options[0].Group);
        Assert.Empty(_builds.Calls);
        Assert.Equal(before, Read("changes/x.md"));
    }

    private const string OneFileGroup =
        "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1]\n" +
        "- [ ] T2 cs src/App/OrderService.cs#OrderService.Cancel 매개변수를 더한다 [G1]\n";

    [Fact]
    public async Task Spec_write_failure_after_a_passing_group_keeps_the_edits_exit_4()
    {
        Spec(Group);
        _exec.During["T2"] = () => File.AppendAllText(SpecPath, "\n메모\n"); // edited while the group ran
        var code = await Run(Opts());

        Assert.Equal(4, code);
        Assert.Contains("// T1", Read("src/App/OrderService.cs"));
        Assert.Contains("// T2", Read("src/App/Repo.cs"));
        Assert.Contains(_ui.Warnings, w => w.Contains("명세 파일이 실행 중에 바뀌었습니다")
            && w.Contains("그룹 G1은 적용됐지만 체크 표시를 남기지 못했습니다"));
        Assert.DoesNotContain("[x]", Read("changes/x.md"));
        Assert.False(File.Exists(ArchivePath));
    }

    [Fact]
    public async Task Spec_write_failure_while_marking_an_unverifiable_group_exits_4_before_any_edit()
    {
        Spec(Group);
        _builds.Results.Enqueue(new GroupBuild(false, "그룹 빌드 검증 불가 (src/App/App.csproj): 레거시", GroupVerifier.UnverifiableKind, []));
        _builds.During = () => File.AppendAllText(SpecPath, "\n메모\n");
        var code = await Run(Opts(cont: true));

        Assert.Equal(4, code);
        Assert.Empty(_exec.Calls);
        AssertGroupFilesUntouched();
        Assert.Contains(_ui.Warnings, w => w.Contains("명세 파일이 실행 중에 바뀌었습니다"));
        Assert.DoesNotContain("GroupUnverifiable", Read("changes/x.md"));
    }

    [Fact]
    public async Task Cancellation_with_a_file_changed_outside_keeps_it_and_its_mark_exit_4()
    {
        Spec(Group);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _exec.During["T2"] = () =>
        {
            File.AppendAllText(P("src/App/OrderService.cs"), "// outside\n");
            cts.Cancel();
        };
        _exec.Exit["T2"] = 2;
        var code = await Run(Opts(), cts.Token);

        Assert.Equal(4, code);
        Assert.Equal(OrderService + "// T1\n// outside\n", Read("src/App/OrderService.cs"));
        Assert.Equal(Repo, Read("src/App/Repo.cs"));
        var spec = Read("changes/x.md");
        Assert.Contains("- [x] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1] <!-- run: R-T1 -->", spec);
        Assert.Contains("- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1]\n", spec);
        var warning = Assert.Single(_ui.Warnings, w => w.Contains("그룹 일부가 적용된 채 남아 빌드가 깨져 있을 수 있습니다"));
        Assert.Contains("OrderService.cs", warning);
    }

    [Fact]
    public async Task Resume_of_a_partly_done_group_runs_only_its_unchecked_tasks_as_the_group()
    {
        // A group rollback kept T1's [x] because its file was changed outside pelly; T2 was rolled back.
        Make("src/App/OrderService.cs", OrderService + "// T1\n// outside\n");
        Spec("- [x] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1] <!-- run: R-A -->\n" +
             "- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1] <!-- failed: R-B GroupMissingTarget -->\n");
        var code = await Run(Opts(resume: true));

        Assert.Equal(0, code);
        Assert.Equal(["T2"], _exec.Calls);
        Assert.NotNull(_exec.Options[0].Group);
        Assert.Equal(2, _builds.Calls.Count); // baseline and verification of the remaining task's scope
        Assert.Equal(0, _builds.ExecutorCallsAtBaseline);
        var archived = File.ReadAllText(ArchivePath);
        Assert.Contains("[G1] <!-- run: R-A -->", archived);
        Assert.Contains("- [x] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1] <!-- run: R-T2 -->", archived);
    }

    [Fact]
    public async Task Two_tasks_of_a_group_on_one_file_roll_back_to_the_file_before_the_group()
    {
        Spec(OneFileGroup);
        _exec.Display["T2"] = "OrderService.Cancel()";
        _builds.Results.Enqueue(Built());
        _builds.Results.Enqueue(Built(Error("src/App/Controller.cs", 7, "CS1501")));
        var code = await Run(Opts());

        Assert.Equal(1, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.Equal(OrderService, Read("src/App/OrderService.cs"));
        var spec = Read("changes/x.md");
        Assert.Contains("<!-- failed: R-T1 GroupMissingTarget -->", spec);
        Assert.Contains("<!-- failed: R-T2 GroupMissingTarget -->", spec);
    }

    [Fact]
    public async Task Two_tasks_of_a_group_on_one_file_show_one_diff_with_both_edits()
    {
        Spec(OneFileGroup);
        _exec.Display["T2"] = "OrderService.Cancel()";
        _ui.ConfirmAnswers.Enqueue(true);
        var code = await Run(Opts(yes: false));

        Assert.Equal(0, code);
        var diff = Assert.Single(_ui.Diffs);
        Assert.Equal(OrderService, diff.Before);
        Assert.Equal(OrderService + "// T1\n// T2\n", diff.After);
        Assert.Equal(OrderService + "// T1\n// T2\n", Read("src/App/OrderService.cs"));
        Assert.Contains("<!-- run: R-T2 -->", File.ReadAllText(ArchivePath));
    }

    /// <summary>Returns scripted builds in order (then <see cref="Fallback"/>) and records each build's projects.</summary>
    private sealed class FakeGroupVerifier : IGroupVerifier
    {
        public Queue<GroupBuild> Results { get; } = new();
        public GroupBuild Fallback { get; set; } = new(true, null, null, []);
        public List<IReadOnlyList<string>> Calls { get; } = [];

        /// <summary>Executor calls made before the first (baseline) build ran.</summary>
        public int ExecutorCallsAtBaseline { get; private set; } = -1;

        public Func<int>? ExecutorCalls { get; set; }

        /// <summary>Runs inside every build (e.g. to edit the spec while the build runs).</summary>
        public Action? During { get; set; }

        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct)
        {
            During?.Invoke();
            if (Calls.Count == 0) ExecutorCallsAtBaseline = ExecutorCalls?.Invoke() ?? -1;
            Calls.Add(projects);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Fallback);
        }
    }

    /// <summary>
    /// Edits the task's file through FileGuard (<see cref="Edit"/>, then <c>// &lt;id&gt;</c> or <c>-- &lt;id&gt;</c> appended)
    /// and reports a scripted exit code and target display. Run ids are <c>R-&lt;id&gt;</c>, then <c>R-&lt;id&gt;-2</c>, …
    /// </summary>
    private sealed class FakeExecutor(FileGuard guard) : ITaskExecutor
    {
        public List<string> Calls { get; } = [];
        public List<TaskRunOptions> Options { get; } = [];
        public Dictionary<string, int> Exit { get; } = [];
        public Dictionary<string, Action> During { get; } = [];
        public Dictionary<string, Exception> Throw { get; } = [];
        public Dictionary<string, Func<string, string>> Edit { get; } = [];
        public Dictionary<string, string> Display { get; } = [];
        public Dictionary<string, IReadOnlyList<string>> Helpers { get; } = [];

        public async Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct)
        {
            Calls.Add(task.Id);
            Options.Add(o);
            int n = Calls.Count(c => c == task.Id);
            string runId = n == 1 ? "R-" + task.Id : $"R-{task.Id}-{n}";
            if (During.TryGetValue(task.Id, out var during)) during();
            if (Throw.TryGetValue(task.Id, out var ex)) throw ex;
            var path = Path.Combine(ws.Root, task.Path.Replace('/', Path.DirectorySeparatorChar));
            var original = guard.Read(path);
            int exit = Exit.GetValueOrDefault(task.Id);
            string? display = Display.GetValueOrDefault(task.Id);
            if (exit != 0 || o.DryRun)
                return new RunResult(exit, runId, path, original, original, exit == 0 ? null : "Verify", display);
            var text = Edit.TryGetValue(task.Id, out var edit) ? edit(original.Text) : original.Text;
            var comment = task.Kind == TaskKind.Sql ? "-- " : "// ";
            var written = await guard.WriteAsync(original, text + comment + task.Id + "\n", CancellationToken.None);
            return new RunResult(0, runId, path, original, written.Snapshot, null, display, Helpers.GetValueOrDefault(task.Id));
        }
    }
}
