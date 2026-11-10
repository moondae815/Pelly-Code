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
            return new RunResult(0, runId, path, original, written.Snapshot, null);
        }
    }
}
