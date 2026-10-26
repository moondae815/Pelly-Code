using PellyCoder.Core.Fsm;

namespace PellyCoder.Core.Abstractions;

/// <summary>All progress output and user input of a run (design §5, D20). The CLI implements it with Spectre.</summary>
public interface IUserInteraction
{
    /// <summary>True when the user can answer prompts (console input not redirected).</summary>
    bool IsInteractive { get; }

    Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct);

    /// <summary>The final "apply this change?" question. Distinct from warning confirmations so a GUI can show the approval controls; defaults to a <see cref="ConfirmAsync"/> with default No.</summary>
    Task<bool> ApproveAsync(string message, CancellationToken ct) => ConfirmAsync(message, false, ct);

    Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct);

    void ShowDiff(string path, string before, string after);

    /// <param name="attempt">EDIT attempt number the phase belongs to (0 before the first edit).</param>
    void ShowStatus(RunPhase phase, int attempt, string message);

    /// <summary>Server-wait update that replaces the previous waiting line instead of adding a new one; defaults to <see cref="ShowStatus"/>.</summary>
    void ShowWaiting(RunPhase phase, int attempt, string message) => ShowStatus(phase, attempt, message);

    void Warn(string message);

    /// <summary>Shown after the run summary is written, only with <c>--verbose</c>. <paramref name="runDirectory"/> is null when no workspace was resolved.</summary>
    void ShowRunSummary(PellyCoder.Core.Logging.RunSummary summary, string? runDirectory);
}
