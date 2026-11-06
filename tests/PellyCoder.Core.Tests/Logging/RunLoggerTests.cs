using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Workspaces;
using static PellyCoder.Core.Tests.TestPaths;

namespace PellyCoder.Core.Tests.Logging;

public sealed class RunLoggerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 30, 15, TimeSpan.Zero);

    private readonly MockFileSystem _fs = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly string _runs = P(@"C:\ws\.pelly\runs");

    private RunContext NewContext(string runId = "20261017-093015-abcd")
    {
        var options = new PellyOptions();
        options.Log.RetentionDays = 30;
        var request = new RunRequest(P(@"C:\ws\Calc.cs"), null, "Divide", null, "req", true, false, null);
        return new RunContext(runId, request, options) { Workspace = new Workspace(P(@"C:\ws"), P(@"C:\ws\.pelly")) };
    }

    private static GenerationResult Gen(string output, string? think)
    {
        var usage = new LlmUsage(100, 40, 10, "stop", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3));
        return new GenerationResult(output, think is not null, think, think is null ? 0 : 12, false, think is null ? null : usage, usage);
    }

    [Fact]
    public void RunId_Format()
    {
        var id = RunLogger.NewRunId(_time);

        Assert.Matches("^20261017-093015-[0-9a-f]{4}$", id);
    }

    [Fact]
    public void AttemptFiles_Written()
    {
        var ctx = NewContext();
        var logger = new RunLogger(_fs, _time);
        logger.Start(ctx);
        var dir = _fs.Path.Combine(_runs, ctx.RunId);

        ctx.Attempt = 1;
        logger.Attempt(ctx, "PROMPT-1", Gen("OUT-1", null), null);
        ctx.Attempt = 2;
        logger.Attempt(ctx, "PROMPT-2", Gen("OUT-2", "THINK-2"), new Feedback("FEEDBACK-2", 3));

        Assert.Equal("PROMPT-1", _fs.File.ReadAllText(_fs.Path.Combine(dir, "attempt-1", "prompt.txt")));
        Assert.Equal("OUT-1", _fs.File.ReadAllText(_fs.Path.Combine(dir, "attempt-1", "output.txt")));
        Assert.False(_fs.File.Exists(_fs.Path.Combine(dir, "attempt-1", "think.txt")));
        Assert.False(_fs.File.Exists(_fs.Path.Combine(dir, "attempt-1", "feedback.txt")));
        Assert.Equal("PROMPT-2", _fs.File.ReadAllText(_fs.Path.Combine(dir, "attempt-2", "prompt.txt")));
        Assert.Equal("THINK-2", _fs.File.ReadAllText(_fs.Path.Combine(dir, "attempt-2", "think.txt")));
        Assert.Equal("OUT-2", _fs.File.ReadAllText(_fs.Path.Combine(dir, "attempt-2", "output.txt")));
        Assert.Equal("FEEDBACK-2", _fs.File.ReadAllText(_fs.Path.Combine(dir, "attempt-2", "feedback.txt")));
    }

    [Fact]
    public void PhaseAndComplete_WriteEventsAndSummary()
    {
        var ctx = NewContext();
        var logger = new RunLogger(_fs, _time);
        logger.Start(ctx);
        ctx.Attempt = 1;
        ctx.ExitCode = 0;
        ctx.Generation = Gen("OUT", "THINK");
        ctx.MatchStages.Add(MatchStage.Exact);
        ctx.ErrorCodes.Add("CS1002");
        ctx.PhaseSeconds[RunPhase.Edit] = 2.5;

        logger.Phase(ctx, PhaseOutcome.Ok, TimeSpan.FromMilliseconds(1234));
        logger.Complete(ctx);

        var dir = _fs.Path.Combine(_runs, ctx.RunId);
        var line = Assert.Single(_fs.File.ReadAllLines(_fs.Path.Combine(dir, "events.jsonl")));
        var ev = JsonDocument.Parse(line).RootElement;
        Assert.Equal("Preflight", ev.GetProperty("phase").GetString());
        Assert.Equal("Ok", ev.GetProperty("outcome").GetString());
        Assert.Equal(1, ev.GetProperty("attempt").GetInt32());
        Assert.Equal(1234, ev.GetProperty("elapsedMs").GetInt64());
        Assert.Equal(Now, ev.GetProperty("ts").GetDateTimeOffset());

        var summary = JsonDocument.Parse(_fs.File.ReadAllText(_fs.Path.Combine(dir, "summary.json"))).RootElement;
        Assert.Equal(ctx.RunId, summary.GetProperty("runId").GetString());
        Assert.Equal("qwen3.6:35b-a3b", summary.GetProperty("modelName").GetString());
        Assert.Equal("CSharp", summary.GetProperty("kind").GetString());
        Assert.Equal("Retry", summary.GetProperty("thinkMode").GetString());
        Assert.Equal(12, summary.GetProperty("thinkTokens").GetInt32());
        Assert.Equal(1.5, summary.GetProperty("ttftSeconds").GetDouble());
        Assert.Equal(["Exact"], summary.GetProperty("matchStages").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["CS1002"], summary.GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(2.5, summary.GetProperty("phaseSeconds").GetProperty("Edit").GetDouble());
        Assert.StartsWith("cs-v", summary.GetProperty("promptVersion").GetString());
    }

    [Fact]
    public void Summary_PromptVersion_FollowsKind()
    {
        var sql = NewContext();
        sql.Target = new EditTarget(EditKind.Sql, P(@"C:\ws\a.sql"), "dbo.p", new TextSpanInfo(1, 2), null);
        var cs = NewContext();
        cs.Target = new EditTarget(EditKind.CSharp, P(@"C:\ws\Calc.cs"), "Calc.Divide", new TextSpanInfo(1, 2), null);

        Assert.Equal("sql-v1", RunLogger.Summarize(sql).PromptVersion);
        Assert.Equal("cs-v2", RunLogger.Summarize(cs).PromptVersion);
        Assert.Equal("cs-v2", RunLogger.Summarize(NewContext()).PromptVersion);
    }

    [Fact]
    public void Summary_SqlRequestWithoutTarget_RecordsSql()
    {
        var request = new RunRequest(P(@"C:\ws\a.sql"), null, null, null, "req", true, false, null, null, EditKind.Sql);
        var ctx = new RunContext("r1", request, new PellyOptions());

        var summary = RunLogger.Summarize(ctx);

        Assert.Equal(EditKind.Sql, summary.Kind);
        Assert.Equal("sql-v1", summary.PromptVersion);
    }

    [Fact]
    public void Summary_CountsFormatErrors()
    {
        var ctx = NewContext();
        ctx.FormatErrors = 2;

        Assert.Equal(2, RunLogger.Summarize(ctx).FormatErrors);
        Assert.Equal(0, RunLogger.Summarize(NewContext()).FormatErrors);
    }

    [Fact]
    public void Summary_CharTestBlock()
    {
        var ctx = NewContext();
        Assert.Null(RunLogger.Summarize(ctx).CharTest);

        ctx.CharTest = new PellyCoder.Core.CharTests.CharTestState(3)
        {
            Framework = PellyCoder.Core.CharTests.TestFrameworks.Info(PellyCoder.Core.CharTests.TestFramework.XunitV3),
            NewTestNames = ["CalcTests.A", "CalcTests.B"],
            Generated = 2, PassedOnCurrent = 2, StableOnRerun = true, KilledByMutation = 1,
            RejectedBy = "7:CharTestSurvivesMutation",
        };
        ctx.Target = new EditTarget(EditKind.CSharpTest, P(@"C:\ws\Calc.cs"), "Calc.Divide", new TextSpanInfo(1, 2), null);
        var logger = new RunLogger(_fs, _time);
        logger.Start(ctx);
        logger.Complete(ctx);

        Assert.Equal(new PellyCoder.Core.CharTests.CharTestSummary(3, 2, 0, 2, true, 1, "7:CharTestSurvivesMutation", "XunitV3", false),
            RunLogger.Summarize(ctx).CharTest);
        var summary = JsonDocument.Parse(_fs.File.ReadAllText(_fs.Path.Combine(_runs, ctx.RunId, "summary.json"))).RootElement;
        Assert.Equal("CSharpTest", summary.GetProperty("kind").GetString());
        Assert.Equal("cs-test-v1", summary.GetProperty("promptVersion").GetString());
        var block = summary.GetProperty("charTest");
        Assert.Equal(0, block.GetProperty("kept").GetInt32());
        Assert.Equal("7:CharTestSurvivesMutation", block.GetProperty("rejectedBy").GetString());

        ctx.CharTest.Kept = true;
        Assert.Equal(2, RunLogger.Summarize(ctx).CharTest!.Kept);
    }

    [Fact]
    public void Summary_CharTestRunWithoutTarget_RecordsCSharpTest()
    {
        var ctx = NewContext();
        ctx.CharTest = new PellyCoder.Core.CharTests.CharTestState(3);

        var summary = RunLogger.Summarize(ctx);

        Assert.Equal(EditKind.CSharpTest, summary.Kind);
        Assert.Equal("cs-test-v1", summary.PromptVersion);
    }

    [Fact]
    public void Summary_AspBlock()
    {
        var ctx = NewContext();
        Assert.Null(RunLogger.Summarize(ctx).Asp);

        const string text = "<%\ncmd.CommandText = \"dbo.usp_A\"\ncmd.CommandType = 4\nconn.Execute(\"exec usp_B 1\")\n%>\n";
        var codec = new PellyCoder.Core.Asp.AspTextCodec("cp949");
        ctx.Original = codec.Decode(P(@"C:\ws\a.asp"), System.Text.Encoding.ASCII.GetBytes(text));
        ctx.Current = ctx.Original;
        var page = PellyCoder.Core.Asp.AspPage.Parse(text);
        var includes = new PellyCoder.Core.Asp.AspIncludeSet([new(P(@"C:\ws\u.inc"), page)], ["missing.inc"], false);
        var state = new PellyCoder.Core.Asp.AspRunState(page, PellyCoder.Core.Asp.AspTargetUnit.Lines, new TextSpanInfo(2, 3), null,
            includes, "CP949", P(@"C:\ws"));
        state.Record("Structure", PellyCoder.Core.Asp.AspCheckStatus.Passed);
        state.Record("AspNames", PellyCoder.Core.Asp.AspCheckStatus.Warned, "x");
        state.Record("AspCompile", PellyCoder.Core.Asp.AspCheckStatus.Skipped, "Windows가 아님");
        state.Record("AspSp", PellyCoder.Core.Asp.AspCheckStatus.Failed, "usp_B");
        ctx.Asp = state;

        var asp = RunLogger.Summarize(ctx).Asp!;

        Assert.Equal("Lines", asp.Unit);
        Assert.Equal("CP949", asp.Encoding);
        Assert.Equal(new Dictionary<string, string>
        {
            ["Structure"] = "passed", ["AspNames"] = "warned", ["AspCompile"] = "skipped", ["AspSp"] = "failed",
        }, asp.Checks);
        Assert.Equal(["dbo.usp_A", "usp_B"], asp.StoredProcedures);
        Assert.Equal(1, asp.IncludesFound);
        Assert.Equal(1, asp.IncludesMissing);
        Assert.False(asp.Executed);
    }

    [Fact]
    public void Summary_WebBlock()
    {
        var ctx = NewContext();
        Assert.Null(RunLogger.Summarize(ctx).Web);

        var index = new Lazy<PellyCoder.Core.Web.WebSiteIndexResult>(() => throw new InvalidOperationException("not used"));
        var state = new PellyCoder.Core.Web.WebRunState(EditKind.Js, PellyCoder.Core.Web.WebTargetUnit.Function, new TextSpanInfo(1, 3),
            "function a() {}\n", "CP949", true, P(@"C:\ws"), index)
        {
            OriginalLevel = Acornima.EcmaVersion.ES5,
            TargetLevel = Acornima.EcmaVersion.ES2015,
            Jquery = new Version(1, 8, 3),
        };
        state.Record("Syntax", PellyCoder.Core.Asp.AspCheckStatus.Passed);
        state.Record("Names", PellyCoder.Core.Asp.AspCheckStatus.Warned, "fnX(줄 2)");
        state.Record("Api", PellyCoder.Core.Asp.AspCheckStatus.Skipped, "x");
        state.Record("Level", PellyCoder.Core.Asp.AspCheckStatus.Failed, "y");
        state.LevelRejections = 2;
        state.NameWarnings = 1;
        state.ApiWarnings = 3;
        state.PropertyWarnings = 4;
        ctx.Web = state;

        var web = RunLogger.Summarize(ctx).Web!;

        Assert.Equal("function", web.Unit);
        Assert.Equal("CP949", web.Encoding);
        Assert.Equal("ES5", web.OriginalLevel);
        Assert.Equal("ES2015", web.TargetLevel);
        Assert.Equal("1.8.3", web.Jquery);
        Assert.Equal(new Dictionary<string, string>
        {
            ["Api"] = "skipped", ["Level"] = "failed", ["Names"] = "warned", ["Syntax"] = "passed",
        }, web.Checks);
        Assert.Equal(2, web.LevelRejections);
        Assert.Equal(1, web.NameWarnings);
        Assert.Equal(3, web.ApiWarnings);
        Assert.Equal(4, web.PropertyWarnings);

        var css = new PellyCoder.Core.Web.WebRunState(EditKind.Css, PellyCoder.Core.Web.WebTargetUnit.Lines, new TextSpanInfo(1, 1),
            "a {}\n", "UTF-8", false, P(@"C:\ws"), index);
        ctx.Web = css;
        var cssWeb = RunLogger.Summarize(ctx).Web!;
        Assert.Equal("lines", cssWeb.Unit);
        Assert.Null(cssWeb.OriginalLevel);
        Assert.Null(cssWeb.TargetLevel);
        Assert.Null(cssWeb.Jquery);
    }

    private static PellyCoder.Core.Web.WebRunState PageState(string pageText, string source, EditKind kind = EditKind.Js)
    {
        var index = new Lazy<PellyCoder.Core.Web.WebSiteIndexResult>(() => throw new InvalidOperationException("not used"));
        var page = PellyCoder.Core.Asp.AspPage.Parse(pageText);
        var view = PellyCoder.Core.Web.PageBlockView.Build(page, pageText, 0);
        return new PellyCoder.Core.Web.WebRunState(kind, PellyCoder.Core.Web.WebTargetUnit.Lines, new TextSpanInfo(2, 2),
            "x\n", "UTF-8", true, P(@"C:\ws"), index)
        {
            Page = new PellyCoder.Core.Web.WebPageState(page, 0, view, [view]),
            JquerySource = source,
        };
    }

    [Fact]
    public void Summary_web_block_has_page_fields()
    {
        var ctx = NewContext();
        ctx.Web = PageState("<html>\n<script>\nvar n = <%= cnt %>;\n</script>\n", "page");

        var web = RunLogger.Summarize(ctx).Web!;

        Assert.Equal("page", web.Container);
        Assert.Equal(new WebBlockSummary("script", 2, 4, "output"), web.Block);
        Assert.Equal("page", web.JquerySource);
        var json = JsonSerializer.Serialize(RunLogger.Summarize(ctx), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"container\":\"page\"", json);
        Assert.Contains("\"block\":{\"kind\":\"script\",\"startLine\":2,\"endLine\":4,\"serverCode\":\"output\"}", json);
        Assert.Contains("\"jquerySource\":\"page\"", json);
    }

    [Fact]
    public void Summary_web_block_file_container()
    {
        var ctx = NewContext();
        var index = new Lazy<PellyCoder.Core.Web.WebSiteIndexResult>(() => throw new InvalidOperationException("not used"));
        ctx.Web = new PellyCoder.Core.Web.WebRunState(EditKind.Js, PellyCoder.Core.Web.WebTargetUnit.Function, new TextSpanInfo(1, 1),
            "function a() {}\n", "UTF-8", true, P(@"C:\ws"), index);

        var web = RunLogger.Summarize(ctx).Web!;

        Assert.Equal("file", web.Container);
        Assert.Null(web.Block);
        Assert.Equal("site", web.JquerySource);
    }

    [Fact]
    public void Asp_summary_has_client_rows()
    {
        var ctx = NewContext();
        var text = "<html>\n<script>\nvar a;\n</script>\n";
        var page = PellyCoder.Core.Asp.AspPage.Parse(text);
        var state = new PellyCoder.Core.Asp.AspRunState(page, PellyCoder.Core.Asp.AspTargetUnit.Lines, new TextSpanInfo(2, 3), null,
            new PellyCoder.Core.Asp.AspIncludeSet([], [], false), "UTF-8", P(@"C:\ws"));
        state.Record("JsSyntax", PellyCoder.Core.Asp.AspCheckStatus.Passed);
        state.Record("JsLevel", PellyCoder.Core.Asp.AspCheckStatus.Failed, "x");
        state.Record("JsNames", PellyCoder.Core.Asp.AspCheckStatus.Warned, "y");
        state.Record("CssSafety", PellyCoder.Core.Asp.AspCheckStatus.Passed);
        ctx.Asp = state;

        var checks = RunLogger.Summarize(ctx).Asp!.Checks;

        Assert.Equal("passed", checks["JsSyntax"]);
        Assert.Equal("failed", checks["JsLevel"]);
        Assert.Equal("warned", checks["JsNames"]);
        Assert.Equal("passed", checks["CssSafety"]);
    }
}
