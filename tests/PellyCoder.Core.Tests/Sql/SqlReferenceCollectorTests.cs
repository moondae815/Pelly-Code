using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlReferenceCollectorTests
{
    private static string ObjectBatch(string file)
    {
        var text = File.ReadAllText(CorpusLocation.Get("Sql/objects/" + file)).TrimStart('﻿').Replace("\r\n", "\n");
        return SqlBatchSplitter.Split(text, 160).ObjectBatches.Single().Text;
    }

    private static SqlReferences Collect(string batch, string target = "dbo.usp_Target") =>
        SqlReferenceCollector.Collect(batch, SqlObjectName.Parse(target), 160, true);

    private static string[] Names(SqlReferences r) => r.Objects.Select(o => o.Name.Display).ToArray();

    [Fact]
    public void CancelOrder_ReferencesOrdersAndHistories()
    {
        var r = Collect(ObjectBatch("usp_CancelOrder.sql"), "dbo.usp_CancelOrder");
        Assert.Equal(["dbo.Orders", "dbo.OrderHistories"], Names(r));
        Assert.False(r.HasDynamicSql);
        Assert.False(r.UsesTempTables);
    }

    [Fact]
    public void TempCteAndTableVariable_AreNotObjects()
    {
        var r = Collect(ObjectBatch("usp_BuildSettlement.sql"), "dbo.usp_BuildSettlement");
        Assert.Equal(["dbo.Orders", "dbo.Sellers"], Names(r));
        Assert.True(r.UsesTempTables);

        var r2 = Collect("""
            CREATE PROCEDURE dbo.usp_Target AS
            DECLARE @t TABLE (Id int);
            WITH x AS (SELECT OrderId FROM dbo.Orders), y AS (SELECT OrderId FROM x)
            SELECT * FROM y JOIN @t t ON t.Id = y.OrderId;
            """);
        Assert.Equal(["dbo.Orders"], Names(r2));
        Assert.False(r2.UsesTempTables);
    }

    [Fact]
    public void DynamicSql_IsFlagged()
    {
        var r = Collect(ObjectBatch("usp_SearchOrdersDynamic.sql"), "dbo.usp_SearchOrdersDynamic");
        Assert.True(r.HasDynamicSql);
        Assert.DoesNotContain(r.Objects, o => o.Name.Name.Contains("sp_executesql"));
        Assert.True(Collect("CREATE PROCEDURE dbo.usp_Target AS EXEC('SELECT 1')").HasDynamicSql);
        Assert.True(Collect("CREATE PROCEDURE dbo.usp_Target @x nvarchar(10) AS EXEC(@x)").HasDynamicSql);
    }

    [Fact]
    public void SchemaQualifiedFunction_IsObject()
    {
        var r = Collect("""
            CREATE PROCEDURE dbo.usp_Target AS
            SELECT dbo.fn_OrderTotal(o.OrderId), GETDATE(), UPPER(o.Name) FROM dbo.Orders o
            CROSS APPLY dbo.fn_Lines(o.OrderId) l;
            """);
        Assert.Equal(["dbo.fn_OrderTotal", "dbo.Orders", "dbo.fn_Lines"], Names(r));
    }

    [Fact]
    public void ExecProcedure_IsObject_SystemProcIsNot()
    {
        var r = Collect("""
            CREATE PROCEDURE dbo.usp_Target @p int AS
            DECLARE @n sysname = N'dbo.usp_Y';
            EXEC dbo.usp_X;
            EXEC sp_help 'dbo.Orders';
            EXEC xp_foo;
            EXEC @n;
            EXEC dbo.usp_Target;
            """);
        Assert.Equal(["dbo.usp_X"], Names(r));
    }

    [Fact]
    public void MultiPartNames_AreSkippedWithText()
    {
        var r = Collect("""
            CREATE PROCEDURE dbo.usp_Target AS
            SELECT * FROM OtherDb.dbo.T;
            SELECT * FROM Srv.Db.dbo.T2;
            SELECT * FROM dbo.Local;
            """);
        Assert.Equal(["dbo.Local"], Names(r));
        Assert.Equal(["OtherDb.dbo.T", "Srv.Db.dbo.T2"], r.SkippedMultiPart);
    }

    [Fact]
    public void Columns_AreCollected_CaseInsensitive()
    {
        var r = Collect(ObjectBatch("usp_CancelOrder.sql"), "dbo.usp_CancelOrder");
        Assert.Contains("cancelreason", r.Columns);
        Assert.Contains("ORDERID", r.Columns);
    }

    [Fact]
    public void FirstOccurrence_LineIsBatchRelative()
    {
        var r = Collect("CREATE PROCEDURE dbo.usp_Target AS\nSELECT 1;\nSELECT * FROM dbo.Orders;\nSELECT * FROM dbo.Orders;");
        var o = Assert.Single(r.Objects);
        Assert.Equal(3, o.Line);
        Assert.Equal(15, o.Column);
    }

    [Fact]
    public void InsertedDeleted_AreIgnored_UnparsedBatchThrows()
    {
        var r = Collect("CREATE TRIGGER dbo.trg ON dbo.Orders AFTER UPDATE AS SELECT * FROM inserted i JOIN deleted d ON d.OrderId = i.OrderId");
        Assert.Empty(r.Objects);
        Assert.Throws<ArgumentException>(() => Collect("CREATE PROCEDURE dbo.usp_Target AS SELEC 1"));
    }

    [Fact]
    public void Position_IsEarliestTextualOccurrence_NotFirstVisited()
    {
        var r = Collect("CREATE PROCEDURE dbo.usp_Target AS\nWITH c AS (SELECT * FROM dbo.Orders)\nSELECT * FROM dbo.Orders o JOIN c ON 1=1;");
        var o = Assert.Single(r.Objects);
        Assert.Equal(2, o.Line);
    }

    [Fact]
    public void UpdateDeleteAliasTargets_AreNotObjects()
    {
        Assert.Equal(["dbo.Orders"], Names(Collect("CREATE PROCEDURE dbo.usp_Target AS UPDATE o SET Status = 1 FROM dbo.Orders o;")));
        Assert.Equal(["dbo.Orders"], Names(Collect("CREATE PROCEDURE dbo.usp_Target AS DELETE o FROM dbo.Orders o;")));
    }

    [Fact]
    public void CteNamedLikeTable_DoesNotHideTheTable()
    {
        var r = Collect("CREATE PROCEDURE dbo.usp_Target AS\nWITH Orders AS (SELECT OrderId FROM dbo.Orders)\nSELECT OrderId FROM Orders;");
        var o = Assert.Single(r.Objects);
        Assert.Equal("dbo.Orders", o.Name.Display);
        Assert.Equal(2, o.Line);
    }

    [Fact]
    public void ColumnMethodCalls_AreIgnored()
    {
        var r = Collect("""
            CREATE PROCEDURE dbo.usp_Target AS
            SELECT e.OrgNode.GetLevel(), s.GetX(), dbo.fn_Real(1)
            FROM dbo.Emp e JOIN dbo.Shapes s ON 1=1
            CROSS APPLY e.Xml.nodes('/x') t(c);
            """);
        Assert.Equal(["dbo.fn_Real", "dbo.Emp", "dbo.Shapes"], Names(r));
        Assert.Empty(r.SkippedMultiPart);
    }

    [Fact]
    public void Minor_DedupeTempDdlSystemSchemaProcAndReturnValueExec()
    {
        var r = Collect("""
            CREATE PROCEDURE dbo.usp_Target AS
            SELECT * FROM A.B.C; SELECT * FROM A.B.C;
            DECLARE @ret int;
            EXEC @ret = dbo.usp_X;
            EXEC dbo.sp_x;
            TRUNCATE TABLE #t;
            """);
        Assert.Equal(["A.B.C"], r.SkippedMultiPart);
        Assert.Equal(["dbo.usp_X", "dbo.sp_x"], Names(r));
        Assert.True(r.UsesTempTables, "truncate");
        Assert.True(Collect("CREATE PROCEDURE dbo.usp_Target AS DROP TABLE #t").UsesTempTables, "drop");
        Assert.True(Collect("CREATE PROCEDURE dbo.usp_Target AS ALTER TABLE #t ADD c int").UsesTempTables, "alter");
    }

    [Fact]
    public void SelfAlias_IsStillObject()
    {
        var r = Collect("CREATE PROCEDURE dbo.usp_Target AS SELECT * FROM Orders AS Orders;");
        Assert.Contains("Orders", Names(r));
    }

    [Fact]
    public void UpdateAliasTarget_StillExcluded()
    {
        var r = Collect("CREATE PROCEDURE dbo.usp_Target AS UPDATE o SET Status = 1 FROM dbo.Orders o;");
        Assert.Equal(["dbo.Orders"], Names(r));
    }

    [Fact]
    public void AliasInOtherStatement_DoesNotHide()
    {
        var r = Collect("""
            CREATE PROCEDURE dbo.usp_Target AS
            SELECT * FROM dbo.Orders o;
            SELECT * FROM o;
            """);
        Assert.Equal(["dbo.Orders", "o"], Names(r));
    }
}
