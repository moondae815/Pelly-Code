using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlObjectNameTests
{
    [Theory]
    [InlineData("usp_X", null, "usp_X")]
    [InlineData("dbo.usp_X", "dbo", "usp_X")]
    [InlineData("[dbo].[usp X]", "dbo", "usp X")]
    public void Parse_Forms(string text, string? schema, string name)
    {
        var parsed = SqlObjectName.Parse(text);
        Assert.Equal(schema, parsed.Schema);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(schema is null ? name : $"{schema}.{name}", parsed.Display);
    }

    [Fact]
    public void SameAs_TreatsMissingSchemaAsDbo_IgnoresCase()
    {
        Assert.True(SqlObjectName.Parse("USP_x").SameAs(SqlObjectName.Parse("[DBO].[usp_X]")));
        Assert.False(SqlObjectName.Parse("usp_X").SameAs(SqlObjectName.Parse("sales.usp_X")));
    }

    [Fact]
    public void Parse_FourPart_Throws()
    {
        Assert.Throws<FormatException>(() => SqlObjectName.Parse("srv.db.dbo.usp_X"));
        Assert.Throws<FormatException>(() => SqlObjectName.Parse("db.dbo.usp_X"));
    }
}
