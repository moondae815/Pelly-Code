using PellyCoder.Cli.Terminal;
using PellyCoder.Core.Fsm;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

public sealed class SpectreUserInteractionTests
{
    private static (SpectreUserInteraction Ui, TestConsole Console) Make()
    {
        var console = new TestConsole().Width(200);
        return (new SpectreUserInteraction(console), console);
    }

    [Fact]
    public void ShowStatus_AttemptZero_HasNoAttemptText()
    {
        var (ui, console) = Make();
        ui.ShowStatus(RunPhase.Preflight, 0, "서버 확인");
        Assert.DoesNotContain("시도", console.Output);
        Assert.Contains("Preflight", console.Output);
        Assert.Contains("서버 확인", console.Output);
    }

    private static PellyCoder.Core.Logging.RunSummary RulesSummary(IReadOnlyList<string>? included, IReadOnlyList<string>? dropped) =>
        new("r1", "1.0", null, "m", "p1", PellyCoder.Core.Models.EditKind.CSharp, "Sdk", 0, 1,
            new Dictionary<RunPhase, double>(), 0, 0, "Retry", 0, false, 0, null, null, [], [], 0, null, included, dropped);

    [Fact]
    public void ShowRunSummary_PrintsIncludedAndDroppedRules()
    {
        var (ui, console) = Make();

        ui.ShowRunSummary(RulesSummary(["주석은 한국어로 [작성]"], ["async 접미사"]), null);

        Assert.Contains("규칙: 포함 1, 제외 1", console.Output);
        Assert.Contains("+ 주석은 한국어로 [작성]", console.Output);
        Assert.Contains("- async 접미사", console.Output);
    }

    [Fact]
    public void ShowRunSummary_NoRules_NoRulesLine()
    {
        var (ui, console) = Make();

        ui.ShowRunSummary(RulesSummary(null, []), null);

        Assert.DoesNotContain("규칙:", console.Output);
    }

    [Fact]
    public void Spectre_ShowRunSummary_PrintsPhasesTokensAndPath()
    {
        var (ui, console) = Make();
        var summary = new PellyCoder.Core.Logging.RunSummary(
            "r1", "1.0", null, "m", "p1", PellyCoder.Core.Models.EditKind.CSharp, "Sdk", 0, 2,
            new Dictionary<RunPhase, double> { [RunPhase.Edit] = 12.5, [RunPhase.Verify] = 3.25 }, 1.5, 0.5,
            "Retry", 300, true, 4000, 3900, 3000,
            [], ["CS0103", "CS[x]"], 1, null);

        ui.ShowRunSummary(summary, "C:/w/.pelly/runs/[r1]");

        var output = console.Output;
        Assert.Contains("Edit", output);
        Assert.Contains("12.5", output);
        Assert.Contains("3.25", output);
        Assert.Contains("4000", output);
        Assert.Contains("3900", output);
        Assert.Contains("3000", output);
        Assert.Contains("300", output);
        Assert.Contains("CS0103", output);
        Assert.Contains("CS[x]", output);
        Assert.Contains("프롬프트·응답 원문: C:/w/.pelly/runs/[r1]", output);
    }

    [Fact]
    public void ShowStatus_AttemptTwo_ShowsAttempt()
    {
        var (ui, console) = Make();
        ui.ShowStatus(RunPhase.Edit, 2, "수정 중");
        Assert.Contains("(시도 2)", console.Output);
    }

    [Fact]
    public void Escape_BracketsInPathMessageAndDiff_DoNotThrow_AndRenderLiterally()
    {
        var (ui, console) = Make();
        ui.ShowStatus(RunPhase.Edit, 1, "파일 [x] 처리 [/]");
        ui.ShowStatus(RunPhase.Edit, 0, "[red]raw[/]");
        ui.Warn("경고 [bold]x[/] [");
        ui.ShowDiff("C:/a/[x]/B.cs", "int[] a = [1];\n", "int[] a = [1, 2];\n");

        Assert.Contains("파일 [x] 처리 [/]", console.Output);
        Assert.Contains("[red]raw[/]", console.Output);
        Assert.Contains("경고 [bold]x[/] [", console.Output);
        Assert.Contains("C:/a/[x]/B.cs", console.Output);
        Assert.Contains("+int[] a = [1, 2];", console.Output);
    }
}
