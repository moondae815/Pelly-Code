using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;

namespace PellyCoder.Eval;

/// <summary>
/// Non-interactive UI of a benchmark run: progress goes to <paramref name="log"/> prefixed with the task id. Runs use
/// <c>--yes</c> and name their target, so the orchestrator never asks; a question is a runner bug and throws.
/// </summary>
public sealed class EvalUserInteraction(string taskId, Action<string> log) : IUserInteraction
{
    public bool IsInteractive => false;

    public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct) =>
        throw new InvalidOperationException($"{taskId}: 벤치마크 실행 중 확인 질문이 나왔습니다: {message}");

    public Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct) =>
        throw new InvalidOperationException($"{taskId}: 벤치마크 실행 중 선택 질문이 나왔습니다: {title}");

    public void ShowDiff(string path, string before, string after) => log($"[{taskId}] diff: {Path.GetFileName(path)}");

    public void ShowStatus(RunPhase phase, int attempt, string message) => log($"[{taskId}] {phase}#{attempt}: {message}");

    public void Warn(string message) => log($"[{taskId}] warn: {message}");

    public void ShowRunSummary(PellyCoder.Core.Logging.RunSummary summary, string? runDirectory) { }
}
