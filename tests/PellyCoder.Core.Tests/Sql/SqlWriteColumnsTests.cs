using PellyCoder.Core.Sql;
using Xunit;

namespace PellyCoder.Core.Tests.Sql;

public sealed class SqlWriteColumnsTests
{
    private static IReadOnlyList<SqlWrite> Collect(string sql) => SqlWriteColumns.Collect(sql, 150, true);

    [Fact]
    public void Insert_with_column_list()
    {
        var w = Assert.Single(Collect("INSERT INTO dbo.Orders (Id, Status) VALUES (1, 2)"));
        Assert.Equal(SqlWriteKind.Insert, w.Kind);
        Assert.True(w.Table.SameAs(new SqlObjectName("dbo", "Orders")));
        Assert.Equal(["Id", "Status"], w.Columns);
    }

    [Fact]
    public void Insert_without_list_is_all_columns()
    {
        var w = Assert.Single(Collect("INSERT INTO dbo.Orders SELECT 1, 2"));
        Assert.Null(w.Columns);
    }

    [Fact]
    public void Insert_with_and_without_list_merges_to_all_columns()
    {
        var w = Assert.Single(Collect("INSERT INTO dbo.Orders (Id) VALUES (1); INSERT INTO dbo.Orders SELECT 1, 2"));
        Assert.Null(w.Columns);
    }

    [Fact]
    public void Update_set_columns()
    {
        var w = Assert.Single(Collect("UPDATE dbo.Orders SET Status = 1, Total = 2 WHERE Id = 3"));
        Assert.Equal(SqlWriteKind.Update, w.Kind);
        Assert.Equal(["Status", "Total"], w.Columns);
    }

    [Fact]
    public void Update_through_alias_resolves_table()
    {
        var w = Assert.Single(Collect("UPDATE o SET Status=1 FROM dbo.Orders o JOIN dbo.Items i ON i.OrderId = o.Id"));
        Assert.True(w.Table.SameAs(new SqlObjectName("dbo", "Orders")));
        Assert.Equal(["Status"], w.Columns);
    }

    [Fact]
    public void Same_table_and_kind_unions_columns_case_insensitively()
    {
        var w = Assert.Single(Collect("UPDATE dbo.Orders SET Status = 1; UPDATE dbo.Orders SET status = 2, Total = 3"));
        Assert.Equal(["Status", "Total"], w.Columns);
    }

    [Fact]
    public void Merge_update_and_insert_columns()
    {
        var w = Assert.Single(Collect(
            "MERGE dbo.Orders AS t USING dbo.Src AS s ON t.Id = s.Id " +
            "WHEN MATCHED THEN UPDATE SET Status = s.Status " +
            "WHEN NOT MATCHED THEN INSERT (Id, Total) VALUES (s.Id, s.Total);"));
        Assert.Equal(SqlWriteKind.Merge, w.Kind);
        Assert.True(w.Table.SameAs(new SqlObjectName("dbo", "Orders")));
        Assert.Equal(["Status", "Id", "Total"], w.Columns);
    }

    [Fact]
    public void Delete_has_no_columns()
    {
        var w = Assert.Single(Collect("DELETE FROM dbo.Orders WHERE Id = 1"));
        Assert.Equal(SqlWriteKind.Delete, w.Kind);
        Assert.Empty(w.Columns!);
    }

    [Theory]
    [InlineData("DECLARE @t TABLE (Id int); INSERT INTO @t (Id) VALUES (1)")]
    [InlineData("DECLARE @t TABLE (Id int); UPDATE @t SET Id = 2")]
    [InlineData("INSERT INTO #tmp (Id) VALUES (1)")]
    [InlineData("UPDATE #tmp SET Id = 2")]
    [InlineData("DELETE FROM #tmp")]
    public void Temp_table_and_table_variable_are_skipped(string sql) => Assert.Empty(Collect(sql));

    [Fact]
    public void Cte_update_resolves_to_base_table()
    {
        var w = Assert.Single(Collect("WITH c AS (SELECT * FROM dbo.Orders) UPDATE c SET Status=1"));
        Assert.True(w.Table.SameAs(new SqlObjectName("dbo", "Orders")));
        Assert.Equal(["Status"], w.Columns);
    }

    [Fact]
    public void Cte_over_join_is_skipped() =>
        Assert.Empty(Collect("WITH c AS (SELECT o.Id FROM dbo.Orders o JOIN dbo.Items i ON i.OrderId=o.Id) UPDATE c SET Id=1"));

    [Fact]
    public void Alias_of_table_variable_is_skipped() =>
        Assert.Empty(Collect("DECLARE @tv TABLE (Id int); UPDATE x SET Id = 1 FROM @tv x"));

    [Fact]
    public void Alias_of_derived_table_is_skipped() =>
        Assert.Empty(Collect("UPDATE x SET Id = 1 FROM (SELECT Id FROM dbo.Orders) x"));

    [Fact]
    public void Output_into_records_insert()
    {
        var ws = Collect("UPDATE dbo.Orders SET Status = 1 OUTPUT inserted.Id INTO dbo.Audit (OrderId)");
        Assert.Equal(2, ws.Count);
        var a = Assert.Single(ws, w => w.Kind == SqlWriteKind.Insert);
        Assert.True(a.Table.SameAs(new SqlObjectName("dbo", "Audit")));
        Assert.Equal(["OrderId"], a.Columns);
        Assert.DoesNotContain(Collect("DELETE FROM dbo.Orders OUTPUT deleted.Id INTO #t"), w => w.Kind == SqlWriteKind.Insert);
    }

    [Fact]
    public void Variable_assignment_only_update_is_skipped() =>
        Assert.Empty(Collect("DECLARE @x int; UPDATE dbo.Orders SET @x = Total"));

    [Fact]
    public void Three_part_name_is_skipped()
    {
        Assert.Empty(Collect("INSERT INTO Other.dbo.Orders (Id) VALUES (1)"));
    }

    [Fact]
    public void Schema_defaults_to_dbo_in_result()
    {
        var w = Assert.Single(Collect("UPDATE Orders SET Status = 1"));
        Assert.Equal("dbo", w.Table.Schema);
        Assert.Equal("Orders", w.Table.Name);
    }

    [Fact]
    public void Bad_sql_never_throws()
    {
        Assert.Empty(Collect("this is not sql @@@"));
        var partial = Collect("UPDATE dbo.Orders SET Status = 1; SELECT (");
        Assert.All(partial, w => Assert.Equal("Orders", w.Table.Name));
    }
}
