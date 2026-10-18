using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlBatchSplitterTests
{
    private static string Corpus(string file) =>
        File.ReadAllText(CorpusLocation.Get("Sql/objects/" + file)).TrimStart('﻿').Replace("\r\n", "\n");

    private static SqlScript Split(string text) => SqlBatchSplitter.Split(text, 160);

    [Fact]
    public void SsmsScript_SplitsIntoSetSetObject()
    {
        var script = Split(Corpus("usp_CancelOrder.sql"));
        Assert.Equal(3, script.Batches.Count);
        var obj = script.Batches[2];
        Assert.Equal(SqlObjectKind.Procedure, obj.ObjectKind);
        Assert.True(obj.ObjectName!.SameAs(SqlObjectName.Parse("dbo.usp_CancelOrder")));
        Assert.Equal(5, obj.StartLine);
        Assert.Equal(52, obj.EndLine);
        Assert.Equal("CreateProcedureStatement", obj.StatementType);
        Assert.Empty(obj.ParseErrors);
    }

    [Fact]
    public void SettlementAll_HasTwoObjectsAndTwoGrants()
    {
        var script = Split(Corpus("Settlement_All.sql"));
        Assert.Equal(2, script.ObjectBatches.Count);
        Assert.Equal("usp_SettleDaily", script.ObjectBatches[0].ObjectName!.Name);
        Assert.Equal("usp_SettleMonthly", script.ObjectBatches[1].ObjectName!.Name);
        var grants = script.Batches.Where(b => b.ObjectKind is null && b.Text.Contains("GRANT")).ToList();
        Assert.Equal(2, grants.Count);
    }

    [Fact]
    public void View_And_Function_AreClassified()
    {
        Assert.Equal(SqlObjectKind.View, Split(Corpus("vw_ShippedOrders.sql")).ObjectBatches.Single().ObjectKind);
        Assert.Equal(SqlObjectKind.Function, Split(Corpus("fn_OrderTotal.sql")).ObjectBatches.Single().ObjectKind);
    }

    [Fact]
    public void GoInsideStringOrComment_DoesNotSplit()
    {
        Assert.Single(Split("SELECT N'\nGO\n'").Batches);
        Assert.Single(Split("/*\nGO\n*/\nSELECT 1").Batches);
    }

    [Theory]
    [InlineData("SELECT 1\ngo\nSELECT 2")]
    [InlineData("SELECT 1\n  GO  \nSELECT 2")]
    [InlineData("SELECT 1\nGO 2\nSELECT 2")]
    public void GoWithCountAndCase_Splits(string text)
    {
        var script = Split(text);
        Assert.Equal(2, script.Batches.Count);
        Assert.Equal((1, 1), (script.Batches[0].StartLine, script.Batches[0].EndLine));
        Assert.Equal((3, 3), (script.Batches[1].StartLine, script.Batches[1].EndLine));
        Assert.Equal(1, script.Batches[1].Index);
    }

    [Fact]
    public void CreateOrAlter_IsObjectBatch()
    {
        var batch = Split("CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1").Batches.Single();
        Assert.Equal("CreateOrAlterProcedureStatement", batch.StatementType);
        Assert.Equal(SqlObjectKind.Procedure, batch.ObjectKind);
    }

    [Fact]
    public void ParseErrorInBatch_IsKept_WithFileLine()
    {
        var script = Split("SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3\nFROM FROM FROM\n");
        Assert.Empty(script.Batches[0].ParseErrors);
        var errors = script.Batches[2].ParseErrors;
        Assert.NotEmpty(errors);
        Assert.Equal(6, errors[0].Line);
    }

    [Fact]
    public void UnclosedComment_IsLexError()
    {
        Assert.NotEmpty(Split("SELECT 1 /* 끝나지 않음").LexErrors);
    }

    [Fact]
    public void SettingsBefore_FollowsLastSet()
    {
        var script = Split("SET QUOTED_IDENTIFIER OFF\nGO\nCREATE PROC dbo.p AS SELECT 1");
        var settings = script.SettingsBefore(1);
        Assert.False(settings.QuotedIdentifier);
        Assert.True(settings.AnsiNulls);
        Assert.Equal(SqlSessionSettings.Default, script.SettingsBefore(0));
    }

    [Fact]
    public void WhitespaceOnlySegments_AreNotBatches()
    {
        var script = Split("SELECT 1\nGO\n\nGO\n  \n");
        Assert.Single(script.Batches);
    }
}
