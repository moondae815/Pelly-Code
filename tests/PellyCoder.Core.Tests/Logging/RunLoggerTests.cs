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
}
