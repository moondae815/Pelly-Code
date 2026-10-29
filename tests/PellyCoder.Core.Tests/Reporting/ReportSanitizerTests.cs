using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Testing;
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
    public void Sanitize_TestFirstHasNoNames()
    {
        // The raw summary carries a test name in a free-text field (failure kind); only enum names and numbers may survive.
        var s = Injected() with
        {
            ExitCode = 0,
            FailureKind = "Calculator.Add_Returns_Sum",
            TestFirst = new TestFirstSummary(1, RedState.Red, GreenState.Passed, 0, 0),
        };
        var runs = new[] { Rec(s) };
        var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs));

        foreach (var text in new[] { report.ToJson(), report.ToMarkdown() })
        {
            Assert.DoesNotContain("Add_Returns_Sum", text);
            Assert.DoesNotContain("Calculator", text);
        }
        var run = Assert.Single(report.Runs);
        Assert.Equal("Red", run.Red);
        Assert.Equal("Passed", run.Green);
        Assert.Equal(0, run.TestRegressions);
        Assert.Contains("\"red\": \"Red\"", report.ToJson());
        Assert.Contains("\"green\": \"Passed\"", report.ToJson());
        Assert.Contains("| 수용 기준 충족률 | 100.0% (1/1) |", report.ToMarkdown());
        Assert.Contains("| RED 확인 경고율 | 0.0% (0/1) |", report.ToMarkdown());
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
    public void Apply_aggregate_has_counts_only()
    {
        var applies = new[]
        {
            ApplyReportTests.Apply("apply-20261018-101010-abcd", 0, archived: true, taskCount: 4,
                tasks: [ApplyReportTests.T("G1"), ApplyReportTests.T("G2", kind: "GroupBuildFailed")]),
        };
        var data = ApplyReport.Build(applies);
        var runs = new[] { Rec(Injected()) };

        var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs), data);

        Assert.Equal(new ApplyCounts(1, 1, 1, 1, 1, 2), report.Aggregate.Apply);
        foreach (var text in new[] { report.ToJson(), report.ToMarkdown() })
        {
            Assert.DoesNotContain("apply-2026", text);
            Assert.DoesNotContain("spec", text);
            Assert.DoesNotContain("G1", text);
            Assert.DoesNotContain("GroupBuildFailed", text);
        }
        Assert.Contains("\"apply\":", report.ToJson());
        Assert.Contains("명세 완료율", report.ToMarkdown());
    }

    private static IReadOnlyList<string> Lines(string md) => md.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void Markdown_keeps_a_blank_line_after_the_summary_table_without_apply_data()
    {
        var runs = new[] { Rec(Injected()) };
        var lines = Lines(ReportSanitizer.Sanitize(runs, RunReport.Build(runs)).ToMarkdown());

        var at = lines.ToList().IndexOf("## 종류");
        Assert.True(at > 1);
        Assert.Equal("", lines[at - 1]);
        Assert.StartsWith("| 단일 작업 완료 시간", lines[at - 2]);
        Assert.DoesNotContain("## 명세", lines);
    }

    [Fact]
    public void Markdown_keeps_blank_lines_around_the_apply_section()
    {
        var runs = new[] { Rec(Injected()) };
        var apply = ApplyReport.Build([ApplyReportTests.Apply("apply-20261018-101010-abcd", 0, archived: true)]);
        var lines = Lines(ReportSanitizer.Sanitize(runs, RunReport.Build(runs), apply).ToMarkdown()).ToList();

        var at = lines.IndexOf("## 명세");
        Assert.True(at > 1);
        Assert.Equal("", lines[at - 1]);
        Assert.StartsWith("| 단일 작업 완료 시간", lines[at - 2]);
        var next = lines.IndexOf("## 종류");
        Assert.Equal("", lines[next - 1]);
        Assert.StartsWith("| 그룹 빌드 통과율", lines[next - 2]);
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
    [InlineData("RULES")]
    public void ErrorCodes_RejectsBypasses(string code) => Assert.False(ReportSanitizer.IsAllowedCode(code));

    [Theory]
    [InlineData("CS0103")]
    [InlineData("SQL46010")]
    [InlineData("Msg207")]
    [InlineData("NU1101")]
    [InlineData("MSB3202")]
    [InlineData("TEST")]
    [InlineData("RULE")]
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

    [Fact]
    public void Sanitize_NoAcIds()
    {
        var applyRec = ApplyReportTests.Apply("apply-1") with { };
        applyRec = applyRec with
        {
            Summary = applyRec.Summary with
            {
                Criteria = [new AcResult("AC-SecretCriterion", RedState.Red, AcOutcome.Met, "run-secret", 2),
                            new AcResult("AC-Other", RedState.NotChecked, AcOutcome.Unverified, null, 0)],
            },
        };
        var runs = new[] { Rec(Injected()) };
        var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs), ApplyReport.Build([applyRec]));

        foreach (var text in new[] { report.ToJson(), report.ToMarkdown() })
        {
            Assert.DoesNotContain("SecretCriterion", text);
            Assert.DoesNotContain("AC-Other", text);
            Assert.DoesNotContain("run-secret", text);
        }
        var md = report.ToMarkdown();
        Assert.Contains("| 수용 기준 충족률 | 100.0% (1/1) |", md);
        Assert.Contains("| 테스트로 검증되지 않은 수용 기준 | 1건 |", md);
        Assert.Contains("| breaks로 뺀 테스트 | 2건 |", md);
    }

    private static ApplyRecord ApplyWith(params AcResult[] acs)
    {
        var rec = ApplyReportTests.Apply("apply-1");
        return rec with { Summary = rec.Summary with { Criteria = acs } };
    }

    [Fact]
    public void Sanitize_AllUnverifiedAc_ZeroDenominatorShowsDash()
    {
        var runs = new[] { Rec(Injected()) };
        var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs),
            ApplyReport.Build([ApplyWith(new AcResult("AC1", RedState.NotChecked, AcOutcome.Unverified, null, 0))]));

        var md = report.ToMarkdown();
        Assert.Contains("| 수용 기준 충족률 | - (0/0) |", md);
        Assert.Contains("| 테스트로 검증되지 않은 수용 기준 | 1건 |", md);
    }

    [Fact]
    public void Sanitize_CombinesSingleRunAndCriteria()
    {
        var single = Injected() with
        {
            ExitCode = 0,
            TestFirst = new TestFirstSummary(1, RedState.Red, GreenState.Passed, 0, 1),
        };
        var runs = new[] { Rec(single) };
        var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs), ApplyReport.Build([ApplyWith(
            new AcResult("AC1", RedState.Red, AcOutcome.Met, "r1", 2),
            new AcResult("AC2", RedState.Red, AcOutcome.NotMet, "r2", 0))]));

        var md = report.ToMarkdown();
        Assert.Contains("| 수용 기준 충족률 | 66.7% (2/3) |", md);
        Assert.Contains("| breaks로 뺀 테스트 | 3건 |", md);
        var tf = report.Aggregate.TestFirst!;
        Assert.Equal(2, tf.Met);
        Assert.Equal(3, tf.Judged);
        using var doc = System.Text.Json.JsonDocument.Parse(report.ToJson());
        var el = doc.RootElement.GetProperty("aggregate").GetProperty("testFirst");
        Assert.Equal(2, el.GetProperty("met").GetInt32());
        Assert.Equal(3, el.GetProperty("judged").GetInt32());
    }

    [Fact]
    public void Sanitize_CharTestHasNoNames()
    {
        // Names and paths ride in free-text fields (failure kind, error codes, framework); only counts and known kinds survive.
        var s = RunHistoryTests.Summary("20261018-101010-abcd", 1, 2, failure: @"Calculator.Add_Returns_Sum in C:\src\Calc.cs", kind: EditKind.CSharpTest) with
        {
            PromptVersion = "cs-test-v1",
            CharTest = new PellyCoder.Core.CharTests.CharTestSummary(3, 3, 0, 3, true, 1, "CalculatorTests.Add_Returns_Sum", "xUnit-CalculatorTests", true),
        };
        var ok = RunHistoryTests.Summary("20261018-101011-abcd", 1, 3, failure: "CharTestFlaky", kind: EditKind.CSharpTest) with { PromptVersion = "cs-test-v1" };
        var runs = new[] { Rec(s), Rec(ok) };

        var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs));

        foreach (var text in new[] { report.ToJson(), report.ToMarkdown() })
        {
            foreach (var leak in new[] { "Calculator", "Add_Returns_Sum", "Calc.cs", "C:", "xUnit-", "20261018-101010" })
                Assert.DoesNotContain(leak, text);
            Assert.Contains("CharTestFlaky", text);
            Assert.Contains("CSharpTest", text);
            Assert.Contains("cs-test-v1", text);
            Assert.DoesNotContain("invalid", text);
        }
        Assert.Contains("특성 테스트: 실행 2건, 남김 0건(성공률 0.0%), 평균 시도 2.5, 실패 유형 CharTestFlaky 1, Other 1", report.ToMarkdown());
        Assert.Equal(2, report.Aggregate.CharTest!.Runs);
    }

    [Fact]
    public void Sanitize_OnlyCharTestRuns_DryRunNotKept()
    {
        var kept = RunHistoryTests.Summary("20261018-101010-abcd", 0, 1, kind: EditKind.CSharpTest) with
        {
            PromptVersion = "cs-test-v1",
            CharTest = new PellyCoder.Core.CharTests.CharTestSummary(3, 3, 3, 3, true, 3, null, "xUnit", false),
        };
        var dry = kept with { RunId = "20261018-101011-abcd", CharTest = kept.CharTest! with { Kept = 0 } };
        var runs = new[] { Rec(kept), Rec(dry) };

        var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs));

        Assert.Equal(0, report.Aggregate.RunCount);
        Assert.Equal(2, report.Aggregate.CharTest!.Runs);
        Assert.Equal(1, report.Aggregate.CharTest.Kept);
        Assert.Contains("특성 테스트: 실행 2건, 남김 1건(성공률 50.0%), 평균 시도 1.0, 실패 유형 없음", report.ToMarkdown());
        Assert.DoesNotContain("invalid", report.ToJson());
    }

    [Fact]
    public void Sanitize_WithoutCharTestRuns_HasNoCharTestOutput()
    {
        var runs = new[] { Rec(Injected()) };
        var report = ReportSanitizer.Sanitize(runs, RunReport.Build(runs));

        Assert.Null(report.Aggregate.CharTest);
        Assert.DoesNotContain("특성 테스트", report.ToMarkdown());
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
