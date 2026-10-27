using PellyCoder.Cli.Completion;
using PellyCoder.Core;

namespace PellyCoder.Cli.Tests.Completion;

public class PowerShellCompletionTests
{
    private static readonly CompletionNode Root = CompletionTree.Build();
    private static readonly string Script = PowerShellCompletion.Script(Root, PellyInfo.Version);

    private static IEnumerable<CompletionNode> All(CompletionNode node) => node.Children.SelectMany(All).Prepend(node);

    [Fact]
    public void Script_IsAscii() => Assert.All(Script, c => Assert.True(c <= 0x7F, $"non-ASCII U+{(int)c:X4}"));

    [Fact]
    public void Script_FirstLineNamesVersion() => Assert.StartsWith("# pelly " + PellyInfo.Version + " ", Script);

    [Fact]
    public void Script_RegistersForPellyAndPellyExe()
    {
        Assert.Contains("Register-ArgumentCompleter -Native -CommandName pelly, pelly.exe", Script);
    }

    [Fact]
    public void Script_ContainsEveryCommandAndOption()
    {
        foreach (var node in All(Root))
        {
            Assert.Contains($"'{node.Name}'", Script);
            foreach (var option in node.Options) Assert.Contains($"'--{option.LongName}'", Script);
        }
    }

    [Fact]
    public void Script_UsesNoPowerShell7OnlySyntax()
    {
        foreach (var token in new[] { "?.", "??", "&&", "||" }) Assert.DoesNotContain(token, Script);
    }

    [Fact]
    public void Script_KeepsStateInsideTheScriptBlock()
    {
        // Run without dot-sourcing, script-scope variables vanish while the completer stays registered.
        Assert.DoesNotContain("$script:", Script);
        Assert.True(Script.IndexOf("-ScriptBlock {", StringComparison.Ordinal) < Script.IndexOf("Name = 'pelly'", StringComparison.Ordinal));
    }

    [Fact]
    public void Script_BranchWithFilesAddsFilenameCompletion() =>
        Assert.Contains("[System.Management.Automation.CompletionCompleters]::CompleteFilename($wordToComplete)", Script);

    [Fact]
    public void Script_OffersPositionalValues() => Assert.Contains("PositionalValues = @('powershell')", Script);

    [Fact]
    public void ThinkValues_MatchThinkModeEnum() =>
        Assert.Equal(["off", "retry", "always"], PowerShellCompletion.OptionValues["think"]);
}
