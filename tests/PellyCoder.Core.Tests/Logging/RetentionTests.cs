using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Workspaces;
using static PellyCoder.Core.Tests.TestPaths;

namespace PellyCoder.Core.Tests.Logging;

public sealed class RetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 30, 15, TimeSpan.Zero);

    private readonly MockFileSystem _fs = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly Workspace _ws = new(P(@"C:\ws"), P(@"C:\ws\.pelly"));
    private readonly string _runs = P(@"C:\ws\.pelly\runs");

    private RunContext NewContext(string runId)
    {
        var request = new RunRequest(P(@"C:\ws\Calc.cs"), null, "Divide", null, "req", true, false, null);
        return new RunContext(runId, request, new PellyOptions()) { Workspace = _ws };
    }

    [Fact]
    public void Prune_DeletesOldRuns_KeepsRecentAndForeignFolders()
    {
        _fs.AddDirectory(_fs.Path.Combine(_runs, "20260901-000000-aaaa")); // 46 days old
        _fs.AddFile(_fs.Path.Combine(_runs, "20260901-000000-aaaa", "summary.json"), new MockFileData("{}"));
        _fs.AddDirectory(_fs.Path.Combine(_runs, "20261001-000000-bbbb")); // 16 days old
        _fs.AddDirectory(_fs.Path.Combine(_runs, "notes"));                // not a run id

        new RunLogger(_fs, _time).Prune(_ws, 30, "20261017-093015-abcd");

        Assert.False(_fs.Directory.Exists(_fs.Path.Combine(_runs, "20260901-000000-aaaa")));
        Assert.True(_fs.Directory.Exists(_fs.Path.Combine(_runs, "20261001-000000-bbbb")));
        Assert.True(_fs.Directory.Exists(_fs.Path.Combine(_runs, "notes")));
    }

    [Fact]
    public void Prune_NeverDeletesCurrentRun()
    {
        var current = "20200101-000000-cccc"; // older than the cutoff, yet it is this run
        _fs.AddDirectory(_fs.Path.Combine(_runs, current));

        new RunLogger(_fs, _time).Prune(_ws, 1, current);

        Assert.True(_fs.Directory.Exists(_fs.Path.Combine(_runs, current)));
    }

    [Fact]
    public void Start_DoesNotPrune()
    {
        _fs.AddDirectory(_fs.Path.Combine(_runs, "20200101-000000-aaaa"));

        new RunLogger(_fs, _time).Start(NewContext("20261017-093015-abcd"));

        Assert.True(_fs.Directory.Exists(_fs.Path.Combine(_runs, "20200101-000000-aaaa")));
    }

    [Fact]
    public void LoggerFailureFlag_ResetsPerRun()
    {
        var logger = new RunLogger(_fs, _time);
        var first = NewContext("20261017-093015-0001");
        var second = NewContext("20261017-093016-0002");
        // A file where the runs folder must be makes every write fail.
        _fs.AddFile(_runs, new MockFileData(""));

        logger.Start(first);
        logger.Start(second);

        Assert.Single(first.Messages, m => m.Contains("실행 로그를 쓰지 못했습니다"));
        Assert.Single(second.Messages, m => m.Contains("실행 로그를 쓰지 못했습니다"));
    }
}
