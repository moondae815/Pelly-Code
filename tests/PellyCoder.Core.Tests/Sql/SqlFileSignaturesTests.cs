using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

/// <summary>Stored procedure parameters read from a .sql file (A2 design §4.4).</summary>
public sealed class SqlFileSignaturesTests
{
    private const int Level = 160;

    [Fact]
    public void Reads_parameters_types_output_and_default()
    {
        const string text = "-- 주문 목록\nCREATE PROCEDURE dbo.usp_OrderList\n  @page int,\n  @status  varchar(10) = NULL,\n" +
                            "  @total decimal(18, 2) OUTPUT\nAS\nSELECT 1;\n";

        var signatures = SqlFileSignatures.Read(text, Level);

        var s = Assert.Single(signatures!);
        Assert.Equal("dbo.usp_OrderList", s.Name.Display);
        Assert.Null(s.FirstResultSet);
        Assert.Equal(
            [
                new SqlParameterInfo("@page", "int", false),
                new SqlParameterInfo("@status", "varchar(10)", false, HasDefault: true),
                new SqlParameterInfo("@total", "decimal(18, 2)", true),
            ],
            s.Parameters);
    }

    [Fact]
    public void Create_or_alter_and_alter_supported()
    {
        const string text = "CREATE OR ALTER PROCEDURE usp_A @a int AS SELECT 1;\nGO\nALTER PROCEDURE [dbo].[usp_B] AS SELECT 2;\nGO\n" +
                            "CREATE VIEW dbo.v AS SELECT 1 AS x;\nGO\nCREATE PROC dbo.usp_C (@c nvarchar(max) = N'x' OUT) AS SELECT 3;\n";

        var signatures = SqlFileSignatures.Read(text, Level)!;

        Assert.Equal(["usp_A", "dbo.usp_B", "dbo.usp_C"], signatures.Select(s => s.Name.Display));
        Assert.Equal([new SqlParameterInfo("@a", "int", false)], signatures[0].Parameters);
        Assert.Empty(signatures[1].Parameters);
        Assert.Equal([new SqlParameterInfo("@c", "nvarchar(max)", true, HasDefault: true)], signatures[2].Parameters);
    }

    [Fact]
    public void Unparsable_file_returns_null() =>
        Assert.Null(SqlFileSignatures.Read("CREATE PROCEDURE ((( AS", Level));
}
