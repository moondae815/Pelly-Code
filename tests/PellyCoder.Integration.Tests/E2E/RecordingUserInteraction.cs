using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>Non-interactive <see cref="IUserInteraction"/> that approves everything and records what the run reported.</summary>
public sealed class RecordingUserInteraction : IUserInteraction
{
    public List<string> Lines { get; } = [];

    public bool IsInteractive => false;

    public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct)
    {
        Lines.Add($"confirm: {message}");
        return Task.FromResult(true);
    }

    public Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct)
    {
        Lines.Add($"select: {title}");
        return Task.FromResult(choices[0]);
    }

    public void ShowDiff(string path, string before, string after) => Lines.Add($"diff: {path}");

    public void ShowStatus(RunPhase phase, int attempt, string message) => Lines.Add($"{phase}#{attempt}: {message}");

    public void Warn(string message) => Lines.Add($"warn: {message}");
}
