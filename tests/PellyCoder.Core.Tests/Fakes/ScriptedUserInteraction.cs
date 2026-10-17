using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;

namespace PellyCoder.Core.Tests.Fakes;

/// <summary>Fake <see cref="IUserInteraction"/> that answers from scripted values and records everything shown.</summary>
public sealed class ScriptedUserInteraction : IUserInteraction
{
    public bool IsInteractive { get; set; }

    /// <summary>Answers for <see cref="ConfirmAsync"/>, in order. An unexpected confirm fails the test.</summary>
    public Queue<bool> ConfirmAnswers { get; } = new();

    /// <summary>Picks a choice index from the labels. Null means a selection is unexpected.</summary>
    public Func<IReadOnlyList<string>, int>? SelectIndex { get; set; }

    /// <summary>Runs on every status update (e.g. to simulate an external edit between phases).</summary>
    public Action<RunPhase, int>? OnStatus { get; set; }

    public List<string> ConfirmMessages { get; } = [];
    public List<IReadOnlyList<string>> SelectLabels { get; } = [];
    public List<(string Path, string Before, string After)> Diffs { get; } = [];
    public List<(RunPhase Phase, int Attempt, string Message)> Statuses { get; } = [];
    public List<string> Warnings { get; } = [];

    public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ConfirmMessages.Add(message);
        if (ConfirmAnswers.Count == 0)
            throw new InvalidOperationException($"ScriptedUserInteraction: unexpected confirm '{message}'.");
        return Task.FromResult(ConfirmAnswers.Dequeue());
    }

    public Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var labels = choices.Select(label).ToList();
        SelectLabels.Add(labels);
        if (SelectIndex is null)
            throw new InvalidOperationException($"ScriptedUserInteraction: unexpected selection '{title}'.");
        return Task.FromResult(choices[SelectIndex(labels)]);
    }

    public void ShowDiff(string path, string before, string after) => Diffs.Add((path, before, after));

    public void ShowStatus(RunPhase phase, int attempt, string message)
    {
        Statuses.Add((phase, attempt, message));
        OnStatus?.Invoke(phase, attempt);
    }

    public void Warn(string message) => Warnings.Add(message);
}
