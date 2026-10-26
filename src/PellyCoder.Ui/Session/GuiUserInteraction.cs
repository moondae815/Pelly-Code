using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Ui.Session;

/// <summary>
/// GUI implementation of <see cref="IUserInteraction"/> (design §5.1). Callable from any thread: every change of the
/// observable state and every <see cref="Changed"/> happens inside <see cref="IUiDispatcher.Post"/>. Questions wait on a
/// <see cref="TaskCompletionSource{TResult}"/> until <see cref="Answer"/> or cancellation; only one may be pending.
/// </summary>
public sealed class GuiUserInteraction(IUiDispatcher dispatcher) : IUserInteraction
{
    private readonly Lock _gate = new();
    private Pending? _pending;

    public bool IsInteractive => true;

    /// <summary>Progress lines; changed on the UI thread only.</summary>
    public ObservableCollection<StatusLine> Lines { get; } = [];

    /// <summary>Lines of the last <see cref="ShowDiff"/> (Core <see cref="DiffRenderer.UnifiedLines"/>).</summary>
    public IReadOnlyList<(char Kind, string Text)>? DiffLines { get; private set; }

    public string? DiffPath { get; private set; }

    public PendingQuestion? Question { get; private set; }

    /// <summary>The run summary as plain multi-line text (shown with <c>--verbose</c>).</summary>
    public string? RunSummaryText { get; private set; }

    /// <summary>Raised on the UI thread after any state change.</summary>
    public event Action? Changed;

    internal IUiDispatcher Dispatcher => dispatcher;

    public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct) =>
        AskBoolAsync(QuestionKind.Confirm, message, ct);

    public Task<bool> ApproveAsync(string message, CancellationToken ct) =>
        AskBoolAsync(QuestionKind.Approve, message, ct);

    public async Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct)
    {
        var labels = choices.Select(label).ToArray();
        var index = await AskAsync(new PendingQuestion(QuestionKind.Select, title, labels), ct).ConfigureAwait(false);
        return choices[index];
    }

    public void ShowDiff(string path, string before, string after)
    {
        var lines = DiffRenderer.UnifiedLines(before, after);
        Mutate(() =>
        {
            DiffPath = path;
            DiffLines = lines;
        });
    }

    public void ShowStatus(RunPhase phase, int attempt, string message) =>
        AddLine(new StatusLine(phase, attempt, message, StatusLineKind.Status));

    public void ShowWaiting(RunPhase phase, int attempt, string message)
    {
        var line = new StatusLine(phase, attempt, message, StatusLineKind.Waiting);
        Mutate(() =>
        {
            if (Lines.Count > 0 && Lines[^1].Kind == StatusLineKind.Waiting) Lines[^1] = line;
            else Lines.Add(line);
        });
    }

    public void Warn(string message) => AddLine(new StatusLine(null, 0, message, StatusLineKind.Warning));

    public void ShowRunSummary(RunSummary summary, string? runDirectory)
    {
        var text = FormatSummary(summary, runDirectory);
        Mutate(() => RunSummaryText = text);
    }

    /// <summary>Answers the pending question. Approve/Confirm: 1 = yes, 0 = no. Select: the chosen index. Ignored when nothing is pending or the pending question is not shown yet.</summary>
    public void Answer(int choice)
    {
        Pending? pending;
        lock (_gate)
        {
            pending = _pending;
            // Called on the UI thread: only answer the question that is on screen. Under an asynchronous dispatcher the
            // run may already wait on the next question while the previous one is still shown (double click).
            if (pending is null || !ReferenceEquals(Question, pending.Question)) return;
            if (pending.Question.Kind == QuestionKind.Select && (choice < 0 || choice >= pending.Question.Choices.Count))
                throw new ArgumentOutOfRangeException(nameof(choice), choice, "선택지 범위를 벗어났습니다.");
            _pending = null;
        }
        ClearQuestion(pending.Question);
        pending.Completion.TrySetResult(choice);
    }

    /// <summary>Clears lines, diff, question and summary before a new run.</summary>
    public void ClearForNewRun() => Mutate(() =>
    {
        Lines.Clear();
        DiffLines = null;
        DiffPath = null;
        Question = null;
        RunSummaryText = null;
    });

    internal void AddLine(StatusLine line) => Mutate(() => Lines.Add(line));

    private async Task<bool> AskBoolAsync(QuestionKind kind, string message, CancellationToken ct) =>
        await AskAsync(new PendingQuestion(kind, message, []), ct).ConfigureAwait(false) != 0;

    private async Task<int> AskAsync(PendingQuestion question, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pending = new Pending(question, new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            if (_pending is not null)
                throw new InvalidOperationException("이미 답을 기다리는 질문이 있습니다.");
            _pending = pending;
        }
        Mutate(() => Question = question);

        await using var registration = ct.Register(() =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_pending, pending)) return;
                _pending = null;
            }
            try
            {
                ClearQuestion(question);
            }
            finally
            {
                // The run (and its rollback) must not depend on the UI thread, which may be gone (UiHost.RunLoop).
                pending.Completion.TrySetCanceled(ct);
            }
        });
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private void ClearQuestion(PendingQuestion question) => dispatcher.Post(() =>
    {
        // Under an asynchronous dispatcher a newer question may already be shown; only clear this one.
        if (!ReferenceEquals(Question, question)) return;
        Question = null;
        Changed?.Invoke();
    });

    private void Mutate(Action change) => dispatcher.Post(() =>
    {
        change();
        Changed?.Invoke();
    });

    private static string FormatSummary(RunSummary summary, string? runDirectory)
    {
        var inv = CultureInfo.InvariantCulture;
        string Opt(int? v) => v is { } n ? n.ToString(inv) : "-";
        var sb = new StringBuilder();
        sb.AppendLine("실행 요약");
        foreach (var (phase, seconds) in summary.PhaseSeconds)
            sb.AppendLine($"  {phase}: {seconds.ToString("0.##", inv)}초");
        sb.AppendLine($"시도: {summary.Attempts.ToString(inv)}");
        sb.AppendLine($"TTFT: {summary.TtftSeconds.ToString("0.##", inv)}초, 대기: {summary.QueueSeconds.ToString("0.##", inv)}초");
        sb.AppendLine($"입력 토큰: local {summary.InputTokensLocal.ToString(inv)}, server {Opt(summary.InputTokensServer)}, cached {Opt(summary.InputTokensCached)}");
        sb.AppendLine($"think 토큰: {summary.ThinkTokens.ToString(inv)}{(summary.ThinkTruncated ? " (잘림)" : "")}");
        sb.AppendLine($"매칭 단계: {(summary.MatchStages.Count == 0 ? "-" : string.Join(", ", summary.MatchStages))}");
        sb.AppendLine($"오류 코드: {(summary.ErrorCodes.Count == 0 ? "-" : string.Join(", ", summary.ErrorCodes))}");
        var included = summary.RulesIncluded ?? [];
        var dropped = summary.RulesDropped ?? [];
        if (included.Count > 0 || dropped.Count > 0)
        {
            sb.AppendLine($"규칙: 포함 {included.Count.ToString(inv)}, 제외 {dropped.Count.ToString(inv)}");
            foreach (var text in included) sb.AppendLine($"  + {text}");
            foreach (var text in dropped) sb.AppendLine($"  - {text}");
        }
        if (runDirectory is not null)
            sb.AppendLine($"프롬프트·응답 원문: {runDirectory}");
        return sb.ToString().TrimEnd();
    }

    private sealed class Pending(PendingQuestion question, TaskCompletionSource<int> completion)
    {
        public PendingQuestion Question => question;
        public TaskCompletionSource<int> Completion => completion;
    }
}
