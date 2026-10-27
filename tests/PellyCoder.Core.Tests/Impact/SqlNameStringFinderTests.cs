using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Impact;

public class SqlNameStringFinderTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";
    private static readonly Workspace Ws = new(Root, Path.Combine(Root, ".pelly"));
    private static readonly SqlNameSeed Seed = new("T1", new SqlObjectName("dbo", "usp_Cancel"));

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static MockFileSystem WithFile(string code)
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("A.cs"), new MockFileData(code));
        return fs;
    }

    private static IReadOnlyList<ImpactTarget> Run(string code, SqlNameSeed? seed = null) =>
        SqlNameStringFinder.Find(WithFile(code), Ws, [seed ?? Seed]).Targets;

    private static string Methods(params string[] bodies) =>
        "class A {\n" + string.Join("\n", bodies.Select((b, i) => $"void M{i}() {{ var s = {b}; }}")) + "\n}";

    private static string[] Names(IReadOnlyList<ImpactTarget> t) => t.Select(x => x.Member).ToArray();

    [Fact]
    public void Finds_exact_literal_forms()
    {
        var targets = Run(Methods("\"dbo.usp_Cancel\"", "\"usp_Cancel\"", "\"[dbo].[usp_Cancel]\"", "\"[usp_Cancel]\"", "\"DBO.USP_CANCEL\""));

        Assert.Equal(["M0", "M1", "M2", "M3", "M4"], Names(targets));
        Assert.All(targets, t => Assert.Equal(SqlNameStringFinder.Reason, t.Reason));
        Assert.All(targets, t => Assert.Equal("A.cs", t.Path));
    }

    [Fact]
    public void Finds_exec_inside_inline_sql()
    {
        var targets = Run(Methods("\"EXEC dbo.usp_Cancel @Id\"", "@\"EXECUTE usp_Cancel\"", "\"\"\"EXEC dbo.usp_Cancel\"\"\"", "$\"EXEC dbo.usp_Cancel {id}\""));

        Assert.Equal(["M0", "M1", "M2", "M3"], Names(targets));
    }

    [Fact]
    public void Ignores_prefix_and_suffix_names()
    {
        Assert.Empty(Run(Methods("\"dbo.usp_CancelOrder\"", "\"x_usp_Cancel\"", "\"SELECT usp_Cancel_flag\"")));
    }

    [Fact]
    public void Other_schema_is_not_a_match()
    {
        Assert.Empty(Run(Methods("\"sales.usp_Cancel\"", "\"EXEC sales.usp_Cancel\"")));
    }

    [Fact]
    public void Null_schema_seed_is_dbo()
    {
        var seed = new SqlNameSeed("T1", new SqlObjectName(null, "usp_Cancel"));
        Assert.Equal(["M0", "M1"], Names(Run(Methods("\"dbo.usp_Cancel\"", "\"usp_Cancel\"", "\"sales.usp_Cancel\""), seed)));
    }

    [Fact]
    public void Non_dbo_seed_needs_the_schema()
    {
        var seed = new SqlNameSeed("T1", new SqlObjectName("sales", "usp_Cancel"));
        Assert.Equal(["M0", "M2"], Names(Run(Methods("\"sales.usp_Cancel\"", "\"usp_Cancel\"", "\"EXEC [sales].[usp_Cancel]\""), seed)));
    }

    [Fact]
    public void Field_initializer_is_outside_member()
    {
        var targets = Run("class A { const string P = \"dbo.usp_Cancel\"; }");

        var t = Assert.Single(targets);
        Assert.Equal(new ImpactTarget("T1", "A.cs", "A", "멤버 밖"), t);
    }

    [Fact]
    public void Repeated_hits_in_one_member_give_one_target()
    {
        var targets = Run("class A { void M() { var a = \"usp_Cancel\"; var b = \"EXEC usp_Cancel\"; } }");

        Assert.Single(targets);
    }

    [Fact]
    public void Truncated_walk_sets_incomplete()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("A.cs"), new MockFileData("class A { }"));
        fs.AddFile(P("sub/B.cs"), new MockFileData("class B { }"));

        var (_, incomplete) = SqlNameStringFinder.Find(fs, Ws, [Seed], maxDirectories: 1);

        Assert.True(incomplete);
    }

    [Fact]
    public void Complete_walk_is_not_incomplete()
    {
        Assert.False(SqlNameStringFinder.Find(WithFile("class A { }"), Ws, [Seed]).Incomplete);
    }
}
