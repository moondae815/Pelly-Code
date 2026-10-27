using System.Text;
using PellyCoder.Core.Config;

namespace PellyCoder.Cli.Completion;

/// <summary>
/// The PowerShell completion script (design §4.2, §4.3): the tree as hashtable literals and one native completer.
/// ASCII only and Windows PowerShell 5.1 syntax; it runs no process. A completer that returns nothing lets PowerShell
/// fall back to its own path completion, which is what file and free-value positions rely on.
/// </summary>
internal static class PowerShellCompletion
{
    /// <summary>Options whose value is one of a fixed set, by long name. <c>--think</c> takes the <see cref="ThinkMode"/> names, as the config loader parses them.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> OptionValues = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["think"] = Enum.GetNames<ThinkMode>().Select(n => n.ToLowerInvariant()).ToList(),
    };

    public static string Script(CompletionNode root, string version)
    {
        var sb = new StringBuilder();
        sb.Append("# pelly ").Append(version).Append(" PowerShell completion. Regenerate after updating pelly: pelly completion powershell\n");
        sb.Append("$script:PellyCompletionTree = ");
        Node(sb, root, 0);
        sb.Append('\n');
        sb.Append("$script:PellyCompletionValues = @{\n");
        foreach (var (option, values) in OptionValues)
            sb.Append("    '--").Append(option).Append("' = @(").Append(string.Join(", ", values.Select(Quote))).Append(")\n");
        sb.Append("}\n");
        sb.Append(Completer);
        return sb.ToString();
    }

    private static void Node(StringBuilder sb, CompletionNode node, int depth)
    {
        var pad = new string(' ', depth * 4);
        sb.Append("@{\n");
        sb.Append(pad).Append("    Name = ").Append(Quote(node.Name)).Append('\n');
        sb.Append(pad).Append("    Positional = ").Append(node.AcceptsPositional ? "$true" : "$false").Append('\n');
        sb.Append(pad).Append("    Options = @(");
        sb.Append(string.Join(", ", node.Options.Select(o => "@{ Name = " + Quote("--" + o.LongName) + "; TakesValue = " + (o.TakesValue ? "$true" : "$false") + " }")));
        sb.Append(")\n");
        sb.Append(pad).Append("    Children = @(");
        for (var i = 0; i < node.Children.Count; i++)
        {
            sb.Append(i == 0 ? "\n" : ",\n").Append(pad).Append("        ");
            Node(sb, node.Children[i], depth + 2);
        }
        if (node.Children.Count > 0) sb.Append('\n').Append(pad).Append("    ");
        sb.Append(")\n");
        sb.Append(pad).Append('}');
    }

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    // Walks the words before the cursor down the tree, then completes options, fixed values or child names.
    // Returning nothing hands the position to PowerShell's path completion.
    private const string Completer = """
        Register-ArgumentCompleter -Native -CommandName pelly, pelly.exe -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            $words = New-Object System.Collections.Generic.List[string]
            $elements = @($commandAst.CommandElements)
            for ($i = 1; $i -lt $elements.Count; $i++) {
                $extent = $elements[$i].Extent
                if ($extent.EndOffset -gt $cursorPosition) { break }
                if ($extent.EndOffset -eq $cursorPosition -and $wordToComplete -ne '') { break }
                $words.Add($extent.Text)
            }
            $node = $script:PellyCompletionTree
            $positionalSeen = $false
            foreach ($word in $words) {
                if ($word.StartsWith('-')) { continue }
                $child = $null
                if (-not $positionalSeen) {
                    foreach ($candidate in $node.Children) {
                        if ([string]::Equals($candidate.Name, $word, [System.StringComparison]::OrdinalIgnoreCase)) { $child = $candidate; break }
                    }
                }
                if ($null -ne $child) { $node = $child } else { $positionalSeen = $true }
            }
            $previous = $null
            if ($words.Count -gt 0) { $previous = $words[$words.Count - 1] }
            $ignoreCase = [System.StringComparison]::OrdinalIgnoreCase
            if ($wordToComplete.StartsWith('-')) {
                foreach ($option in $node.Options) {
                    if ($option.Name.StartsWith($wordToComplete, $ignoreCase)) {
                        [System.Management.Automation.CompletionResult]::new($option.Name, $option.Name, 'ParameterName', $option.Name)
                    }
                }
                return
            }
            if ($null -ne $previous -and $script:PellyCompletionValues.ContainsKey($previous)) {
                foreach ($value in $script:PellyCompletionValues[$previous]) {
                    if ($value.StartsWith($wordToComplete, $ignoreCase)) {
                        [System.Management.Automation.CompletionResult]::new($value, $value, 'ParameterValue', $value)
                    }
                }
                return
            }
            if ($null -ne $previous -and $previous.StartsWith('-')) {
                foreach ($option in $node.Options) {
                    if ($option.TakesValue -and [string]::Equals($option.Name, $previous, $ignoreCase)) { return }
                }
            }
            if (-not $positionalSeen) {
                foreach ($candidate in $node.Children) {
                    if ($candidate.Name.StartsWith($wordToComplete, $ignoreCase)) {
                        [System.Management.Automation.CompletionResult]::new($candidate.Name, $candidate.Name, 'ParameterValue', $candidate.Name)
                    }
                }
            }
        }

        """;
}
