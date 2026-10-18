using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using System.Text.Json.Serialization;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Reporting;

public sealed class RunHistoryTests
{
    private static readonly Workspace Ws = new("/w", "/w/.pelly");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    internal static RunSummary Summary(string runId, int exit = 0, int attempts = 1, int formatErrors = 0, double ttft = 0.5,
        double queue = 0, string? failure = null, int think = 0, bool truncated = false, EditKind kind = EditKind.CSharp,
        double editSeconds = 2) => new(
        runId, "1.0", "0.1", "m", "cs-v1", kind, "Sdk", exit, attempts,
        new Dictionary<RunPhase, double> { [RunPhase.Edit] = editSeconds, [RunPhase.Verify] = 1 }, ttft, queue,
        "Retry", think, truncated, 100, null, null, [], [], formatErrors, failure);

    internal static void Write(MockFileSystem fs, RunSummary s) =>
        fs.AddFile($"/w/.pelly/runs/{s.RunId}/summary.json", new MockFileData(JsonSerializer.Serialize(s, Json)));

    [Fact]
    public void Load_ReadsSummaries_CountsUnreadable()
    {
        var fs = new MockFileSystem();
        Write(fs, Summary("20261001-101010-aaaa"));
        Write(fs, Summary("20261002-101010-bbbb", exit: 1, failure: "BuildErrors"));
        fs.AddFile("/w/.pelly/runs/20261003-101010-cccc/summary.json", new MockFileData("{ not json"));
        fs.AddFile("/w/.pelly/runs/20261004-101010-dddd/summary.json", new MockFileData("{\"runId\":\"x\"}"));
        fs.AddDirectory("/w/.pelly/runs/20261005-101010-eeee"); // no summary yet: ignored, not unreadable

        var result = RunHistory.Load(fs, Ws, null);

        Assert.Equal(2, result.Runs.Count);
        Assert.Equal(2, result.Unreadable);
        Assert.Equal(RunPhase.Edit, result.Runs[0].Summary.PhaseSeconds.Keys.Min());
        Assert.Equal("BuildErrors", result.Runs[1].Summary.FailureKind);
    }

    [Fact]
    public void Load_NoRunsFolder_IsEmpty()
    {
        var result = RunHistory.Load(new MockFileSystem(), Ws, null);
        Assert.Empty(result.Runs);
        Assert.Equal(0, result.Unreadable);
    }

    [Fact]
    public void Load_SinceFiltersByRunIdDate()
    {
        var fs = new MockFileSystem();
        Write(fs, Summary("20261001-235959-aaaa"));
        Write(fs, Summary("20261002-000001-bbbb"));
        Write(fs, Summary("20261015-120000-cccc"));

        var result = RunHistory.Load(fs, Ws, new DateOnly(2026, 10, 2));

        Assert.Equal(["20261002-000001-bbbb", "20261015-120000-cccc"], result.Runs.Select(r => r.Summary.RunId));
    }
}
