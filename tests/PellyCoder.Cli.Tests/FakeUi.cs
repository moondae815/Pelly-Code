using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;

namespace PellyCoder.Cli.Tests;

/// <summary>Minimal <see cref="IUserInteraction"/> for command tests: only <see cref="IsInteractive"/> matters.</summary>
internal sealed class FakeUi(bool interactive) : IUserInteraction
{
    public bool IsInteractive => interactive;
    public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct) => Task.FromResult(defaultValue);
    public Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct) => Task.FromResult(choices[0]);
    public void ShowDiff(string path, string before, string after) { }
    public void ShowStatus(RunPhase phase, int attempt, string message) { }
    public void Warn(string message) { }
}
