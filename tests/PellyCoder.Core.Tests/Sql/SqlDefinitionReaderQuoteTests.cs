using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

/// <summary>The OBJECT_ID argument the read-only reader sends (the queries themselves are integration-tested).</summary>
public class SqlDefinitionReaderQuoteTests
{
    [Theory]
    [InlineData(null, "usp_X", "[dbo].[usp_X]")]
    [InlineData("sales", "usp_X", "[sales].[usp_X]")]
    [InlineData(null, "My Proc", "[dbo].[My Proc]")]
    [InlineData("dbo", "a]b", "[dbo].[a]]b]")]
    public void Quote_is_two_part_with_dbo_default(string? schema, string name, string expected) =>
        Assert.Equal(expected, SqlDefinitionReader.Quote(new SqlObjectName(schema, name)));
}
