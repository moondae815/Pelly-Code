using System.Reflection;
using PellyCoder.Cli.Commands;
using PellyCoder.Cli.Completion;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Tests.Completion;

public class CompletionTreeTests
{
    private static readonly CompletionNode Root = CompletionTree.Build();

    private static CompletionNode Node(params string[] path)
    {
        var node = Root;
        foreach (var name in path) node = Assert.Single(node.Children, c => c.Name == name);
        return node;
    }

    private static string[] OptionNames(CompletionNode node) => node.Options.Select(o => o.LongName).ToArray();

    [Fact]
    public void Root_HasEveryTopLevelCommandFromRegistry()
    {
        var expected = CliApp.CommandSettings.Keys.Select(k => k.Split(' ')[0]).Distinct().Order().ToArray();
        Assert.Equal(expected, Root.Children.Select(c => c.Name).Order().ToArray());
    }

    [Fact]
    public void SqlBranch_HasSubcommandsAndAcceptsPositional()
    {
        var sql = Node("sql");
        Assert.Equal(["check", "pull"], sql.Children.Select(c => c.Name).Order().ToArray());
        Assert.True(sql.AcceptsPositional);
        Assert.Contains("object", OptionNames(sql));
        Assert.Contains("line", OptionNames(sql));
    }

    [Fact]
    public void SpecBranch_IsUsageOnly()
    {
        var spec = Node("spec");
        Assert.Equal(["check", "new"], spec.Children.Select(c => c.Name).Order().ToArray());
        Assert.False(spec.AcceptsPositional);
    }

    [Fact]
    public void EveryVisibleOptionAppears()
    {
        foreach (var (key, type) in CliApp.CommandSettings)
        {
            var node = Node(key.Split(' '));
            var visible = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.GetCustomAttribute<CommandOptionAttribute>())
                .Where(a => a is { IsHidden: false })
                .SelectMany(a => a!.LongNames)
                .Where(n => !key.StartsWith("ui ", StringComparison.Ordinal) || !UiCommands.GuiRejectedOptions.Contains(n));
            foreach (var name in visible)
                Assert.True(OptionNames(node).Contains(name), $"{key}: --{name} missing");
        }
    }

    [Fact]
    public void HiddenOptionsAreExcluded()
    {
        Assert.DoesNotContain("offline-selftest", OptionNames(Node("doctor")));
        Assert.DoesNotContain("selftest", OptionNames(Node("ui")));
    }

    [Fact]
    public void UiEditCommands_DropGuiRejectedOptions()
    {
        var uiCs = OptionNames(Node("ui", "cs"));
        Assert.DoesNotContain("yes", uiCs);
        Assert.DoesNotContain("pause-on-exit", uiCs);
        Assert.Contains("verify-test", uiCs);
        Assert.Contains("yes", OptionNames(Node("cs")));
    }

    [Fact]
    public void HelpEverywhere_VersionOnlyAtRoot()
    {
        Assert.Contains("help", OptionNames(Root));
        Assert.Contains("version", OptionNames(Root));
        foreach (var key in CliApp.CommandSettings.Keys)
        {
            var node = Node(key.Split(' '));
            Assert.Contains("help", OptionNames(node));
            Assert.DoesNotContain("version", OptionNames(node));
        }
    }

    [Fact]
    public void TakesValue_FollowsValueName()
    {
        var cs = Node("cs").Options;
        Assert.True(Assert.Single(cs, o => o.LongName == "line").TakesValue);
        Assert.False(Assert.Single(cs, o => o.LongName == "dry-run").TakesValue);
    }

    [Fact]
    public void LeafWithArguments_AcceptsPositional()
    {
        Assert.True(Node("cs").AcceptsPositional);
        Assert.False(Node("report").AcceptsPositional);
    }

    [Fact]
    public void GuiRejectedOptions_MatchGuiOptionErrors()
    {
        Assert.Single(UiCommands.GuiOptionErrors(new CsSettings { Yes = true }));
        Assert.Single(UiCommands.GuiOptionErrors(new CsSettings { PauseOnExit = true }));
        Assert.Empty(UiCommands.GuiOptionErrors(new CsSettings()));
        Assert.Equal(["pause-on-exit", "yes"], UiCommands.GuiRejectedOptions.Order().ToArray());
        var names = typeof(RunSettings).GetProperties()
            .Where(p => p.Name is nameof(RunSettings.Yes) or nameof(RunSettings.PauseOnExit))
            .SelectMany(p => p.GetCustomAttribute<CommandOptionAttribute>()!.LongNames).Order().ToArray();
        Assert.Equal(names, UiCommands.GuiRejectedOptions.Order().ToArray());
    }
}
