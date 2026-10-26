using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Ui.Session;
using PellyCoder.Ui.Tests.Session;
using PellyCoder.Ui.Views;

namespace PellyCoder.Ui.Tests.Views;

/// <summary>
/// The UI loop failing while a run is active (a throwing Changed handler, a render fault): <see cref="UiHost"/> cancels the
/// run and waits for its rollback before the exception leaves. The real <c>Application.Run</c> cannot be nested inside the
/// headless test session (it owns the dispatcher loop), so the loop is a delegate here.
/// </summary>
public sealed class UiHostLoopTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly GuiUserInteraction _ui = new(new InlineDispatcher());

    private UiSession NewSession(RunFunc run) => new(run, _ui, [], CancellationToken.None) { Request = "요청" };

    private async Task StartAndWaitForQuestion(UiSession session)
    {
        _ = session.RunAsync();
        var deadline = DateTime.UtcNow + Limit;
        while (!(_ui.Question is not null && session.State == SessionState.AwaitingAnswer))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("question not shown");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LoopThrowsWhileAwaitingAnswer_CancelsRunThenRethrows()
    {
        var rolledBack = false;
        var session = NewSession(async (_, ui, ct) =>
        {
            try { return await ui.ApproveAsync("적용할까요?", ct) ? 0 : 2; }
            catch (OperationCanceledException)
            {
                await Task.Delay(100, CancellationToken.None); // rollback takes a moment
                rolledBack = true;
                throw;
            }
        });
        await StartAndWaitForQuestion(session);
        var error = new StringWriter();

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            UiHost.RunLoop(session, () => throw new InvalidOperationException("Changed handler failed"), error, Limit));

        Assert.Equal("Changed handler failed", thrown.Message);
        Assert.True(rolledBack);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task LoopThrows_RollbackTimesOut_WarnsOnStderr()
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = NewSession((_, _, _) =>
        {
            started.SetResult();
            return release.Task;
        });
        _ = session.RunAsync();
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        var error = new StringWriter();

        Assert.Throws<InvalidOperationException>(() =>
            UiHost.RunLoop(session, () => throw new InvalidOperationException("render fault"), error, TimeSpan.FromMilliseconds(100)));

        Assert.Equal(
            "실행 중이던 작업을 0.1초 안에 되돌리지 못했습니다. 파일에 승인되지 않은 변경이 남아 있을 수 있습니다. 원본과 비교해 확인하십시오."
            + Environment.NewLine, error.ToString());
        release.SetResult(ExitCodes.UserCancelled);
    }

    [Fact]
    public void LoopEndsNormally_NoAbort()
    {
        var session = NewSession((_, _, _) => Task.FromResult(0));
        var error = new StringWriter();
        var ran = false;

        UiHost.RunLoop(session, () => ran = true, error, Limit);

        Assert.True(ran);
        Assert.Equal("", error.ToString());
    }
}
