using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
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

    private static (CommandAppTester App, TestConsole Console, MockFileSystem Fs) Build()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>(), "/w");
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
}
