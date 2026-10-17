using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CSharp;

public sealed class InlineSqlDetectorTests
{
    private const string Display = "Repo.Load(int)";

    private static string Src(string body) =>
        "namespace D;\n\npublic class Repo\n{\n    public string Load(int id)\n    {\n" + body + "\n    }\n}\n";

    private static bool Changed(string before, string after) =>
        InlineSqlDetector.ChangedSqlLiteral(Src(before), Src(after), Display, LanguageVersion.Latest);

    [Fact]
    public void InlineSql_NewSelectLiteral_True() =>
        Assert.True(Changed("        return \"x\";", "        return \"select * from T where Id = \" + id;"));

    [Fact]
    public void InlineSql_VerbatimAndInterpolated_True()
    {
        Assert.True(Changed("        return \"x\";", "        return @\"DELETE\nFROM T\";"));
        Assert.True(Changed("        return \"x\";", "        return $\"UPDATE T SET A = {id}\";"));
    }

    [Fact]
    public void InlineSql_UnchangedLiteral_False() =>
        Assert.False(Changed("        var s = \"SELECT 1\";\n        return s;", "        var s = \"SELECT 1\";\n        if (id < 0) return \"\";\n        return s;"));

    [Fact]
    public void InlineSql_NonSqlLiteral_False() =>
        Assert.False(Changed("        return \"x\";", "        return \"selection is updated\";"));

    [Fact]
    public void InlineSql_OtherMemberChanged_False() =>
        Assert.False(InlineSqlDetector.ChangedSqlLiteral(
            Src("        return \"x\";"),
            Src("        return \"x\";").Replace("}\n", "    public string Other() => \"SELECT 1\";\n}\n"),
            Display, LanguageVersion.Latest));
}
