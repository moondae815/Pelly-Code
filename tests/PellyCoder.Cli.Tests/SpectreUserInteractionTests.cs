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
