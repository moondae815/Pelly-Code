using System.IO.Abstractions.TestingHelpers;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Models;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

public sealed class GroupErrorAttributionTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";
    private static readonly Workspace Ws = new(Root, Path.Combine(Root, ".pelly"));

    // Line numbers: Divide spans 5-8, Other 10-13, the field initializer is line 15.
    private const string Calc =
        "namespace Demo;\n" +
        "\n" +
        "public class Calc\n" +
        "{\n" +
        "    public int Divide(int a, int b)\n" +
        "    {\n" +
        "        return a / c;\n" +
        "    }\n" +
        "\n" +
        "    public int Other(string s)\n" +
        "    {\n" +
        "        return Divide(s, 1);\n" +
        "    }\n" +
        "\n" +
        "    private int _x = Missing();\n" +
        "}\n";

    private readonly MockFileSystem _fs = new();

    public GroupErrorAttributionTests()
    {
        _fs.AddFile(P("App/Calc.cs"), new MockFileData(Calc));
        _fs.AddFile(P("App/Caller.cs"), new MockFileData(Calc.Replace("class Calc", "class Caller")));
    }

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static readonly GroupMember Divide = new("T1", P("App/Calc.cs"), "Calc.Divide(int, int)");
    private static readonly GroupMember Unrelated = new("T2", P("App/Calc.cs"), "Calc.Missing()");

    private static Diagnostic Error(string file, int line, string code = "CS0103") =>
        new(file, line, 16, "error", code, "message");

    private Attribution Attribute(params Diagnostic[] errors) =>
        GroupErrorAttribution.Attribute(_fs, Ws, errors, [Divide, Unrelated], LanguageVersion.Latest);

    [Fact]
    public void Error_inside_a_group_target_is_attributed_to_its_task()
    {
        var error = Error("App/Calc.cs", 7);

        var result = Attribute(error);

        Assert.Equal(error, Assert.Single(result.ByTask["T1"]));
        Assert.False(result.ByTask.ContainsKey("T2"));
        Assert.Empty(result.Unattributed);
    }

    [Fact]
    public void Error_in_another_member_of_a_group_file_is_unattributed_with_member_location()
    {
        var error = Error("App/Calc.cs", 12, "CS1503");

        var result = Attribute(error);

        Assert.Empty(result.ByTask);
        Assert.Equal(("App/Calc.cs#Other", error), Assert.Single(result.Unattributed));
    }

    [Fact]
    public void Error_outside_any_member_is_unattributed_with_line_location()
    {
        var result = Attribute(Error("App/Calc.cs", 15));

        Assert.Equal("App/Calc.cs:15", Assert.Single(result.Unattributed).Location);
    }

    [Fact]
    public void Error_in_a_file_outside_the_group_is_unattributed()
    {
        // Caller.cs has a member with the same display shape but is not a group file.
        var result = Attribute(Error("App/Caller.cs", 7));

        Assert.Empty(result.ByTask);
        Assert.Equal("App/Caller.cs#Divide", Assert.Single(result.Unattributed).Location);
    }

    [Fact]
    public void Error_in_a_missing_file_is_unattributed_with_line_location()
    {
        var result = Attribute(Error("App/Gone.cs", 4));

        Assert.Equal("App/Gone.cs:4", Assert.Single(result.Unattributed).Location);
    }

    [Fact]
    public void Error_without_file_is_unattributed()
    {
        var error = new Diagnostic("", 0, 0, "error", "CS5001", "Program does not contain a static 'Main' method");

        var result = Attribute(error);

        Assert.Empty(result.ByTask);
        Assert.Equal(("CS5001", error), Assert.Single(result.Unattributed));
    }

    [Fact]
    public void Group_file_matches_by_full_path_with_os_comparer()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return; // case-sensitive file systems keep case
        var member = Divide with { FullPath = P("app/calc.cs") };

        var result = GroupErrorAttribution.Attribute(_fs, Ws, [Error("App/Calc.cs", 7)], [member], LanguageVersion.Latest);

        Assert.Single(result.ByTask["T1"]);
    }
}
