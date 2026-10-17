using System.Diagnostics;
using PellyCoder.Cli;

namespace PellyCoder.Cli.Tests;

public class ShutdownCoordinatorTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Signal_CancelsToken_AndWaitsUntilCompletionIsSignalled()
    {
        using var shutdown = new ShutdownCoordinator(Long);
        var handler = Task.Run(shutdown.RequestShutdownAndWait, TestContext.Current.CancellationToken);

        // The token is cancelled while the handler is still blocked.
        await Task.Run(() => SpinWait.SpinUntil(() => shutdown.Token.IsCancellationRequested, Long), TestContext.Current.CancellationToken);
        Assert.True(shutdown.Token.IsCancellationRequested);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(handler.IsCompleted);

        shutdown.SignalCompleted();

        Assert.True(await handler.WaitAsync(Long, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CompletionAlreadySignalled_ReturnsImmediately()
    {
        using var shutdown = new ShutdownCoordinator(Long);
        shutdown.SignalCompleted();

        var sw = Stopwatch.StartNew();
        Assert.True(shutdown.RequestShutdownAndWait());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(shutdown.Token.IsCancellationRequested);
    }

    [Fact]
    public void NoCompletion_WaitIsBoundedByTimeout()
    {
        using var shutdown = new ShutdownCoordinator(TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        Assert.False(shutdown.RequestShutdownAndWait());
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(150));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void RepeatedSignals_AreIdempotent()
    {
        using var shutdown = new ShutdownCoordinator(TimeSpan.FromMilliseconds(50));
        shutdown.RequestCancel();
        shutdown.RequestCancel();
        Assert.False(shutdown.RequestShutdownAndWait());
        shutdown.SignalCompleted();
        Assert.True(shutdown.RequestShutdownAndWait());
        Assert.True(shutdown.Token.IsCancellationRequested);
    }

    [Fact]
    public void SignalAfterDispose_DoesNotThrow()
    {
        var shutdown = new ShutdownCoordinator(Long);
        shutdown.Dispose();

        var sw = Stopwatch.StartNew();
        shutdown.RequestCancel();
        shutdown.RequestShutdownAndWait();
        shutdown.SignalCompleted();
        shutdown.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DefaultTimeout_StaysUnderWindowsCloseGrace()
    {
        Assert.Equal(TimeSpan.FromSeconds(4.5), ShutdownCoordinator.DefaultCompletionWait);
    }
}
