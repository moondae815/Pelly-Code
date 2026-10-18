using Microsoft.SqlServer.TransactSql.ScriptDom;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlParsersTests
{
    [Fact]
    public void Create_ReturnsMatchingParserForEverySupportedLevel()
    {
        foreach (var level in SqlParsers.SupportedLevels)
            Assert.Equal($"TSql{level}Parser", SqlParsers.Create(level, true).GetType().Name);
        Assert.IsType<TSql160Parser>(SqlParsers.Create(160, false));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(165)]
    [InlineData(180)]
    public void Create_UnsupportedLevel_Throws(int level) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlParsers.Create(level, true));
}
