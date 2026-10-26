using PellyCoder.Core.Fsm;
using PellyCoder.Core.Reporting;
using PellyCoder.Ui.Session;

namespace PellyCoder.Ui.Tests.Session;

public sealed class GuiUserInteractionTests
{
    private readonly GuiUserInteraction _ui = new(new InlineDispatcher());

    [Fact]
    public void ShowWaiting_ReplacesConsecutiveWaitingLine()
    {
        _ui.ShowStatus(RunPhase.Edit, 1, "EDIT 시작");
        _ui.ShowWaiting(RunPhase.Edit, 1, "서버 응답 대기 중 (1초)");
        _ui.ShowWaiting(RunPhase.Edit, 1, "서버 응답 대기 중 (2초)");

        Assert.Equal(2, _ui.Lines.Count);
        Assert.Equal(new StatusLine(RunPhase.Edit, 1, "서버 응답 대기 중 (2초)", StatusLineKind.Waiting), _ui.Lines[^1]);
        Assert.Equal(StatusLineKind.Status, _ui.Lines[0].Kind);
    }

    [Fact]
    public void ShowWaiting_AfterStatus_AddsNewLine()
    {
        _ui.ShowWaiting(RunPhase.Edit, 1, "대기 1");
        _ui.ShowStatus(RunPhase.Edit, 1, "상태");
        _ui.ShowWaiting(RunPhase.Edit, 1, "대기 2");

        Assert.Equal(
            [StatusLineKind.Waiting, StatusLineKind.Status, StatusLineKind.Waiting],
            _ui.Lines.Select(l => l.Kind).ToArray());
    }

    [Fact]
    public async Task Confirm_And_Approve_AreDistinguished()
    {
        var confirm = _ui.ConfirmAsync("그래도 계속할까요?", false, TestContext.Current.CancellationToken);
        Assert.Equal(QuestionKind.Confirm, _ui.Question!.Kind);
        Assert.Equal("그래도 계속할까요?", _ui.Question.Message);
        Assert.Empty(_ui.Question.Choices);
        _ui.Answer(1);
        Assert.True(await confirm);
        Assert.Null(_ui.Question);

        var approve = _ui.ApproveAsync("적용할까요?", TestContext.Current.CancellationToken);
        Assert.Equal(QuestionKind.Approve, _ui.Question!.Kind);
        Assert.Equal("적용할까요?", _ui.Question.Message);
        _ui.Answer(0);
        Assert.False(await approve);
        Assert.Null(_ui.Question);
    }

    [Fact]
    public async Task Select_ReturnsChosenItem()
    {
        var select = _ui.SelectAsync("고르세요", ["a", "b"], s => s.ToUpperInvariant(), TestContext.Current.CancellationToken);

        Assert.Equal(QuestionKind.Select, _ui.Question!.Kind);
        Assert.Equal("고르세요", _ui.Question.Message);
        Assert.Equal(["A", "B"], _ui.Question.Choices);
        _ui.Answer(1);
        Assert.Equal("b", await select);
        Assert.Null(_ui.Question);
    }

    [Fact]
    public async Task Question_CancelledByToken()
    {
        using var cts = new CancellationTokenSource();
        var confirm = _ui.ConfirmAsync("계속?", true, cts.Token);
        Assert.NotNull(_ui.Question);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirm);
        Assert.Null(_ui.Question);
    }

    [Fact]
    public async Task SecondQuestionWhilePending_Throws()
    {
        var first = _ui.ConfirmAsync("첫 질문", false, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _ui.ApproveAsync("둘째 질문", TestContext.Current.CancellationToken));

        Assert.Equal("첫 질문", _ui.Question!.Message);
        _ui.Answer(1);
        Assert.True(await first);
    }

    [Fact]
    public void ShowDiff_StoresUnifiedLines()
    {
        const string before = "a\nb\nc\n";
        const string after = "a\nB\nc\n[red]x[/]\n";

        _ui.ShowDiff("src/A.cs", before, after);

        Assert.Equal("src/A.cs", _ui.DiffPath);
        Assert.Equal(DiffRenderer.UnifiedLines(before, after), _ui.DiffLines);
    }

    [Fact]
    public async Task Answer_IgnoredUntilNextQuestionIsShown()
    {
        var dispatcher = new QueuedDispatcher();
        var ui = new GuiUserInteraction(dispatcher);
        var ct = TestContext.Current.CancellationToken;
        var script = Task.Run(async () =>
            await ui.ConfirmAsync("그래도 계속할까요?", false, ct) && await ui.ApproveAsync("적용할까요?", ct), ct);

        while (dispatcher.Count < 1) await Task.Delay(10, ct);
        dispatcher.Drain();
        Assert.Equal(QuestionKind.Confirm, ui.Question!.Kind);

        ui.Answer(1);                                    // clear of q1 queued, script asks q2
        while (dispatcher.Count < 2) await Task.Delay(10, ct);
        ui.Answer(1);                                    // double click on the still-shown q1 buttons

        dispatcher.Drain();
        Assert.Equal(QuestionKind.Approve, ui.Question!.Kind);
        Assert.False(script.IsCompleted);

        ui.Answer(0);
        dispatcher.Drain();
        Assert.False(await script.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Null(ui.Question);
    }
}
