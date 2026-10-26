using PellyCoder.Core.Fsm;

namespace PellyCoder.Ui.Session;

/// <summary>
/// The window's state machine, separate from the view (design §5.2–5.3):
/// Idle → Running ⇄ AwaitingAnswer → (Cancelling) → Finished → Running … , or Invalid when command validation failed.
/// Public members are called on the UI thread; the run itself executes on the thread pool and posts its results back
/// through the interaction's <see cref="IUiDispatcher"/>. Rollback stays with the FSM: the session only cancels the token.
/// </summary>
public sealed class UiSession
{
    private readonly RunFunc _run;
    private readonly GuiUserInteraction _ui;
    private readonly CancellationToken _shutdown;
    private CancellationTokenSource? _cts;
    private bool _closeRequested;
    private string _request = "";

    public UiSession(RunFunc run, GuiUserInteraction ui, IReadOnlyList<string> argumentErrors, CancellationToken shutdown)
    {
        _run = run;
        _ui = ui;
        _shutdown = shutdown;
        ArgumentErrors = argumentErrors;
        _ui.Changed += OnInteractionChanged;
        if (argumentErrors.Count > 0)
        {
            State = SessionState.Invalid;
            ExitCode = ExitCodes.EnvError;
            foreach (var error in argumentErrors)
                _ui.AddLine(new StatusLine(null, 0, error, StatusLineKind.Error));
        }
    }

    public SessionState State { get; private set; } = SessionState.Idle;

    public string Request
    {
        get => _request;
        set
        {
            _request = value ?? "";
            Changed?.Invoke();
        }
    }

    public bool CanRun => State is SessionState.Idle or SessionState.Finished && !string.IsNullOrWhiteSpace(_request);

    /// <summary>Exit code of the last finished run; 2 before any run, 4 when <see cref="State"/> is Invalid.</summary>
    public int ExitCode { get; private set; } = ExitCodes.UserCancelled;

    public IReadOnlyList<string> ArgumentErrors { get; }

    public event Action? Changed;

    /// <summary>Raised once, after a run ends, when a close was requested while it was running.</summary>
    public event Action? CloseReady;

    /// <summary>Runs the request with a fresh token linked to the shutdown token; completes after the session is Finished.</summary>
    public async Task RunAsync()
    {
        if (!CanRun) throw new InvalidOperationException("지금은 실행할 수 없습니다.");

        var request = _request;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        _cts = cts;
        _ui.ClearForNewRun();
        SetState(SessionState.Running);

        int code;
        try
        {
            code = await Task.Run(() => _run(request, _ui, cts.Token), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            code = ExitCodes.UserCancelled;
        }
        catch (Exception ex)
        {
            _ui.AddLine(new StatusLine(null, 0, ex.Message, StatusLineKind.Error));
            code = ExitCodes.EnvError;
        }
        _ui.AddLine(new StatusLine(null, 0, $"종료 코드 {code}: {ExitCodeText.Describe(code)}", StatusLineKind.Result));

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Dispatcher.Post(() =>
        {
            ExitCode = code;
            _cts = null;
            cts.Dispose();
            SetState(SessionState.Finished);
            if (_closeRequested)
            {
                _closeRequested = false;
                CloseReady?.Invoke();
            }
            finished.SetResult();
        });
        await finished.Task.ConfigureAwait(false);
    }

    /// <summary>Running/AwaitingAnswer → Cancelling. The FSM rolls back and the run ends with 2.</summary>
    public void Cancel()
    {
        if (State is not (SessionState.Running or SessionState.AwaitingAnswer)) return;
        SetState(SessionState.Cancelling);
        _cts?.Cancel();
    }

    /// <summary>
    /// True when the window may close now. While a run is active it cancels the run (a pending question is cancelled,
    /// not answered "no") and returns false; <see cref="CloseReady"/> follows when the rollback has finished.
    /// </summary>
    public bool RequestClose()
    {
        switch (State)
        {
            case SessionState.Running or SessionState.AwaitingAnswer:
                _closeRequested = true;
                Cancel();
                return false;
            case SessionState.Cancelling:
                _closeRequested = true;
                return false;
            default:
                return true;
        }
    }

    private void OnInteractionChanged()
    {
        if (State == SessionState.Running && _ui.Question is not null) SetState(SessionState.AwaitingAnswer);
        else if (State == SessionState.AwaitingAnswer && _ui.Question is null) SetState(SessionState.Running);
    }

    private void SetState(SessionState state)
    {
        if (State == state) return;
        State = state;
        Changed?.Invoke();
    }
}
