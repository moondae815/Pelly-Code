using System.Reflection;
using PellyCoder.Cli.Commands;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Completion;

/// <summary>
/// One command in the completion tree. <paramref name="AcceptsPositional"/>: the command itself takes positional arguments
/// (a file, a request), so a word that is not a child name is left to the shell's path completion.
/// </summary>
internal sealed record CompletionNode(string Name, IReadOnlyList<OptionInfo> Options, IReadOnlyList<CompletionNode> Children, bool AcceptsPositional);

/// <summary>Builds the command tree for shell completion from <see cref="CliApp.CommandSettings"/> (design §4.4).</summary>
internal static class CompletionTree
{
    private static readonly OptionInfo Help = new("help", false);
    private static readonly OptionInfo Version = new("version", false);

    /// <summary>
    /// Root "pelly" with <c>--help</c>/<c>--version</c>. A first word registered both alone ("sql") and as a prefix
    /// ("sql pull") is a branch whose default command takes that word's settings; one registered only as a prefix
    /// ("spec new") is a branch with a usage-only default. Every command gets <c>--help</c>; <c>ui</c> edit commands
    /// leave out <see cref="UiCommands.GuiRejectedOptions"/>.
    /// </summary>
    public static CompletionNode Build()
    {
        var registry = CliApp.CommandSettings;
        var top = registry.Keys.Select(k => k.Split(' ')[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var children = top.Select(word =>
        {
            var subs = registry.Keys.Where(k => k.StartsWith(word + " ", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(k => Leaf(k.Split(' ')[1], k, registry[k]))
                .ToList();
            if (registry.TryGetValue(word, out var own))
                return Leaf(word, word, own) with { Children = subs };
            return new CompletionNode(word, [Help], subs, AcceptsPositional: false);
        }).ToList();
        return new CompletionNode("pelly", [Help, Version], children, AcceptsPositional: false);
    }

    private static CompletionNode Leaf(string name, string key, Type settings)
    {
        var options = CliApp.CommandOptions(settings).AsEnumerable();
        if (key.StartsWith("ui ", StringComparison.Ordinal))
            options = options.Where(o => !UiCommands.GuiRejectedOptions.Contains(o.LongName));
        var positional = settings.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.GetCustomAttribute<CommandArgumentAttribute>() is not null);
        return new CompletionNode(name, [.. options, Help], [], positional);
    }
}
