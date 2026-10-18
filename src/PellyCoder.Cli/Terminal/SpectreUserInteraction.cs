using PellyCoder.Core.Abstractions;
using System.Globalization;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using Spectre.Console;

namespace PellyCoder.Cli.Terminal;

/// <summary>Console implementation of <see cref="IUserInteraction"/> (design 8.11). Every external string is escaped.</summary>
public sealed class SpectreUserInteraction(IAnsiConsole console) : IUserInteraction
{
    public bool IsInteractive => !Console.IsInputRedirected && console.Profile.Capabilities.Interactive;

    public async Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct)
    {
        var prompt = new ConfirmationPrompt(Markup.Escape(message)) { DefaultValue = defaultValue };
        return await prompt.ShowAsync(console, ct);
    }

    public async Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct)
    {
        // Index-based so T needs no notnull constraint.
        var prompt = new SelectionPrompt<int>()
            .Title(Markup.Escape(title))
            .UseConverter(i => Markup.Escape(label(choices[i])))
            .AddChoices(Enumerable.Range(0, choices.Count));
        return choices[await prompt.ShowAsync(console, ct)];
    }

    public void ShowDiff(string path, string before, string after)
    {
        console.MarkupLine($"[bold]{Markup.Escape(path)}[/]");
        foreach (var (kind, text) in DiffRenderer.UnifiedLines(before, after))
        {
            var color = kind switch { '+' => "green", '-' => "red", '@' => "cyan", _ => null };
            var line = Markup.Escape(kind == '@' ? text : kind + text);
            console.MarkupLine(color is null ? line : $"[{color}]{line}[/]");
        }
    }

    public void ShowStatus(RunPhase phase, int attempt, string message) =>
        console.MarkupLine(attempt == 0
            ? $"[grey]{phase}[/] {Markup.Escape(message)}"
            : $"[grey]{phase} (시도 {attempt})[/] {Markup.Escape(message)}");

    public void Warn(string message) => console.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");

    public void ShowRunSummary(RunSummary summary, string? runDirectory)
    {
        var inv = CultureInfo.InvariantCulture;
        console.MarkupLine("[bold]실행 요약[/]");
        var table = new Table().AddColumn("단계").AddColumn("초");
        foreach (var (phase, seconds) in summary.PhaseSeconds)
            table.AddRow(Markup.Escape(phase.ToString()), Markup.Escape(seconds.ToString("0.##", inv)));
        console.Write(table);

        string Opt(int? v) => v is { } n ? n.ToString(inv) : "-";
        console.MarkupLine($"시도: {summary.Attempts.ToString(inv)}");
        console.MarkupLine($"TTFT: {Markup.Escape(summary.TtftSeconds.ToString("0.##", inv))}초, 대기: {Markup.Escape(summary.QueueSeconds.ToString("0.##", inv))}초");
        console.MarkupLine($"입력 토큰: local {summary.InputTokensLocal.ToString(inv)}, server {Opt(summary.InputTokensServer)}, cached {Opt(summary.InputTokensCached)}");
        console.MarkupLine($"think 토큰: {summary.ThinkTokens.ToString(inv)}{(summary.ThinkTruncated ? " (잘림)" : "")}");
        console.MarkupLine($"매칭 단계: {Markup.Escape(summary.MatchStages.Count == 0 ? "-" : string.Join(", ", summary.MatchStages))}");
        console.MarkupLine($"오류 코드: {Markup.Escape(summary.ErrorCodes.Count == 0 ? "-" : string.Join(", ", summary.ErrorCodes))}");
        if (runDirectory is not null)
            console.MarkupLine($"프롬프트·응답 원문: {Markup.Escape(runDirectory)}");
    }
}
