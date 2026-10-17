using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
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
}
