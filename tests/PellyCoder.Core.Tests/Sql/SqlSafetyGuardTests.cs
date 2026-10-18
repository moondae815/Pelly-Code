using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlSafetyGuardTests
{
    private const int Compat = 160;

    private static string Corpus(string file) =>
        File.ReadAllText(CorpusLocation.Get("Sql/objects/" + file)).TrimStart('﻿').Replace("\r\n", "\n");

    private static SqlSafetyResult Run(string original, string patched, int index = -1)
    {
        var script = SqlBatchSplitter.Split(original, Compat);
        if (index < 0)
            index = script.ObjectBatches[0].Index;
        return SqlSafetyGuard.Check(script, index, patched, Compat);
    }

    private static string Proc(string body) =>
        "CREATE PROCEDURE dbo.p\nAS\nBEGIN\n" + body + "\nEND";

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.p AS BEGIN SELECT 1; END", "CREATE PROCEDURE dbo.p AS BEGIN SELECT 2; END")]
    [InlineData("ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1; END", "ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 2; END")]
    [InlineData("CREATE OR ALTER VIEW dbo.v AS SELECT 1 AS a", "CREATE OR ALTER VIEW dbo.v AS SELECT 2 AS a")]
    public void AllowedForms_Pass(string original, string patched)
    {
        var result = Run(original, patched);
        Assert.True(result.Ok, result.Reason);
        Assert.Null(result.Reason);
        Assert.Empty(result.ParseErrors);
    }

    [Fact]
    public void SsmsFile_BodyEdit_Passes()
    {
        var text = Corpus("usp_CancelOrder.sql");
        var result = Run(text, text.Replace("IF @Status = 9", "IF @Status IN (9, 10)"));
        Assert.True(result.Ok, result.Reason);
    }

    [Fact]
    public void GoInsertedInTarget_IsRejected()
    {
        var original = "SET ANSI_NULLS ON\nGO\n" + Proc("SELECT 1;");
        var patched = "SET ANSI_NULLS ON\nGO\n" + Proc("SELECT 1;\nGO\nSELECT 2;");
        var result = Run(original, patched);
        Assert.False(result.Ok);
        Assert.Equal("the edit added or removed a GO batch separator. Keep the whole object in one batch and do not add GO.", result.Reason);
    }

    [Fact]
    public void OtherBatchChanged_IsRejected()
    {
        var text = Corpus("usp_CancelOrder.sql");
        var result = Run(text, text.Replace("SET ANSI_NULLS ON", "SET ANSI_NULLS OFF"));
        Assert.False(result.Ok);
        Assert.Equal("text outside the target object batch changed. Only the target object may change.", result.Reason);
    }

    [Fact]
    public void SecondStatementAfterObject_IsRejected()
    {
        // A view must be the only statement in its batch, so ScriptDom reports a parse error (check 3 runs first).
        var view = Run("CREATE VIEW dbo.v AS SELECT 1 AS a", "CREATE VIEW dbo.v AS SELECT 1 AS a; DROP TABLE x");
        Assert.False(view.Ok);
        Assert.Null(view.Reason);
        Assert.NotEmpty(view.ParseErrors);

        // A function parses a trailing statement as a second top-level statement.
        var fn = Run("CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1; END", "CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1; END; DROP TABLE x");
        Assert.False(fn.Ok);
        Assert.True(fn.Reason is not null || fn.ParseErrors.Count > 0);
    }

    [Fact]
    public void TopLevelNoExecOffAndCommitAfterProcedure_IsRejected()
    {
        var original = Proc("SELECT 1;");
        var result = Run(original, original + ";\nSET NOEXEC OFF;\nCOMMIT;");
        Assert.False(result.Ok);
        // The procedure body swallows the trailing statements (none is top-level), so the forbidden-construct check catches it.
        Assert.Equal("forbidden construct 'SET NOEXEC' in the target object. Remove it.", result.Reason);
    }

    [Fact]
    public void RenamedObject_IsRejected()
    {
        var text = Corpus("usp_CancelOrder.sql");
        var result = Run(text, text.Replace("dbo.usp_CancelOrder", "dbo.usp_CancelOrder2"));
        Assert.False(result.Ok);
        Assert.Equal("the target batch must contain exactly one CREATE PROCEDURE statement for dbo.usp_CancelOrder.", result.Reason);
    }

    [Fact]
    public void CreateChangedToAlter_IsRejected()
    {
        var result = Run("CREATE PROCEDURE dbo.p AS SELECT 1", "ALTER PROCEDURE dbo.p AS SELECT 1");
        Assert.False(result.Ok);
        Assert.Equal("the target batch must contain exactly one CREATE PROCEDURE statement for dbo.p.", result.Reason);
    }

    [Fact]
    public void CreateOrAlterDisplay_IsSpelledOut()
    {
        var result = Run("CREATE OR ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1; END", "CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1; END");
        Assert.Equal("the target batch must contain exactly one CREATE OR ALTER FUNCTION statement for dbo.f.", result.Reason);
    }

    public static TheoryData<string, string> ForbiddenCases() => new()
    {
        { "SET NOEXEC ON;", "SET NOEXEC" },
        { "SET PARSEONLY ON;", "SET PARSEONLY" },
        { "SET FMTONLY ON;", "SET FMTONLY" },
        { "SHUTDOWN;", "SHUTDOWN" },
        { "RECONFIGURE;", "RECONFIGURE" },
        { "EXEC master..xp_cmdshell 'dir';", "xp_cmdshell" },
        { "SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1') AS r;", "OPENROWSET" },
        { "SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x;').db.dbo.t;", "OPENDATASOURCE" },
        { "ALTER DATABASE d SET RECOVERY SIMPLE;", "ALTER DATABASE" },
        { "CREATE LOGIN l WITH PASSWORD = 'x';", "CREATE LOGIN" },
        { "ALTER LOGIN l DISABLE;", "ALTER LOGIN" },
        { "DROP LOGIN l;", "DROP LOGIN" },
        { "GRANT SELECT ON dbo.t TO u;", "GRANT" },
        { "REVOKE SELECT ON dbo.t FROM u;", "REVOKE" },
        { "DENY SELECT ON dbo.t TO u;", "DENY" },
    };

    [Theory]
    [MemberData(nameof(ForbiddenCases))]
    public void Forbidden_EachIsRejected(string statement, string display)
    {
        var result = Run(Proc("SELECT 1;"), Proc("SELECT 1;\n" + statement));
        Assert.False(result.Ok);
        Assert.Equal($"forbidden construct '{display}' in the target object. Remove it.", result.Reason);
    }

    [Fact]
    public void ForbiddenWordInStringOrComment_IsAllowed()
    {
        var body = "SELECT N'xp_cmdshell';\n-- SHUTDOWN\n/* GRANT */\nSELECT 1;";
        var result = Run(Proc("SELECT 1;"), Proc(body));
        Assert.True(result.Ok, result.Reason);
    }

    [Fact]
    public void ParseError_ReturnsFileLines()
    {
        var original = "SET ANSI_NULLS ON\nGO\nCREATE PROCEDURE dbo.p\nAS\nSELECT 1";
        var patched = "SET ANSI_NULLS ON\nGO\nCREATE PROCEDURE dbo.p\nAS\nSELECT FROM FROM";
        var result = Run(original, patched);
        Assert.False(result.Ok);
        Assert.Null(result.Reason);
        Assert.NotEmpty(result.ParseErrors);
        Assert.Equal(5, result.ParseErrors[0].Line);
    }

    [Fact]
    public void ForbiddenIn_ListsOriginalConstructs()
    {
        var script = SqlBatchSplitter.Split(Proc("GRANT SELECT ON dbo.t TO u;"), Compat);
        Assert.Equal(["GRANT"], SqlSafetyGuard.ForbiddenIn(script.ObjectBatches[0], Compat));
        var clean = SqlBatchSplitter.Split(Corpus("usp_CancelOrder.sql"), Compat);
        Assert.Empty(SqlSafetyGuard.ForbiddenIn(clean.ObjectBatches[0], Compat));
    }
}
