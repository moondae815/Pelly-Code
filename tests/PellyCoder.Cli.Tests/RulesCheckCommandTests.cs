using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Config;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

public class RulesCheckCommandTests
{
    private static CommandAppTester Build(string? agents, out TestConsole console)
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/w/.git");
        if (agents is not null) fs.AddFile("/w/AGENTS.md", new MockFileData(agents));
        fs.Directory.SetCurrentDirectory("/w");
        console = new TestConsole().Width(200);
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<IAnsiConsole>(console);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return app;
    }

    [Fact]
    public void Check_NoFile_Exit0_SaysNoRules()
    {
        var result = Build(null, out _).Run("rules", "check");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("규칙이 없습니다", result.Output);
        Assert.Contains("pelly rules init", result.Output);
    }

    [Fact]
    public void Check_Valid_Exit0_ShowsTable()
    {
        var md = "# A\n\n## Pelly rules\n\n- [*] Use tabs\n- [Dapper,sql] Parameterize queries | forbid: string\\.Format\n\n## Other\n";
        var result = Build(md, out _).Run("rules", "check");
        Assert.Equal(0, result.ExitCode);
        foreach (var h in new[] { "줄", "태그", "규칙", "토큰", "forbid" }) Assert.Contains(h, result.Output);
        Assert.Contains("Use tabs", result.Output);
        Assert.Contains("string\\.Format", result.Output);
        Assert.Contains("규칙 2개, 항상 넣는 규칙 1개", result.Output);
        Assert.Contains("토큰 / budget.rules 150", result.Output);
        Assert.DoesNotContain("예산을 넘어", result.Output);
    }

    [Fact]
    public void Check_Errors_Exit4_ListsLines()
    {
        var md = "## Pelly rules\n- no tags here\n- [*] ok\n";
        var result = Build(md, out _).Run("rules", "check");
        Assert.Equal(4, result.ExitCode);
        Assert.Contains("AGENTS.md 규칙 오류 1건", result.Output);
        Assert.Contains("줄 2:", result.Output);
    }

    [Fact]
    public void Check_AlwaysRulesOverBudget_Warns()
    {
        var lines = string.Concat(Enumerable.Range(0, 12).Select(i => $"- [*] Rule number {i} says always do the thing carefully and completely, every time\n"));
        var result = Build("## Pelly rules\n" + lines, out _).Run("rules", "check");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("항상 넣는 규칙만으로 예산을 넘어 아래쪽 규칙이 빠집니다", result.Output);
    }

    [Fact]
    public void FindOptionWithoutValue_RulesCheck_ConfigWithoutValue()
    {
        Assert.Equal("--config", CliApp.FindOptionWithoutValue(["rules", "check", "--config"]));
        Assert.Null(CliApp.FindOptionWithoutValue(["rules", "check", "--config", "/w/pelly.json"]));
        Assert.Null(CliApp.FindOptionWithoutValue(["rules", "bogus", "--config"]));
        Assert.Null(CliApp.FindOptionWithoutValue(["rules"]));
    }
}
