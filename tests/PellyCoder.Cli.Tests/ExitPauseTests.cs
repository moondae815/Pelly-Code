using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

public class ExitPauseTests
{
    [Fact]
    public void ShouldPause_FlagAndConsole_True() =>
        Assert.True(ExitPause.ShouldPause(["cs", "A.cs", "--pause-on-exit"], inputRedirected: false));

    [Fact]
    public void ShouldPause_RedirectedInput_NeverPauses() =>
        Assert.False(ExitPause.ShouldPause(["cs", "A.cs", "--pause-on-exit"], inputRedirected: true));

    [Fact]
    public void ShouldPause_AfterDoubleDash_Ignored() =>
        Assert.False(ExitPause.ShouldPause(["cs", "A.cs", "--", "--pause-on-exit"], inputRedirected: false));

    [Fact]
    public void ShouldPause_ParseErrorStillPauses() =>
        Assert.True(ExitPause.ShouldPause(["cs", "--bogus", "--pause-on-exit"], inputRedirected: false));

    [Fact]
    public void ShouldPause_NoFlag_False() =>
        Assert.False(ExitPause.ShouldPause(["cs", "A.cs", "--PAUSE-ON-EXIT"], inputRedirected: false));

    [Fact]
    public void Wait_ReturnsOnKey()
    {
        var polls = 0;
        var read = 0;

        ExitPause.Wait(new TestConsole(), 0, () => ++polls >= 3, () => read++, CancellationToken.None);

        Assert.Equal(3, polls);
        Assert.Equal(1, read);
    }

    [Fact]
    public void Wait_ReturnsOnShutdown()
    {
        using var cts = new CancellationTokenSource();
        var polls = 0;

        ExitPause.Wait(new TestConsole(), 2, () => { if (++polls == 2) cts.Cancel(); return false; }, () => { }, cts.Token);

        Assert.Equal(2, polls);
    }

    [Fact]
    public void Wait_NoConsole_StopsWaiting()
    {
        ExitPause.Wait(new TestConsole(), 0, () => throw new InvalidOperationException(), () => { }, CancellationToken.None);
    }

    [Fact]
    public void Wait_PrintsExitCode()
    {
        var console = new TestConsole();

        ExitPause.Wait(console, 4, () => true, () => { }, CancellationToken.None);

        Assert.Contains("종료 코드 4. 아무 키나 누르면 창을 닫습니다", console.Output);
    }
}
