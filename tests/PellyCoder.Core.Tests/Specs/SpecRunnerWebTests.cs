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

/// <summary><c>pelly apply</c> with js and css tasks (W3 design §4.1): executor branches, groups that undo together, apply.json.</summary>
public sealed class SpecRunnerWebTests : IDisposable
{
    private const string OrderJs = "function fnSearch(f) {\n  return f;\n}\n";

    private const string ListPage =
        "<%@ Language=\"VBScript\" %>\n" +
        "<%\nSub Search()\nEnd Sub\n%>\n" +
        "<script>\n" +
        "function goPage(n) {\n" +
        "  fnSearch(document.f, n);\n" +
        "}\n" +
        "</script>\n";

    private const string Proc = "CREATE PROCEDURE dbo.usp_A AS SELECT 1;\n";

    private readonly string _root = Directory.CreateTempSubdirectory("pelly-web-apply-").FullName;
    private readonly FileSystem _fs = new();
    private readonly FileGuard _guard;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero));
    private readonly ScriptedUserInteraction _ui = new() { IsInteractive = true };
    private readonly FakeExecutor _exec;
    private readonly FakeGroupVerifier _builds = new();

    public SpecRunnerWebTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _guard = new FileGuard(_fs, TimeProvider.System);
        _exec = new FakeExecutor(_guard);
        Make("App.sln", "");
        Make("web/js/order.js", OrderJs);
        Make("web/css/admin.css", ".search-box { width: 100px; }\n");
        Make("web/admin/list.asp", ListPage);
        Make("db/a.sql", Proc);
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

    private string ArchivePath => P("changes/archive/2026-11-10-x.md");

    private void Spec(string tasks) =>
        Make("changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks);

    private static ApplyOptions Opts(bool cont = false) => new(cont, false, false, true, false, null, false);

    private Task<int> Run(ApplyOptions o, ITaskExecutor? executor = null) =>
        new SpecRunner(_fs, _guard, executor ?? _exec, _ui, _time, 160, groups: _builds)
            .RunAsync(SpecPath, o, TestContext.Current.CancellationToken);

    private static FsmTaskExecutor Recording(List<RunRequest> seen) => new((r, _) =>
    {
        seen.Add(r);
        return Task.FromResult(new RunResult(0, "R-" + seen.Count, r.FilePath, null, null, null));
    });

    private System.Text.Json.JsonDocument ApplyJson() =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(P(".pelly/runs"), "apply.json", SearchOption.AllDirectories))));

    [Fact]
    public async Task Apply_JsAndCssTasks_RunInOrder_MarkDone()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch 바꾼다\n- [ ] T2 css web/css/admin.css#.search-box 넓힌다\n");
        Assert.Equal(0, await Run(Opts()));
        Assert.Equal(["T1", "T2"], _exec.Calls);
        var archived = File.ReadAllText(ArchivePath);
        Assert.Contains("- [x] T1 js web/js/order.js#fnSearch 바꾼다 <!-- run: R-T1 -->", archived);
        Assert.Contains("- [x] T2 css web/css/admin.css#.search-box 넓힌다 <!-- run: R-T2 -->", archived);
    }

    [Fact]
    public async Task Group_SqlAspJs_JsFails_AllRestoredToOriginalBytes()
    {
        var sql = Encoding.GetEncoding(949).GetBytes("-- 주문 목록\r\nCREATE PROCEDURE dbo.usp_A AS SELECT 1;\r\n");
        File.WriteAllBytes(P("db/a.sql"), sql);
        var page = File.ReadAllBytes(P("web/admin/list.asp"));
        var js = File.ReadAllBytes(P("web/js/order.js"));
        Spec("- [ ] T1 sql db/a.sql 바꾼다 [G1]\n- [ ] T2 asp web/admin/list.asp#Search 바꾼다 [G1]\n- [ ] T3 js web/js/order.js#fnSearch 바꾼다 [G1]\n");
        _exec.Exit["T3"] = 1;
        Assert.Equal(1, await Run(Opts()));
        Assert.Equal(["T1", "T2", "T3"], _exec.Calls);
        Assert.Equal(sql, File.ReadAllBytes(P("db/a.sql")));
        Assert.Equal(page, File.ReadAllBytes(P("web/admin/list.asp")));
        Assert.Equal(js, File.ReadAllBytes(P("web/js/order.js")));
    }

    [Fact]
    public async Task Group_JsOnly_NoBuild()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch 바꾼다 [G1]\n- [ ] T2 css web/css/admin.css#.search-box 넓힌다 [G1]\n");
        Assert.Equal(0, await Run(Opts()));
        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.Empty(_builds.Calls);
        Assert.Contains("// T1", Read("web/js/order.js"));
    }

    [Fact]
    public async Task ApplyJson_JsTaskHasKindAndFunction()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch 바꾼다\n- [ ] T2 css web/css/admin.css#.search-box 넓힌다\n" +
             "- [ ] T3 js web/admin/list.asp#L7-9 바꾼다\n");
        Assert.Equal(0, await Run(Opts()));
        using var json = ApplyJson();
        var tasks = json.RootElement.GetProperty("tasks").EnumerateArray().ToDictionary(e => e.GetProperty("taskId").GetString()!);
        Assert.Equal("js", tasks["T1"].GetProperty("kind").GetString());
        Assert.Equal("fnSearch", tasks["T1"].GetProperty("function").GetString());
        Assert.False(tasks["T1"].TryGetProperty("proc", out _));
        Assert.Equal("css", tasks["T2"].GetProperty("kind").GetString());
        Assert.Equal(".search-box", tasks["T2"].GetProperty("selector").GetString());
        Assert.Equal("7-9", tasks["T3"].GetProperty("lines").GetString());
        Assert.False(tasks["T3"].TryGetProperty("function", out _));
    }

    [Fact]
    public async Task Apply_AspThenJsSamePage()
    {
        Spec("- [ ] T1 asp web/admin/list.asp#Search 바꾼다\n- [ ] T2 js web/admin/list.asp#goPage 바꾼다\n");
        var seen = new List<RunRequest>();
        Assert.Equal(0, await Run(Opts(), Recording(seen)));
        Assert.Equal(2, seen.Count);
        Assert.Equal(EditKind.Asp, seen[0].Kind);
        Assert.Equal(EditKind.Js, seen[1].Kind);
        Assert.Equal("goPage", seen[1].MethodName);
        Assert.Null(seen[1].Lines);
    }

    [Fact]
    public async Task Apply_PreImpact_HasNoJsWarnings()
    {
        Make("web/admin/other.asp", "<input onclick=\"fnSearch(1)\">\n");
        Spec("- [ ] T1 js web/js/order.js#fnSearch 바꾼다\n");
        Assert.Equal(0, await Run(Opts()));
        Assert.Empty(_ui.ConfirmMessages);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("영향 대상"));
    }

    private const string Renamed = "function fnFind(f) {\n  return f;\n}\n";

    private System.Text.Json.JsonElement[] JsCallers()
    {
        using var json = ApplyJson();
        return json.RootElement.TryGetProperty("jsCallersNotUpdated", out var list) ? list.EnumerateArray().Select(e => e.Clone()).ToArray() : [];
    }

    [Fact]
    public async Task AfterApply_Rename_OldNameCallLeft_Warns()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch 이름을 fnFind로 바꾼다\n");
        _exec.Rewrite["T1"] = Renamed;
        Assert.Equal(0, await Run(Opts()));
        Assert.Contains("fnSearch의 서명이 바뀌었습니다: fnSearch(f) → fnFind(f). 고치지 않은 호출: web/admin/list.asp:8", _ui.Warnings);
        var row = Assert.Single(JsCallers());
        Assert.Equal("fnSearch", row.GetProperty("function").GetString());
        Assert.Equal("web/admin/list.asp", row.GetProperty("path").GetString());
        Assert.Equal(8, row.GetProperty("line").GetInt32());
        Assert.Equal("fnSearch(f) → fnFind(f)", row.GetProperty("change").GetString());
    }

    [Fact]
    public async Task AfterApply_Rename_AllCallersFixed_NoWarning()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch 이름을 바꾼다\n- [ ] T2 js web/admin/list.asp#goPage 새 이름을 부른다\n");
        _exec.Rewrite["T1"] = Renamed;
        _exec.Rewrite["T2"] = ListPage.Replace("fnSearch(document.f, n)", "fnFind(document.f, n)");
        Assert.Equal(0, await Run(Opts()));
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("서명이 바뀌었습니다"));
        Assert.Empty(JsCallers());
    }

    [Fact]
    public async Task AfterApply_Rename_OtherDeclarationNote()
    {
        Make("web/js/old.js", "function fnSearch(x) {}\n");
        Spec("- [ ] T1 js web/js/order.js#fnSearch 이름을 바꾼다\n");
        _exec.Rewrite["T1"] = Renamed;
        Assert.Equal(0, await Run(Opts()));
        Assert.Contains("fnSearch의 서명이 바뀌었습니다: fnSearch(f) → fnFind(f). 고치지 않은 호출: web/admin/list.asp:8 (다른 선언이 있음: web/js/old.js:1)", _ui.Warnings);
    }

    [Fact]
    public async Task AfterApply_ParamAdded_MismatchedCallsWarned_MatchingExcluded_AppendNote()
    {
        Make("web/admin/b.asp", "<input onclick=\"fnSearch(this)\">\n");
        Spec("- [ ] T1 js web/js/order.js#fnSearch page를 더한다\n");
        _exec.Rewrite["T1"] = OrderJs.Replace("fnSearch(f)", "fnSearch(f, page)");
        Assert.Equal(0, await Run(Opts()));
        Assert.Contains("fnSearch의 서명이 바뀌었습니다: fnSearch(f) → fnSearch(f, page). 고치지 않은 호출: web/admin/b.asp:1 — 기존 호출은 새 인수가 undefined로 동작함", _ui.Warnings);
        Assert.DoesNotContain(JsCallers(), r => r.GetProperty("path").GetString() == "web/admin/list.asp");
    }

    [Fact]
    public async Task AfterApply_ParamAdded_CoveredButUnfixedCall_StillWarned()
    {
        Make("web/admin/b.asp", "<input onclick=\"fnSearch(this)\">\n");
        Spec("- [ ] T1 js web/js/order.js#fnSearch page를 더한다\n- [ ] T2 asp web/admin/b.asp#L1-1 호출을 고친다\n");
        _exec.Rewrite["T1"] = OrderJs.Replace("fnSearch(f)", "fnSearch(f, page)");
        Assert.Equal(0, await Run(Opts()));
        Assert.Contains(_ui.Warnings, w => w.StartsWith("fnSearch의 서명이 바뀌었습니다") && w.Contains("web/admin/b.asp:1"));
    }

    [Fact]
    public async Task AfterApply_Reordered_CheckNote()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch 매개변수 이름을 바꾼다\n");
        _exec.Rewrite["T1"] = OrderJs.Replace("fnSearch(f) {\n  return f;", "fnSearch(g) {\n  return g;");
        Assert.Equal(0, await Run(Opts()));
        Assert.Contains("fnSearch의 매개변수 순서·이름이 바뀌었습니다: fnSearch(f) → fnSearch(g). 부르는 곳 1곳을 확인하십시오: web/admin/list.asp:8", _ui.Warnings);
    }

    [Fact]
    public async Task AfterApply_AspTaskRenamedBlockFunction_Warns()
    {
        Make("web/admin/c.asp", "<a href=\"javascript:goPage(1)\">x</a>\n");
        Spec("- [ ] T1 asp web/admin/list.asp#L6-10 함수 이름을 바꾼다\n");
        _exec.Rewrite["T1"] = ListPage.Replace("function goPage(", "function goTo(");
        Assert.Equal(0, await Run(Opts()));
        Assert.Contains("goPage의 서명이 바뀌었습니다: goPage(n) → goTo(n). 고치지 않은 호출: web/admin/c.asp:1", _ui.Warnings);
    }

    [Fact]
    public async Task AfterApply_RestoredTask_NoWarning()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch 이름을 바꾼다 [G1]\n- [ ] T2 css web/css/admin.css#.search-box 넓힌다 [G1]\n");
        _exec.Rewrite["T1"] = Renamed;
        _exec.Exit["T2"] = 1;
        Assert.Equal(1, await Run(Opts()));
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("서명이 바뀌었습니다"));
        Assert.Equal(OrderJs, Read("web/js/order.js"));
    }

    [Fact]
    public async Task AfterApply_ResumeDoneTask_NotCompared()
    {
        Make("web/js/order.js", Renamed);
        Spec("- [x] T1 js web/js/order.js#fnSearch 이름을 바꾼다 <!-- run: R0 -->\n- [ ] T2 css web/css/admin.css#.search-box 넓힌다\n");
        Assert.Equal(0, await Run(new ApplyOptions(false, true, false, true, false, null, false)));
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("서명이 바뀌었습니다"));
    }

    [Fact]
    public async Task Mismatch_JsAppliedLaterFailed_Warns()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch 이름을 바꾼다\n- [ ] T2 asp web/admin/list.asp#Search 호출을 고친다\n");
        _exec.Rewrite["T1"] = Renamed;
        _exec.Exit["T2"] = 1;
        Assert.Equal(1, await Run(Opts(cont: true)));
        Assert.Contains("T1(js)은 적용됐고 T2는 실패했습니다. 함수와 부르는 곳이 어긋나 있을 수 있습니다. 함께 되돌리려면 [G1]로 묶으십시오", _ui.Warnings);
        using var json = ApplyJson();
        Assert.Contains(json.RootElement.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "JsCallerMismatch");
    }

    [Fact]
    public async Task Mismatch_AspTaskChangedSignature_LabeledAsp()
    {
        Spec("- [ ] T1 asp web/admin/list.asp#L6-10 함수 이름을 바꾼다\n- [ ] T2 js web/js/order.js#fnSearch 고친다\n");
        _exec.Rewrite["T1"] = ListPage.Replace("function goPage(", "function goTo(");
        _exec.Exit["T2"] = 1;
        Assert.Equal(1, await Run(Opts(cont: true)));
        Assert.Contains("T1(asp)은 적용됐고 T2는 실패했습니다. 함수와 부르는 곳이 어긋나 있을 수 있습니다. 함께 되돌리려면 [G1]로 묶으십시오", _ui.Warnings);
    }

    [Fact]
    public async Task Mismatch_JsBodyOnly_NoWarning()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch 본문을 고친다\n- [ ] T2 asp web/admin/list.asp#Search 고친다\n");
        _exec.Exit["T2"] = 1;
        Assert.Equal(1, await Run(Opts(cont: true)));
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("어긋나"));
    }

    /// <summary>Records each request and rewrites the file like <see cref="FakeExecutor"/> would, through the real executor.</summary>
    private FsmTaskExecutor RecordingRewrite(List<RunRequest> seen) => new(async (r, ct) =>
    {
        seen.Add(r);
        var original = _guard.Read(r.FilePath);
        var id = "T" + seen.Count;
        if (!_exec.Rewrite.TryGetValue(id, out var text)) text = original.Text + "// " + id + "\n";
        var written = await _guard.WriteAsync(original, text, CancellationToken.None);
        return new RunResult(0, "R-" + seen.Count, r.FilePath, original, written.Snapshot, null);
    });

    [Fact]
    public async Task Apply_LaterJsTask_GetsChangedSignature()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch page를 더한다\n- [ ] T2 js web/admin/list.asp#goPage page를 넘긴다\n");
        _exec.Rewrite["T1"] = OrderJs.Replace("fnSearch(f)", "fnSearch(f, page)");
        var seen = new List<RunRequest>();
        Assert.Equal(0, await Run(Opts(), RecordingRewrite(seen)));
        Assert.Null(seen[0].ChangedJsFunctions);
        var c = Assert.Single(seen[1].ChangedJsFunctions!);
        Assert.Equal(("T1", "fnSearch(f)", "fnSearch(f, page)"), (c.TaskId, c.Before.Display, c.After.Display));
    }

    [Fact]
    public async Task Apply_LaterAspTask_GetsChangedSignature()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch page를 더한다\n- [ ] T2 asp web/admin/list.asp#Search 고친다\n");
        _exec.Rewrite["T1"] = OrderJs.Replace("fnSearch(f)", "fnSearch(f, page)");
        var seen = new List<RunRequest>();
        Assert.Equal(0, await Run(Opts(), RecordingRewrite(seen)));
        Assert.Single(seen[1].ChangedJsFunctions!);
    }

    [Fact]
    public async Task Apply_RenamedTarget_Followed_NoteRecorded()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch 이름을 바꾼다\n- [ ] T2 js web/js/order.js#fnSearch 본문을 고친다\n");
        _exec.Rewrite["T1"] = Renamed;
        var seen = new List<RunRequest>();
        Assert.Equal(0, await Run(Opts(), RecordingRewrite(seen)));
        Assert.Equal("fnFind", seen[1].MethodName);
        Assert.Contains("T2의 대상 fnSearch는 T1이 바꾼 fnFind로 찾음", _ui.Warnings);
        using var json = ApplyJson();
        var t2 = json.RootElement.GetProperty("tasks").EnumerateArray().Single(e => e.GetProperty("taskId").GetString() == "T2");
        Assert.Equal("T2의 대상 fnSearch는 T1이 바꾼 fnFind로 찾음", t2.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Apply_BareNameResolvedToQualified_RenameFollowed()
    {
        const string common = "var Common = {\n  fnSearch: function (f) {\n    return f;\n  }\n};\n";
        Make("web/js/common.js", common);
        Spec("- [ ] T1 js web/js/common.js#fnSearch 이름을 바꾼다\n- [ ] T2 js web/js/common.js#fnSearch 본문을 고친다\n");
        _exec.Rewrite["T1"] = common.Replace("fnSearch:", "fnFind:");
        var seen = new List<RunRequest>();
        Assert.Equal(0, await Run(Opts(), RecordingRewrite(seen)));
        Assert.EndsWith("fnFind", seen[1].MethodName);
    }

    [Fact]
    public async Task Apply_RestoredJsTask_NotPassed()
    {
        Spec("- [ ] T1 js web/js/order.js#fnSearch page를 더한다 [G1]\n- [ ] T2 css web/css/admin.css#.search-box 넓힌다 [G1]\n" +
             "- [ ] T3 js web/admin/list.asp#goPage 고친다\n");
        _exec.Rewrite["T1"] = OrderJs.Replace("fnSearch(f)", "fnSearch(f, page)");
        _exec.Exit["T2"] = 1;
        Assert.Equal(1, await Run(Opts(cont: true)));
        var t3 = _exec.Options[^1];
        Assert.Equal("T3", _exec.Calls[^1]);
        Assert.Null(t3.ChangedJsFunctions);
    }

    [Fact]
    public async Task AfterApply_FollowedRenameThenParamAdded_Warned()
    {
        Make("web/admin/b.asp", "<input onclick=\"fnFind(this)\">\n");
        Spec("- [ ] T1 js web/js/order.js#fnSearch 이름을 fnFind로 바꾼다\n- [ ] T2 js web/js/order.js#fnSearch page를 더한다\n");
        _exec.Rewrite["T1"] = Renamed;
        _exec.Rewrite["T2"] = Renamed.Replace("fnFind(f)", "fnFind(f, page)");
        Assert.Equal(0, await Run(Opts()));
        Assert.Contains("fnFind의 서명이 바뀌었습니다: fnFind(f) → fnFind(f, page). 고치지 않은 호출: web/admin/b.asp:1 — 기존 호출은 새 인수가 undefined로 동작함", _ui.Warnings);
    }

    [Fact]
    public async Task Follow_OnlyFromFunctionTargets_NotFromLineRanges()
    {
        // An asp #L task renames goPage by line pairing; a later js #goPage task must not be redirected by it.
        Spec("- [ ] T1 asp web/admin/list.asp#L6-10 함수 이름을 바꾼다\n- [ ] T2 js web/admin/list.asp#goPage 고친다\n");
        _exec.Rewrite["T1"] = ListPage.Replace("function goPage(", "function goTo(");
        var seen = new List<RunRequest>();
        await Run(Opts(cont: true), RecordingRewrite(seen));
        Assert.Equal("goPage", seen[1].MethodName);
    }

    // ---- form fields after the apply (W3 design §5.3) ----

    private const string MemoPage =
        "<form name=\"frm\" action=\"memo_save.asp\" method=\"post\">\n" + // 1
        "<input name=\"memoText\">\n" +                                    // 2
        "</form>\n";                                                        // 3

    private const string MemoSave =
        "<%\n" +                                // 1
        "v = Request.Form(\"memoText\")\n" +    // 2
        "%>\n";                                 // 3

    private const string MemoNotSent =
        "web/admin/memo_save.asp가 읽는 memoText(줄 2)를 web/admin/memo.asp의 폼(줄 1)이 보내지 않음, 보내는 비슷한 이름: memo";

    private void MemoSite()
    {
        Make("web/admin/memo.asp", MemoPage);
        Make("web/admin/memo_save.asp", MemoSave);
    }

    private System.Text.Json.JsonElement[] FormMismatches()
    {
        using var json = ApplyJson();
        return json.RootElement.TryGetProperty("formFieldMismatches", out var list) ? list.EnumerateArray().Select(e => e.Clone()).ToArray() : [];
    }

    [Fact]
    public async Task AfterApply_FormRenamed_ReaderNotUpdated_Warns()
    {
        MemoSite();
        Spec("- [ ] T1 asp web/admin/memo.asp#L2-2 이름을 memo로 바꾼다\n");
        _exec.Rewrite["T1"] = MemoPage.Replace("\"memoText\"", "\"memo\"");
        Assert.Equal(0, await Run(Opts()));
        Assert.Contains(MemoNotSent, _ui.Warnings);
        Assert.Single(FormMismatches());
    }

    [Fact]
    public async Task ApplyJson_FormFieldMismatches()
    {
        MemoSite();
        Spec("- [ ] T1 asp web/admin/memo.asp#L2-2 이름을 memo로 바꾼다\n");
        _exec.Rewrite["T1"] = MemoPage.Replace("\"memoText\"", "\"memo\"");
        Assert.Equal(0, await Run(Opts()));
        var row = Assert.Single(FormMismatches());
        Assert.Equal("notSent", row.GetProperty("kind").GetString());
        Assert.Equal("web/admin/memo_save.asp", row.GetProperty("target").GetString());
        Assert.Equal("memoText", row.GetProperty("key").GetString());
        Assert.Equal("web/admin/memo.asp", row.GetProperty("sender").GetString());
        Assert.Equal(MemoNotSent, row.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AfterApply_FormAndReaderBothUpdated_NoWarning_ResolvedNote()
    {
        MemoSite();
        Spec("- [ ] T1 asp web/admin/memo_save.asp#L2-2 memo를 읽는다\n- [ ] T2 asp web/admin/memo.asp#L2-2 이름을 memo로 바꾼다\n");
        _exec.Rewrite["T1"] = MemoSave.Replace("\"memoText\"", "\"memo\"");
        _exec.FormWarnings["T1"] = 1;
        _exec.Rewrite["T2"] = MemoPage.Replace("\"memoText\"", "\"memo\"");
        Assert.Equal(0, await Run(Opts()));
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("보내지 않음") || w.Contains("보내는 폼·ajax가 없음"));
        Assert.Contains("작업 중 폼 필드 경고 1건 중 1건은 뒤 작업이 해결함", _ui.Warnings);
        Assert.Empty(FormMismatches());
    }

    [Fact]
    public async Task AfterApply_RestoredTask_NotCounted()
    {
        MemoSite();
        Spec("- [ ] T1 asp web/admin/memo.asp#L2-2 이름을 바꾼다 [G1]\n- [ ] T2 css web/css/admin.css#.search-box 넓힌다 [G1]\n");
        _exec.Rewrite["T1"] = MemoPage.Replace("\"memoText\"", "\"memo\"");
        _exec.FormWarnings["T1"] = 1;
        _exec.Exit["T2"] = 1;
        Assert.Equal(1, await Run(Opts()));
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("보내지 않음") || w.Contains("폼 필드 경고"));
        Assert.Empty(FormMismatches());
    }

    private sealed class FakeGroupVerifier : IGroupVerifier
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct)
        {
            Calls.Add(projects);
            return Task.FromResult(new GroupBuild(true, null, null, []));
        }
    }

    /// <summary>
    /// Edits the task's file through FileGuard (a comment line with the task ID appended, or <see cref="Rewrite"/>) and reports a
    /// scripted exit code. Run ids are <c>R-&lt;id&gt;</c>.
    /// </summary>
    internal sealed class FakeExecutor(FileGuard guard) : ITaskExecutor
    {
        public List<string> Calls { get; } = [];
        public List<TaskRunOptions> Options { get; } = [];
        public Dictionary<string, int> Exit { get; } = [];

        /// <summary>Task ID → the whole text its file gets instead of the appended comment.</summary>
        public Dictionary<string, string> Rewrite { get; } = [];

        /// <summary>Task ID → the form field warnings its run reports.</summary>
        public Dictionary<string, int> FormWarnings { get; } = [];

        public async Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct)
        {
            Calls.Add(task.Id);
            Options.Add(o);
            var n = Calls.Count(c => c == task.Id);
            var runId = n == 1 ? "R-" + task.Id : $"R-{task.Id}-{n}";
            var path = Path.Combine(ws.Root, task.Path.Replace('/', Path.DirectorySeparatorChar));
            var original = guard.Read(path);
            var exit = Exit.GetValueOrDefault(task.Id);
            if (exit != 0 || o.DryRun)
                return new RunResult(exit, runId, path, original, original, exit == 0 ? null : "Verify");
            var comment = task.Kind switch { TaskKind.Sql => "-- ", TaskKind.Asp => "' ", TaskKind.Css => "/* ", _ => "// " };
            var newText = Rewrite.TryGetValue(task.Id, out var whole) ? whole : original.Text + comment + task.Id + "\n";
            var written = await guard.WriteAsync(original, newText, CancellationToken.None);
            return new RunResult(0, runId, path, original, written.Snapshot, null, FormFieldWarnings: FormWarnings.GetValueOrDefault(task.Id));
        }
    }
}
