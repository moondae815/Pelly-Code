using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using System.Text.Json.Serialization;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Reporting;

public sealed class ApplyHistoryTests
{
    private static readonly Workspace Ws = new("/w", "/w/.pelly");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static void Write(MockFileSystem fs, ApplySummary s) =>
        fs.AddFile($"/w/.pelly/runs/{s.ApplyId}/apply.json", new MockFileData(JsonSerializer.Serialize(s, Json)));

    private static ApplySummary Summary(string id) =>
        new(id, "spec", [new ApplyTaskResult("T1", "R1", 0, null, 1, "G1")], 0, [], true, TaskCount: 4, Resumed: true);

    [Fact]
    public void Reads_apply_json_and_skips_summary_dirs()
    {
        var fs = new MockFileSystem();
        Write(fs, Summary("apply-20261001-101010-aaaa"));
        fs.AddFile("/w/.pelly/runs/20261001-111111-bbbb/summary.json", new MockFileData("{}"));

        var (applies, unreadable) = ApplyHistory.Load(fs, Ws, null);

        var a = Assert.Single(applies);
        Assert.Equal(0, unreadable);
        Assert.Equal(4, a.Summary.TaskCount);
        Assert.True(a.Summary.Resumed);
        Assert.Equal("G1", a.Summary.Tasks[0].Group);
    }

    [Fact]
    public void Applies_are_filtered_by_since()
    {
        var fs = new MockFileSystem();
        Write(fs, Summary("apply-20261001-101010-aaaa"));
        Write(fs, Summary("apply-20261005-101010-bbbb"));

        var (applies, _) = ApplyHistory.Load(fs, Ws, new DateOnly(2026, 10, 3));

        Assert.Equal("apply-20261005-101010-bbbb", Assert.Single(applies).Summary.ApplyId);
    }

    [Fact]
    public void Old_apply_json_without_new_fields_reads_with_defaults()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/w/.pelly/runs/apply-20261001-101010-aaaa/apply.json", new MockFileData(
            "{\"applyId\":\"apply-20261001-101010-aaaa\",\"specId\":\"s\",\"tasks\":[{\"taskId\":\"T1\",\"runId\":\"R\",\"exitCode\":0,\"failureKind\":null,\"seconds\":1}],\"exitCode\":0,\"warnings\":[],\"archived\":true}"));

        var (applies, unreadable) = ApplyHistory.Load(fs, Ws, null);

        var a = Assert.Single(applies);
        Assert.Equal(0, unreadable);
        Assert.Equal(0, a.Summary.TaskCount);
        Assert.False(a.Summary.Resumed);
        Assert.Null(a.Summary.Tasks[0].Group);
    }

    [Fact]
    public void Unreadable_is_counted()
    {
        var fs = new MockFileSystem();
        Write(fs, Summary("apply-20261001-101010-aaaa"));
        fs.AddFile("/w/.pelly/runs/apply-20261002-101010-bbbb/apply.json", new MockFileData("{ not json"));
        fs.AddFile("/w/.pelly/runs/apply-20261003-101010-cccc/apply.json", new MockFileData("{\"applyId\":\"x\"}"));
        fs.AddFile("/w/.pelly/runs/apply-bad/apply.json", new MockFileData(JsonSerializer.Serialize(Summary("apply-bad"), Json)));

        var (applies, unreadable) = ApplyHistory.Load(fs, Ws, null);

        Assert.Single(applies);
        Assert.Equal(3, unreadable);
    }

    [Fact]
    public void No_runs_dir_is_empty()
    {
        var (applies, unreadable) = ApplyHistory.Load(new MockFileSystem(), Ws, null);

        Assert.Empty(applies);
        Assert.Equal(0, unreadable);
    }
}
