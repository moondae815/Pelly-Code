using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Testing;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

public class ReportCommandTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static (CommandAppTester App, TestConsole Console, MockFileSystem Fs) Build(MockFileSystem? shared = null)
    {
        var fs = shared ?? new MockFileSystem(new Dictionary<string, MockFileData>(), "/w");
        fs.AddDirectory("/w/.git");
        var console = new TestConsole().Width(200);
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<IAnsiConsole>(console);
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return (app, console, fs);
    }

    private static void AddRun(MockFileSystem fs, string id, int exit, int attempts = 1, string? failure = null)
    {
        var s = new RunSummary(id, "1.0", null, "m", "cs-v1", EditKind.CSharp, "Sdk", exit, attempts,
            new Dictionary<RunPhase, double> { [RunPhase.Edit] = 2 }, 0.8, 0.1, "Retry", 100, false, 10, null, null, [], [], 0, failure);
        fs.AddFile($"/w/.pelly/runs/{id}/summary.json", new MockFileData(JsonSerializer.Serialize(s, Json)));
    }

    private static void AddTestFirstRun(MockFileSystem fs, string id, int exit, RedState red, GreenState green, int regressions)
    {
        var s = new RunSummary(id, "1.0", null, "m", "cs-v1", EditKind.CSharp, "Sdk", exit, 1,
            new Dictionary<RunPhase, double> { [RunPhase.Edit] = 2 }, 0.8, 0.1, "Retry", 100, false, 10, null, null, [], [], 0, null,
            TestFirst: new TestFirstSummary(1, red, green, regressions, 0));
        fs.AddFile($"/w/.pelly/runs/{id}/summary.json", new MockFileData(JsonSerializer.Serialize(s, Json)));
    }

    [Fact]
    public void ReportCommand_ShowsAcceptanceRate()
    {
        var (app, _, fs) = Build();
        AddTestFirstRun(fs, "20261001-101010-aaaa", 0, RedState.Red, GreenState.Passed, 0);
        AddTestFirstRun(fs, "20261002-101010-bbbb", 1, RedState.Red, GreenState.Failed, 0);
        AddTestFirstRun(fs, "20261003-101010-cccc", 0, RedState.AlreadyPassed, GreenState.Passed, 0);

        var result = app.Run("report");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("수용 기준 충족률", result.Output);
        Assert.Contains("50.0% (1/2)", result.Output);
        Assert.Contains("RED 확인 경고율", result.Output);
        Assert.Contains("33.3% (1/3)", result.Output);
    }

    [Fact]
    public void Report_WithoutTestFirstRuns_HasNoAcceptanceRows()
    {
        var (app, _, fs) = Build();
        AddRun(fs, "20261001-101010-aaaa", 0);

        Assert.DoesNotContain("수용 기준 충족률", app.Run("report").Output);
    }

    [Fact]
    public void Report_NoRuns_Exit0_WithPath()
    {
        var (app, _, _) = Build();
        var result = app.Run("report");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("실행 기록이 없습니다", result.Output);
        Assert.Contains(".pelly", result.Output);
    }

    [Fact]
    public void Report_PrintsPrdRowsInOrder()
    {
        var (app, _, fs) = Build();
        AddRun(fs, "20261001-101010-aaaa", 0);
        AddRun(fs, "20261002-101010-bbbb", 1, failure: "BuildErrors");

        var result = app.Run("report");

        Assert.Equal(0, result.ExitCode);
        var order = new[]
        {
            "독립 검증 없음 — 벤치마크 실행기로 측정", "초기 응답 지연(TTFT)", "대기(Queue)", "추론 예산 제어(Thinking)",
            "패치 포맷 파싱 성공률", "최종 작업 성공률", "단일 작업 완료 시간", "BuildErrors",
        };
        var last = -1;
        foreach (var label in order)
        {
            var at = result.Output.IndexOf(label, StringComparison.Ordinal);
            Assert.True(at > last, $"'{label}' missing or out of order in:\n{result.Output}");
            last = at;
        }
        Assert.Contains("검증 불변 조건", result.Output);
    }

    private static void AddApply(MockFileSystem fs, string id, int exit, bool archived, int taskCount, params ApplyTaskResult[] tasks)
    {
        var s = new ApplySummary(id, "spec-x", tasks, exit, [], archived, TaskCount: taskCount);
        fs.AddFile($"/w/.pelly/runs/{id}/apply.json", new MockFileData(JsonSerializer.Serialize(s, Json)));
    }

    [Fact]
    public void Shows_spec_completion_even_without_task_runs()
    {
        var (app, _, fs) = Build();
        AddApply(fs, "apply-20261001-101010-aaaa", 0, true, 4, new ApplyTaskResult("T1", "R", 0, null, 1, "G1"));
        AddApply(fs, "apply-20261002-101010-bbbb", 1, false, 5, new ApplyTaskResult("T1", "R", 0, "GroupBuildFailed", 1, "G1"));

        var result = app.Run("report");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("실행 기록이 없습니다", result.Output);
        Assert.Contains("명세 완료율", result.Output);
        Assert.Contains("50.0% (1/2건, 이어서 하기 0건 제외)", result.Output);
        Assert.Contains("명세 완료율(작업 3~8개)", result.Output);
        Assert.Contains("50.0% (1/2그룹, 검증 불가 0그룹 제외)", result.Output);
    }

    [Fact]
    public void Spec_rows_show_a_dash_without_a_denominator()
    {
        var (app, _, fs) = Build();
        AddRun(fs, "20261001-101010-aaaa", 0);
        AddApply(fs, "apply-20261001-111111-bbbb", 0, true, 2);

        var result = app.Run("report");

        Assert.Contains("명세 완료율(작업 3~8개)", result.Output);
        Assert.Contains("-", result.Output);
        Assert.Contains("그룹 빌드 통과율", result.Output);
    }

    [Fact]
    public void Report_Since_AppliesToApplies()
    {
        var (app, _, fs) = Build();
        AddApply(fs, "apply-20261001-101010-aaaa", 0, true, 4);

        var result = app.Run("report", "--since", "2026-10-02");

        Assert.Contains("실행 기록이 없습니다", result.Output);
    }

    [Fact]
    public void Report_Since_FiltersByDate()
    {
        var (app, _, fs) = Build();
        AddRun(fs, "20261001-101010-aaaa", 0);
        var filtered = app.Run("report", "--since", "2026-10-02");
        Assert.Equal(0, filtered.ExitCode);
        Assert.Contains("실행 기록이 없습니다", filtered.Output);
    }

    [Fact]
    public void Report_Since_BadDate_IsRejected()
    {
        var (app, _, _) = Build();
        var result = app.Run("report", "--since", "oops");
        Assert.Equal(ExitCodes.EnvError, CliApp.NormalizeExitCode(result.ExitCode));
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void ReportCommand_Sanitize_WritesTwoFiles()
    {
        var (app, _, fs) = Build();
        AddRun(fs, "20261001-101010-aaaa", 0);
        AddRun(fs, "20261002-101010-bbbb", 1, failure: @"C:\Users\kim\Secret.cs");

        var result = app.Run("report", "--sanitize", "--out", "/out");

        Assert.Equal(0, result.ExitCode);
        var files = fs.Directory.GetFiles("/out").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, files.Count);
        Assert.Matches(@"pelly-report-\d{8}-\d{6}\.json$", files[0]);
        Assert.Matches(@"pelly-report-\d{8}-\d{6}\.md$", files[1]);
        foreach (var f in files)
        {
            Assert.Contains(Path.GetFileName(f), result.Output.Replace("\n", ""));
            var text = fs.File.ReadAllText(f);
            Assert.DoesNotContain("Secret", text);
            Assert.DoesNotContain("aaaa", text);
            Assert.Contains("Other", text);
        }
    }

    [Fact]
    public void Sanitize_SameSecond_DoesNotOverwrite()
    {
        var (app, _, fs) = Build();
        AddRun(fs, "20261001-101010-aaaa", 0);
        Assert.Equal(0, app.Run("report", "--sanitize", "--out", "/out").ExitCode);
        var (second, _, _) = Build(fs);
        Assert.Equal(0, second.Run("report", "--sanitize", "--out", "/out").ExitCode);
        Assert.Equal(4, fs.Directory.GetFiles("/out").Length);
    }

    [Fact]
    public void Sanitize_InvalidOutPath_Exit4()
    {
        var (app, _, fs) = Build();
        AddRun(fs, "20261001-101010-aaaa", 0);
        var result = app.Run("report", "--sanitize", "--out", "bad\0path");
        Assert.Equal(ExitCodes.EnvError, CliApp.NormalizeExitCode(result.ExitCode));
        Assert.Contains("저장할 수 없습니다", result.Output);
    }

    [Fact]
    public void Out_WithoutSanitize_IsRejected()
    {
        var (app, _, _) = Build();
        var result = app.Run("report", "--out", "/out");
        Assert.Equal(ExitCodes.EnvError, CliApp.NormalizeExitCode(result.ExitCode));
        Assert.Contains("--out은 --sanitize와 함께 쓰세요", result.Output);
    }

    [Fact]
    public void Sanitize_NoRuns_WritesNothing_Exit0()
    {
        var (app, _, fs) = Build();
        var result = app.Run("report", "--sanitize", "--out", "/out");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("비식별 리포트를 쓰지 않았습니다 (실행 기록 없음)", result.Output);
        Assert.False(fs.Directory.Exists("/out"));
    }
}
