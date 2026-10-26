using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Ui.Session;
using PellyCoder.Ui.Views;

namespace PellyCoder.Ui.Tests.Views;

public sealed class MainWindowTests(HeadlessSession headless)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private const string RequestText = "배송 중인 주문은 취소할 수 없게 해줘";

    private static (MainWindow Window, UiSession Session) Open(
        RunFunc run, bool dryRun = false, string? initialRequest = null, IReadOnlyList<string>? errors = null)
    {
        var ui = new GuiUserInteraction(new AvaloniaDispatcher());
        var session = new UiSession(run, ui, errors ?? [], CancellationToken.None);
        var launch = new UiLaunch("Pelly — OrderService.cs", "OrderService.cs 120번 줄", initialRequest, dryRun);
        var window = new MainWindow(new MainWindowModel(launch, session, ui));
        window.Show();
        Pump(window);
        return (window, session);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static async Task WaitUntil(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            Pump(window);
            if (condition()) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"control {name} not found");

    private static bool Visible(Window window, string name) => Find<Control>(window, name).IsEffectivelyVisible;

    /// <summary>Clicks the button with the mouse, so a hidden or disabled button does not react.</summary>
    private static void Click(Window window, string name)
    {
        var button = Find<Button>(window, name);
        Assert.True(button.IsEffectivelyVisible, $"{name} is not visible");
        Assert.True(button.IsEffectivelyEnabled, $"{name} is not enabled");
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
                     ?? throw new InvalidOperationException($"{name} is not in the window");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump(window);
    }

    private static IReadOnlyList<string?> Texts(Window window, string container) =>
        Find<Control>(window, container).GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

    [Fact]
    public Task TypeRequest_Run_Approve_ExitZero() => headless.Run(async () =>
    {
        string? received = null;
        var (window, session) = Open(async (request, ui, ct) =>
        {
            received = request;
            ui.ShowDiff("OrderService.cs", "a\n", "b\n");
            return await ui.ApproveAsync("적용할까요?", ct) ? ExitCodes.Success : ExitCodes.UserCancelled;
        });

        var box = Find<TextBox>(window, "RequestBox");
        box.Focus();
        window.KeyTextInput(RequestText);
        Pump(window);
        Assert.Equal(RequestText, box.Text);
        Assert.Equal(RequestText, session.Request);

        Click(window, "RunButton");
        await WaitUntil(window, () => Visible(window, "ApplyButton"));
        Assert.False(Find<Button>(window, "ApplyButton").IsDefault);

        Click(window, "ApplyButton");
        await WaitUntil(window, () => session.State == SessionState.Finished);
        Assert.Equal(0, session.ExitCode);
        Assert.Equal(RequestText, received);
    });

    [Fact]
    public Task ApproveQuestion_ShowsApplyReject_ConfirmShowsContinueStop() => headless.Run(async () =>
    {
        var (window, session) = Open(async (_, ui, ct) =>
        {
            if (!await ui.ConfirmAsync("경고가 있습니다. 그래도 계속할까요?", false, ct)) return ExitCodes.UserCancelled;
            return await ui.ApproveAsync("적용할까요?", ct) ? ExitCodes.Success : ExitCodes.UserCancelled;
        }, initialRequest: RequestText);

        Click(window, "RunButton");
        await WaitUntil(window, () => Visible(window, "ContinueButton"));
        Assert.True(Visible(window, "StopButton"));
        Assert.False(Visible(window, "ApplyButton"));
        Assert.False(Visible(window, "RejectButton"));
        Assert.Contains("경고가 있습니다. 그래도 계속할까요?", Texts(window, "QuestionPanel"));

        Click(window, "ContinueButton");
        await WaitUntil(window, () => Visible(window, "ApplyButton"));
        Assert.True(Visible(window, "RejectButton"));
        Assert.False(Visible(window, "ContinueButton"));
        Assert.False(Visible(window, "StopButton"));

        Click(window, "RejectButton");
        await WaitUntil(window, () => session.State == SessionState.Finished);
        Assert.Equal(ExitCodes.UserCancelled, session.ExitCode);
        Assert.False(Visible(window, "ApplyButton"));
    });

    [Fact]
    public Task Diff_ShowsMarkupLikeTextVerbatim() => headless.Run(async () =>
    {
        var (window, session) = Open((_, ui, _) =>
        {
            ui.ShowDiff("[bold]A.cs[/]", "a", "[red]x[/] <Bold>y</Bold>");
            return Task.FromResult(ExitCodes.Success);
        }, dryRun: true, initialRequest: RequestText);

        Click(window, "RunButton");
        await WaitUntil(window, () => session.State == SessionState.Finished && Visible(window, "DiffList"));

        var lines = Texts(window, "DiffList");
        Assert.Contains("+[red]x[/] <Bold>y</Bold>", lines);
        Assert.Contains("-a", lines);
        Assert.Equal("[bold]A.cs[/]", Find<TextBlock>(window, "DiffPathText").Text);
    });

    [Fact]
    public Task DryRun_ShowsOnlyOk() => headless.Run(async () =>
    {
        var closed = false;
        var (window, session) = Open((_, ui, _) =>
        {
            ui.ShowDiff("A.cs", "a", "b");
            return Task.FromResult(ExitCodes.Success);
        }, dryRun: true, initialRequest: RequestText);
        window.Closed += (_, _) => closed = true;

        Click(window, "RunButton");
        await WaitUntil(window, () => session.State == SessionState.Finished && Visible(window, "OkButton"));
        Assert.False(Visible(window, "ApplyButton"));
        Assert.False(Visible(window, "RejectButton"));
        Assert.False(Visible(window, "ContinueButton"));
        Assert.False(Visible(window, "StopButton"));

        Click(window, "OkButton");
        Assert.True(closed);
        Assert.Equal(0, session.ExitCode);
    });

    [Fact]
    public Task InvalidArguments_ShowErrorsAndDisableRun() => headless.Run(() =>
    {
        var ran = false;
        var (window, session) = Open((_, _, _) =>
        {
            ran = true;
            return Task.FromResult(ExitCodes.Success);
        }, initialRequest: RequestText, errors: ["--yes는 ui에서 쓸 수 없습니다."]);

        Assert.Contains("--yes는 ui에서 쓸 수 없습니다.", Texts(window, "LinesList"));
        Assert.False(Find<Button>(window, "RunButton").IsEffectivelyEnabled);
        Assert.Equal(SessionState.Invalid, session.State);
        Assert.Equal(ExitCodes.EnvError, session.ExitCode);
        Assert.False(ran);
    });

    [Fact]
    public Task Closing_WhileRunning_IsCancelledUntilRunEnds() => headless.Run(async () =>
    {
        var rollback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
        var (window, session) = Open(async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await rollback.Task; // the FSM rolls back before the run ends
                throw;
            }
            return ExitCodes.Success;
        }, initialRequest: RequestText);
        window.Closed += (_, _) => closed = true;

        Click(window, "RunButton");
        await WaitUntil(window, () => session.State == SessionState.Running);

        window.Close();
        Pump(window);
        Assert.False(closed);
        Assert.Equal(SessionState.Cancelling, session.State);
        Assert.True(Visible(window, "CancellingText"));
        Assert.False(Find<Button>(window, "CloseButton").IsEffectivelyEnabled);

        window.Close(); // a second close while rolling back is ignored too
        Pump(window);
        Assert.False(closed);

        rollback.SetResult();
        await WaitUntil(window, () => closed);
        Assert.Equal(ExitCodes.UserCancelled, session.ExitCode);
    });
}
