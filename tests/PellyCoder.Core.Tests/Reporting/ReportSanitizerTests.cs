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

    [Theory]
    [InlineData("CS0103\n")]
    [InlineData("CS\u0661\u0662")]
    [InlineData(" CS0103")]
    [InlineData("cs0103")]
    [InlineData("Msg01012345678")]
    [InlineData("OrderService")]
    public void ErrorCodes_RejectsBypasses(string code) => Assert.False(ReportSanitizer.IsAllowedCode(code));

    [Theory]
    [InlineData("CS0103")]
    [InlineData("SQL46010")]
    [InlineData("Msg207")]
    [InlineData("NU1101")]
    [InlineData("MSB3202")]
    [InlineData("TEST")]
    public void ErrorCodes_AcceptsKnownShapes(string code) => Assert.True(ReportSanitizer.IsAllowedCode(code));

    [Theory]
    [InlineData("1.0\n")]
    [InlineData("OrderService")]
    [InlineData("kim")]
    [InlineData("")]
    public void Version_RejectsBypasses(string v) => Assert.Equal("invalid", ReportSanitizer.Version(v));

    [Theory]
    [InlineData("0.40.0")]
    [InlineData("1.0.0+abc123")]
    public void Version_AcceptsNumericStart(string v) => Assert.Equal(v, ReportSanitizer.Version(v));

    [Theory]
    [InlineData("cs-v2\n")]
    [InlineData("cs-v\u0663")]
    [InlineData("cs-v12345")]
    [InlineData("cs-v2 OrderService")]
    public void PromptVersion_RejectsBypasses(string v) => Assert.Equal("invalid", ReportSanitizer.PromptVersion(v));

    [Fact]
    public void Json_DictionaryKeysKeepExactNames()
    {
        var runs = new[] { Rec(Injected()) };
        var json = ReportSanitizer.Sanitize(runs, RunReport.Build(runs)).ToJson();
        Assert.Contains("\"Edit\":", json);
        Assert.Contains("\"Other\":", json);
    }

    [Fact]
    public void Sanitize_OnlySummaryJsonIsRead()
    {
        var inner = new MockFileSystem(new Dictionary<string, MockFileData>(), "/w");
        inner.AddDirectory("/w/.git");
        var s = Injected();
        RunHistoryTests.Write(inner, s);
        inner.AddFile($"/w/.pelly/runs/{s.RunId}/events.jsonl", new MockFileData("secret events"));
        inner.AddFile($"/w/.pelly/runs/{s.RunId}/attempt-1/prompt.txt", new MockFileData("secret prompt"));
        var fs = ReadGuard.Wrap(inner);

        // the guard itself works
        Assert.Throws<InvalidOperationException>(() => fs.File.ReadAllText($"/w/.pelly/runs/{s.RunId}/events.jsonl"));

        var ws = WorkspaceLocator.Resolve(fs, "/w/pelly-report");
        var history = RunHistory.Load(fs, ws, null);
        var report = ReportSanitizer.Sanitize(history.Runs, RunReport.Build(history.Runs));

        Assert.Single(report.Runs);
        Assert.DoesNotContain("secret", report.ToJson());
    }
}

/// <summary>File system whose read/open methods throw for any path that is not a summary.json.</summary>
internal class ReadGuard : System.Reflection.DispatchProxy
{
    private object _target = null!;
    private bool _isFile;

    public static System.IO.Abstractions.IFileSystem Wrap(System.IO.Abstractions.IFileSystem inner)
    {
        var p = Create<System.IO.Abstractions.IFileSystem, ReadGuard>();
        var g = (ReadGuard)(object)p;
        g._target = inner;
        return p;
    }

    protected override object? Invoke(System.Reflection.MethodInfo? m, object?[]? args)
    {
        if (_isFile && m!.Name is not ("Exists" or "WriteAllText") && args?.FirstOrDefault() is string path
            && !path.EndsWith("summary.json", StringComparison.Ordinal))
            throw new InvalidOperationException($"unexpected file access: {m.Name} {path}");
        var result = m!.Invoke(_target, args);
        if (!_isFile && m.Name == "get_File")
        {
            var p = Create<System.IO.Abstractions.IFile, ReadGuard>();
            var g = (ReadGuard)(object)p;
            g._target = result!;
            g._isFile = true;
            return p;
        }
        return result;
    }
}
