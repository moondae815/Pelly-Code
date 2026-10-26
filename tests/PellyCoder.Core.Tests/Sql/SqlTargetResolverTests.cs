using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlTargetResolverTests
{
    private const string One = "ALTER PROCEDURE dbo.usp_A\nAS\nSELECT 1\nGO\n";
    private const string Two = One + "ALTER PROCEDURE dbo.usp_B\nAS\nSELECT 2\n";

    private static SqlScript Split(string text) => SqlBatchSplitter.Split(text, 160);

    [Fact]
    public void Single_object_is_chosen_automatically()
    {
        var r = SqlTargetResolver.Resolve(Split(One), null);
        Assert.Equal("usp_A", r.Target!.ObjectName!.Name);
        Assert.Null(r.Problem);
        Assert.False(r.Ambiguous);
    }

    [Fact]
    public void Two_objects_without_name_is_ambiguous()
    {
        var r = SqlTargetResolver.Resolve(Split(Two), null);
        Assert.Null(r.Target);
        Assert.True(r.Ambiguous);
        Assert.Contains("--object", r.Problem);
    }

    [Fact]
    public void Object_option_picks_by_name_ignoring_brackets_and_case()
    {
        var r = SqlTargetResolver.Resolve(Split(Two), "[dbo].[USP_B]");
        Assert.Equal("usp_B", r.Target!.ObjectName!.Name);
    }

    [Fact]
    public void Unknown_object_is_a_problem_not_ambiguous()
    {
        var r = SqlTargetResolver.Resolve(Split(Two), "dbo.usp_Z");
        Assert.Null(r.Target);
        Assert.False(r.Ambiguous);
        Assert.Contains("usp_Z", r.Problem);
    }

    [Fact]
    public void Bad_object_name_is_a_problem()
    {
        var r = SqlTargetResolver.Resolve(Split(One), "a.b.c.d");
        Assert.Null(r.Target);
        Assert.NotNull(r.Problem);
    }

    [Fact]
    public void File_without_object_is_a_problem()
    {
        var r = SqlTargetResolver.Resolve(Split("SELECT 1\n"), null);
        Assert.Null(r.Target);
        Assert.False(r.Ambiguous);
        Assert.NotNull(r.Problem);
    }

    [Fact]
    public void Lex_errors_are_a_problem()
    {
        var r = SqlTargetResolver.Resolve(new SqlScript([], ["unterminated"]), null);
        Assert.Null(r.Target);
        Assert.Contains("unterminated", r.Problem);
    }
}
