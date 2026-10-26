using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Ui.Session;

namespace PellyCoder.Ui.Tests.Session;

public sealed class UiSessionTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly GuiUserInteraction _ui = new(new InlineDispatcher());
    private readonly CancellationTokenSource _shutdown = new();

    public void Dispose() => _shutdown.Dispose();

    private UiSession NewSession(RunFunc run, IReadOnlyList<string>? errors = null) =>
        new(run, _ui, errors ?? [], _shutdown.Token) { Request = "배송 중인 주문은 취소할 수 없게 해줘" };

    /// <summary>Script: one status line, the final approval question, 0 when approved and 2 when rejected.</summary>
    private static async Task<int> ApproveScript(string request, IUserInteraction ui, CancellationToken ct)
    {
        ui.ShowStatus(RunPhase.Locate, 0, "OrderService.Cancel");
        return await ui.ApproveAsync("적용할까요?", ct) ? ExitCodes.Success : ExitCodes.UserCancelled;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private async Task<Task> StartAndWaitForQuestion(UiSession session)
    {
        var run = session.RunAsync();
        await WaitUntil(() => _ui.Question is not null && session.State == SessionState.AwaitingAnswer);
        return run;
    }

    [Fact]
    public async Task Approve_ExitsZero()
    {
        var session = NewSession(ApproveScript);

        var run = await StartAndWaitForQuestion(session);
        Assert.Equal(QuestionKind.Approve, _ui.Question!.Kind);
        _ui.Answer(1);
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Finished, session.State);
        Assert.Equal(0, session.ExitCode);
    }

    [Fact]
    public async Task Reject_ExitsTwo()
    {
        var session = NewSession(ApproveScript);

        var run = await StartAndWaitForQuestion(session);
        _ui.Answer(0);
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Finished, session.State);
        Assert.Equal(2, session.ExitCode);
    }

    [Fact]
    public async Task Cancel_WhileRunning_ExitsTwo()
    {
        var observed = false;
        var session = NewSession(async (_, ui, ct) =>
        {
            ui.ShowStatus(RunPhase.Edit, 1, "서버 응답 대기 중");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { observed = true; throw; }
            return 0;
        });

        var run = session.RunAsync();
        Assert.Equal(SessionState.Running, session.State);
        session.Cancel();
        Assert.Equal(SessionState.Cancelling, session.State);
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.True(observed);
        Assert.Equal(SessionState.Finished, session.State);
        Assert.Equal(2, session.ExitCode);
    }

    [Fact]
    public async Task Close_WhileAwaitingAnswer_CancelsThenCloseReady()
    {
        var session = NewSession(ApproveScript);
        var closeReady = 0;
        session.CloseReady += () => Interlocked.Increment(ref closeReady);

        var run = await StartAndWaitForQuestion(session);
        Assert.False(session.RequestClose());
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(1, closeReady);
        Assert.Equal(2, session.ExitCode);
        Assert.Null(_ui.Question);
    }

    [Fact]
    public async Task Close_WhileCancelling_IsIgnoredUntilRunEnds()
    {
        var rollbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rollbackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = NewSession(async (_, _, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException)
            {
                rollbackStarted.SetResult();
                await rollbackGate.Task; // rollback in progress
                throw;
            }
            return 0;
        });
        var closeReady = 0;
        session.CloseReady += () => Interlocked.Increment(ref closeReady);

        var run = session.RunAsync();
        Assert.False(session.RequestClose());
        await rollbackStarted.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal(SessionState.Cancelling, session.State);
        Assert.False(session.RequestClose());
        Assert.Equal(0, closeReady);

        rollbackGate.SetResult();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(1, closeReady);
        Assert.Equal(2, session.ExitCode);
        Assert.Equal(SessionState.Finished, session.State);
        Assert.True(session.RequestClose());
        Assert.Equal(1, closeReady);
    }

    [Fact]
    public void Close_BeforeAnyRun_ExitsTwo()
    {
        var session = NewSession(ApproveScript);

        Assert.Equal(SessionState.Idle, session.State);
        Assert.True(session.RequestClose());
        Assert.Equal(2, session.ExitCode);
    }

    [Fact]
    public async Task RerunAfterFailure_ExitCodeIsLastRun()
    {
        var runs = 0;
        List<string>? linesAtSecondStart = null;
        var session = NewSession((_, ui, _) =>
        {
            if (++runs == 1)
            {
                ui.ShowStatus(RunPhase.Verify, 1, "첫 실행 빌드 실패");
                return Task.FromResult(1);
            }
            linesAtSecondStart = _ui.Lines.Select(l => l.Text).ToList();
            return Task.FromResult(0);
        });

        await session.RunAsync().WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal(1, session.ExitCode);
        Assert.True(session.CanRun);
        await session.RunAsync().WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(0, session.ExitCode);
        Assert.NotNull(linesAtSecondStart);
        Assert.Empty(linesAtSecondStart);
        Assert.DoesNotContain(_ui.Lines, l => l.Text == "첫 실행 빌드 실패");
    }

    [Fact]
    public async Task EachRun_GetsFreshToken()
    {
        var runs = 0;
        bool? secondCancelled = null;
        var session = NewSession(async (_, _, ct) =>
        {
            if (++runs == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            }
            secondCancelled = ct.IsCancellationRequested;
            return 0;
        });

        var first = session.RunAsync();
        session.Cancel();
        await first.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal(2, session.ExitCode);

        await session.RunAsync().WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.False(secondCancelled);
        Assert.Equal(0, session.ExitCode);
    }

    [Fact]
    public async Task ShutdownToken_WhileAwaitingApproval_CancelsAndRollsBackPath()
    {
        var rolledBack = false;
        var session = NewSession(async (request, ui, ct) =>
        {
            try { return await ApproveScript(request, ui, ct); }
            catch (OperationCanceledException) { rolledBack = true; throw; }
        });

        var run = await StartAndWaitForQuestion(session);
        _shutdown.Cancel();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.True(rolledBack);
        Assert.Null(_ui.Question);
        Assert.Equal(SessionState.Finished, session.State);
        Assert.Equal(2, session.ExitCode);
    }

    [Fact]
    public void ShutdownToken_WhileIdle_RaisesCloseReady()
    {
        var session = NewSession(ApproveScript);
        var closeReady = 0;
        session.CloseReady += () => Interlocked.Increment(ref closeReady);

        _shutdown.Cancel();

        Assert.Equal(1, closeReady);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Equal(2, session.ExitCode);
    }

    [Fact]
    public void ShutdownToken_WhileInvalid_RaisesCloseReadyKeepsExit4()
    {
        var session = NewSession(ApproveScript, errors: ["파일이 없습니다"]);
        var closeReady = 0;
        session.CloseReady += () => Interlocked.Increment(ref closeReady);

        _shutdown.Cancel();

        Assert.Equal(1, closeReady);
        Assert.Equal(4, session.ExitCode);
    }

    [Fact]
    public async Task ShutdownToken_WhileFinished_RaisesCloseReadyKeepsLastExit()
    {
        var session = NewSession((_, _, _) => Task.FromResult(ExitCodes.VerifyFailed));
        var closeReady = 0;
        session.CloseReady += () => Interlocked.Increment(ref closeReady);
        await session.RunAsync().WaitAsync(Limit, TestContext.Current.CancellationToken);

        _shutdown.Cancel();

        Assert.Equal(1, closeReady);
        Assert.Equal(ExitCodes.VerifyFailed, session.ExitCode);
    }

    [Fact]
    public async Task ShutdownToken_WhileRunning_ClosesAfterRunEnds()
    {
        var rollbackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = NewSession(async (_, _, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                await rollbackGate.Task; // rollback in progress
                throw;
            }
            return 0;
        });
        var closeReady = 0;
        session.CloseReady += () => Interlocked.Increment(ref closeReady);

        var run = session.RunAsync();
        _shutdown.Cancel();
        await cancelled.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal(0, closeReady);

        rollbackGate.SetResult();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(1, closeReady);
        Assert.Equal(2, session.ExitCode);
        Assert.Equal(SessionState.Finished, session.State);
    }

    [Fact]
    public async Task RunThrows_ShowsErrorAndExit4()
    {
        var session = NewSession((_, _, _) => throw new InvalidOperationException("boom"));

        await session.RunAsync().WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Contains(_ui.Lines, l => l.Kind == StatusLineKind.Error && l.Text == "boom");
        Assert.Equal(4, session.ExitCode);
        Assert.Equal(SessionState.Finished, session.State);
        Assert.True(session.CanRun);
    }

    [Fact]
    public async Task BlankRequest_CannotRun()
    {
        var called = false;
        var session = NewSession((_, _, _) => { called = true; return Task.FromResult(0); });
        session.Request = "   ";

        Assert.False(session.CanRun);
        await Assert.ThrowsAsync<InvalidOperationException>(session.RunAsync);
        Assert.False(called);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public void ArgumentErrors_StartInvalid()
    {
        var session = NewSession(ApproveScript, errors: ["파일이 없습니다", "--yes는 ui에서 쓸 수 없습니다"]);

        Assert.Equal(SessionState.Invalid, session.State);
        Assert.Equal(
            [new StatusLine(null, 0, "파일이 없습니다", StatusLineKind.Error), new StatusLine(null, 0, "--yes는 ui에서 쓸 수 없습니다", StatusLineKind.Error)],
            _ui.Lines);
        Assert.Equal(4, session.ExitCode);
        Assert.False(session.CanRun);
        Assert.True(session.RequestClose());
        Assert.Equal(["파일이 없습니다", "--yes는 ui에서 쓸 수 없습니다"], session.ArgumentErrors);
    }

    [Fact]
    public async Task Finished_ShowsResultLine()
    {
        var session = NewSession((_, _, _) => Task.FromResult(0));

        await session.RunAsync().WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(new StatusLine(null, 0, "종료 코드 0: 성공 (패치 적용)", StatusLineKind.Result), _ui.Lines[^1]);
    }

    [Fact]
    public async Task AbortRun_WhileAwaitingApproval_CancelsAndWaitsForRollback()
    {
        var rolledBack = false;
        var session = NewSession(async (request, ui, ct) =>
        {
            try { return await ApproveScript(request, ui, ct); }
            catch (OperationCanceledException)
            {
                await Task.Delay(100, CancellationToken.None); // rollback takes a moment
                rolledBack = true;
                throw;
            }
        });
        await StartAndWaitForQuestion(session);

        Assert.True(session.AbortRun(Limit));

        Assert.True(rolledBack);
    }

    [Fact]
    public void AbortRun_WithoutRun_ReturnsTrue()
    {
        var session = NewSession(ApproveScript);

        Assert.True(session.AbortRun(Limit));
    }

    [Fact]
    public async Task AbortRun_RunIgnoresCancellation_ReturnsFalseAfterTimeout()
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = NewSession((_, _, _) =>
        {
            started.SetResult();
            return release.Task; // a rollback that does not finish in time
        });
        var run = session.RunAsync();
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.False(session.AbortRun(TimeSpan.FromMilliseconds(100)));

        release.SetResult(ExitCodes.UserCancelled);
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FinishHandlerThrows_RunAsyncStillCompletes()
    {
        var dispatcher = new QueuedDispatcher();
        var ui = new GuiUserInteraction(dispatcher);
        var session = new UiSession((_, _, _) => Task.FromResult(0), ui, [], _shutdown.Token) { Request = "요청" };
        session.Changed += () =>
        {
            if (session.State == SessionState.Finished) throw new InvalidOperationException("handler failed");
        };

        var run = session.RunAsync();
        var deadline = DateTime.UtcNow + Limit;
        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            try { dispatcher.Drain(); }
            catch (InvalidOperationException) { } // the UI thread's fault; the run must still complete
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(run.IsCompleted, "RunAsync did not complete");
        Assert.Equal(SessionState.Finished, session.State);
    }
}
