using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using PellyCoder.Ui.Session;

namespace PellyCoder.Ui.Views;

/// <summary>One unified-diff line as shown: the text as in the CLI (kind prefix, hunk header as is) and its colours.</summary>
public sealed record DiffLineItem(string Text, IBrush Background, IBrush Foreground);

/// <summary>
/// View model of <see cref="MainWindow"/>: a snapshot of <see cref="UiSession"/> and <see cref="GuiUserInteraction"/>
/// refreshed on their <c>Changed</c> events (UI thread), plus the window's commands. Plain <see cref="INotifyPropertyChanged"/>.
/// </summary>
public sealed class MainWindowModel : INotifyPropertyChanged
{
    private static readonly IBrush Plain = Brushes.Black;
    private static readonly IBrush NoBackground = Brushes.Transparent;
    private static readonly IBrush AddedBackground = new ImmutableSolidColorBrush(Color.FromRgb(0xD8, 0xF5, 0xDD));
    private static readonly IBrush RemovedBackground = new ImmutableSolidColorBrush(Color.FromRgb(0xFB, 0xDD, 0xDD));
    private static readonly IBrush HunkForeground = new ImmutableSolidColorBrush(Color.FromRgb(0x00, 0x7C, 0x89));
    private static readonly IBrush WarningForeground = new ImmutableSolidColorBrush(Color.FromRgb(0x9A, 0x67, 0x00));
    private static readonly IBrush ErrorForeground = new ImmutableSolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));

    private readonly UiLaunch _launch;
    private readonly UiSession _session;
    private readonly GuiUserInteraction _ui;
    private IReadOnlyList<(char Kind, string Text)>? _shownDiff;
    private PendingQuestion? _shownQuestion;

    public MainWindowModel(UiLaunch launch, UiSession session, GuiUserInteraction ui)
    {
        _launch = launch;
        _session = session;
        _ui = ui;
        if (launch.InitialRequest is { } request && session.State != SessionState.Invalid)
            session.Request = request;
        session.Changed += Refresh;
        ui.Changed += Refresh;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => _launch.Title;

    public string TargetLabel => $"대상: {_launch.TargetLabel}";

    public UiSession Session => _session;

    public ObservableCollection<StatusLine> Lines => _ui.Lines;

    public string Request
    {
        get => _session.Request;
        set
        {
            if (_session.Request == value) return;
            _session.Request = value;
            OnPropertyChanged();
        }
    }

    public bool IsRequestEditable { get; private set => Set(ref field, value); }
    public bool CanRun { get; private set => Set(ref field, value); }
    public bool CanCancel { get; private set => Set(ref field, value); }
    public bool CanClose { get; private set => Set(ref field, value); }
    public bool IsCancelling { get; private set => Set(ref field, value); }

    public IReadOnlyList<DiffLineItem> DiffLines { get; private set => Set(ref field, value); } = [];
    public bool HasDiff { get; private set => Set(ref field, value); }
    public string? DiffPath { get; private set => Set(ref field, value); }

    public bool HasQuestion { get; private set => Set(ref field, value); }
    public string? QuestionMessage { get; private set => Set(ref field, value); }
    public bool IsApproveQuestion { get; private set => Set(ref field, value); }
    public bool IsConfirmQuestion { get; private set => Set(ref field, value); }
    public bool IsSelectQuestion { get; private set => Set(ref field, value); }
    public IReadOnlyList<string> SelectChoices { get; private set => Set(ref field, value); } = [];

    public int SelectedChoice
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            CanSelect = IsSelectQuestion && value >= 0;
        }
    } = -1;

    public bool CanSelect { get; private set => Set(ref field, value); }

    /// <summary>Dry run: the diff is shown without a question, only 「확인」 (which closes the window).</summary>
    public bool ShowOk { get; private set => Set(ref field, value); }

    public string? RunSummaryText { get; private set => Set(ref field, value); }
    public bool HasRunSummary { get; private set => Set(ref field, value); }

    /// <summary>Progress line text, formatted like the CLI (<c>PHASE (시도 n) message</c>).</summary>
    public static FuncValueConverter<StatusLine?, string?> LineText { get; } = new(line => line switch
    {
        null => null,
        { Phase: null } => line.Text,
        { Attempt: 0 } => $"{line.Phase} {line.Text}",
        _ => $"{line.Phase} (시도 {line.Attempt}) {line.Text}",
    });

    public static FuncValueConverter<StatusLine?, IBrush?> LineBrush { get; } = new(line => line?.Kind switch
    {
        StatusLineKind.Warning => WarningForeground,
        StatusLineKind.Error => ErrorForeground,
        _ => Plain,
    });

    public static FuncValueConverter<StatusLine?, FontWeight> LineWeight { get; } =
        new(line => line?.Kind == StatusLineKind.Result ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>Starts a run. The session completes it on its own; errors of the run are shown as lines by the session.</summary>
    public void Run()
    {
        if (_session.CanRun) _ = _session.RunAsync();
    }

    public void Cancel() => _session.Cancel();

    /// <summary>Approve/Confirm: true = 「적용」/「계속」, false = 「거부」/「중단」.</summary>
    public void Answer(bool yes)
    {
        if (IsApproveQuestion || IsConfirmQuestion) _ui.Answer(yes ? 1 : 0);
    }

    public void Select()
    {
        if (CanSelect) _ui.Answer(SelectedChoice);
    }

    /// <summary>Esc: rejects or stops a pending yes/no question, cancels the run on a selection. False when nothing was pending (then Esc closes).</summary>
    public bool Escape()
    {
        if (IsApproveQuestion || IsConfirmQuestion)
        {
            Answer(false);
            return true;
        }
        if (IsSelectQuestion)
        {
            Cancel();
            return true;
        }
        return false;
    }

    private void Refresh()
    {
        var state = _session.State;
        IsRequestEditable = state is SessionState.Idle or SessionState.Finished;
        CanRun = _session.CanRun;
        CanCancel = state is SessionState.Running or SessionState.AwaitingAnswer;
        IsCancelling = state == SessionState.Cancelling;
        CanClose = !IsCancelling;

        if (!ReferenceEquals(_shownDiff, _ui.DiffLines))
        {
            _shownDiff = _ui.DiffLines;
            DiffLines = _shownDiff?.Select(ToItem).ToArray() ?? [];
        }
        HasDiff = _shownDiff is not null;
        DiffPath = _ui.DiffPath;

        var question = _ui.Question;
        HasQuestion = question is not null;
        QuestionMessage = question?.Message;
        IsApproveQuestion = question?.Kind == QuestionKind.Approve;
        IsConfirmQuestion = question?.Kind == QuestionKind.Confirm;
        var isSelect = question?.Kind == QuestionKind.Select;
        if (!ReferenceEquals(_shownQuestion, question))
        {
            _shownQuestion = question;
            SelectChoices = isSelect ? question!.Choices : [];
            SelectedChoice = -1;
        }
        IsSelectQuestion = isSelect;
        CanSelect = isSelect && SelectedChoice >= 0;

        ShowOk = _launch.DryRun && HasDiff && question is null && state == SessionState.Finished;
        RunSummaryText = _ui.RunSummaryText;
        HasRunSummary = RunSummaryText is not null;
    }

    private static DiffLineItem ToItem((char Kind, string Text) line) => line.Kind switch
    {
        '+' => new DiffLineItem("+" + line.Text, AddedBackground, Plain),
        '-' => new DiffLineItem("-" + line.Text, RemovedBackground, Plain),
        '@' => new DiffLineItem(line.Text, NoBackground, HunkForeground),
        _ => new DiffLineItem(line.Kind + line.Text, NoBackground, Plain),
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
