using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Reporting;

public sealed class ReportSanitizerTests
{
    private static RunRecord Rec(RunSummary s) => new(s, DateTimeOffset.UnixEpoch);

    private static RunSummary Injected() => new(
        "20261018-101010-abcd", "1.0; DROP TABLE", "0.40.0", "secret-model", "cs-v2 OrderService",
        EditKind.CSharp, "Sdk", 1, 2,
        new Dictionary<RunPhase, double> { [RunPhase.Edit] = 2.5, [RunPhase.Verify] = 1.25 }, 0.8, 0.1,
        "Retry", 120, true, 1000, 900, 400, [MatchStage.Exact], ["CS0103", "CustomerService.Cancel", "Msg207 dbo.Orders", "TEST"], 1,
        @"C:\Users\kim\OrderService.cs");

    [Fact]
    public void Sanitize_InjectedStrings_NeverAppear()
    {
        var runs = new[] { Rec(Injected()) };
        var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs));

        foreach (var text in new[] { report.ToJson(), report.ToMarkdown() })
        {
            foreach (var leak in new[] { "secret-model", "Users", "OrderService", "CustomerService", "dbo.Orders", "DROP", "20261018-101010" })
                Assert.DoesNotContain(leak, text);
            foreach (var kept in new[] { "CS0103", "TEST", "0.40.0", "Other", "invalid" })
                Assert.Contains(kept, text);
        }
    }

    [Fact]
    public void Sanitize_KeepsNumbers()
    {
        var runs = new[] { Rec(Injected()) };
        var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs));
        var r = Assert.Single(report.Runs);

        Assert.Equal(1, r.Index);
        Assert.Equal(2.5, r.PhaseSeconds["Edit"]);
        Assert.Equal(1.25, r.PhaseSeconds["Verify"]);
        Assert.Equal(0.8, r.TtftSeconds);
        Assert.Equal(0.1, r.QueueSeconds);
        Assert.Equal((120, 1000, 900, 400, 2), (r.ThinkTokens, r.InputTokensLocal, r.InputTokensServer, r.InputTokensCached, r.Attempts));
        Assert.Equal(["Exact"], r.MatchStages);
        Assert.Equal(1, report.Aggregate.FailureKinds["Other"]);
    }

    [Fact]
    public void Sanitize_UsesInvariantCulture()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var runs = new[] { Rec(Injected()) };
            var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs));
            Assert.Contains("2.50", report.ToMarkdown());
            Assert.Contains("2.5", report.ToJson());
            Assert.DoesNotContain("2,5", report.ToJson());
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    [Fact]
    public void Sanitize_DoesNotOpenOtherFiles()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>(), "/w");
        fs.AddDirectory("/w/.git");
        var s = Injected();
        RunHistoryTests.Write(fs, s);
        // Any read of these must fail: a sanitizer that touches them would crash instead of reporting.
        var locked = new MockFileData("secret events") { AllowedFileShare = FileShare.None };
        fs.AddFile($"/w/.pelly/runs/{s.RunId}/events.jsonl", locked);
        fs.AddFile($"/w/.pelly/runs/{s.RunId}/attempt-1/prompt.txt", new MockFileData("secret prompt") { AllowedFileShare = FileShare.None });

        Assert.ThrowsAny<Exception>(() => fs.File.ReadAllText($"/w/.pelly/runs/{s.RunId}/events.jsonl")); // the trap works

        var ws = WorkspaceLocator.Resolve(fs, "/w/pelly-report");
        var history = RunHistory.Load(fs, ws, null);
        var report = ReportSanitizer.Sanitize(history.Runs, RunReport.Build(history.Runs));

        Assert.Single(report.Runs);
        Assert.DoesNotContain("secret", report.ToJson());
    }
}
