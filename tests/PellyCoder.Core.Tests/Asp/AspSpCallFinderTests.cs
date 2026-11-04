using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.Asp;

public class AspSpCallFinderTests
{
    private static IReadOnlyList<AspSpCall> Find(string code, AspSpCallFunction[]? wrappers = null, TextSpanInfo? within = null) =>
        AspSpCallFinder.Find(AspPage.Parse("<%\n" + code + "\n%>"), wrappers ?? [], within);

    [Fact]
    public void CommandText_WithStoredProcType()
    {
        var calls = Find("cmd.CommandType = adCmdStoredProc\ncmd.CommandText = \"dbo.usp_Get Orders\"");
        var call = Assert.Single(calls);
        Assert.Equal("dbo.usp_Get", call.Name.Display);
        calls = Find("cmd.CommandText = \"dbo.usp_X\"\ncmd.CommandType = 4");
        Assert.Equal("dbo.usp_X", Assert.Single(calls).Name.Display);
        Assert.Equal(2, Assert.Single(calls).Line);
    }

    [Fact]
    public void ExecInString_Concatenated()
    {
        var call = Assert.Single(Find("conn.Execute(\"exec dbo.usp_SearchOrders \" & id)"));
        Assert.Equal("dbo.usp_SearchOrders", call.Name.Display);
        Assert.Null(call.ArgumentCount);
    }

    [Fact]
    public void CallSyntax()
    {
        var call = Assert.Single(Find("cmd.CommandText = \"{call usp_X(?, ?)}\""));
        Assert.Equal("usp_X", call.Name.Display);
        Assert.Equal(2, call.ArgumentCount);
    }

    [Fact]
    public void RecordsetOpen()
    {
        Assert.Equal("usp_List", Assert.Single(Find("rs.Open \"usp_List 1\", conn")).Name.Display);
        Assert.Equal("usp_Bare", Assert.Single(Find("rs.Open \"usp_Bare\", conn, 1, 3")).Name.Display);
    }

    [Fact]
    public void Wrapper_SecondArgument()
    {
        var wrappers = new[] { new AspSpCallFunction { Name = "RunSP", Argument = 2 } };
        var call = Assert.Single(Find("Set rs = RunSP(conn, \"dbo.usp_W\", 5)", wrappers));
        Assert.Equal("dbo.usp_W", call.Name.Display);
        Assert.Empty(Find("RunSP \"dbo.usp_W\", conn", wrappers));
    }

    [Fact]
    public void CountsExecArguments()
    {
        Assert.Equal(2, Assert.Single(Find("conn.Execute \"EXEC usp_X @a = 1, @b = 'x,y'\"")).ArgumentCount);
        Assert.Equal(0, Assert.Single(Find("conn.Execute \"EXECUTE usp_X\"")).ArgumentCount);
    }

    [Fact]
    public void CountsParametersAppend()
    {
        var call = Assert.Single(Find(
            "cmd.CommandText = \"usp_X\"\ncmd.Parameters.Append cmd.CreateParameter(\"a\", 3, 1, , 1)\ncmd.Parameters.Append cmd.CreateParameter(\"b\", 3, 1, , 2)"));
        Assert.Equal(2, call.ArgumentCount);
    }

    [Fact]
    public void Within_LimitsToSpan()
    {
        var code = "conn.Execute \"exec usp_A\"\nx = 1\nconn.Execute \"exec usp_B\"";
        var calls = Find(code, within: new TextSpanInfo(4, 4));
        Assert.Equal("usp_B", Assert.Single(calls).Name.Display);
    }

    [Fact]
    public void PlainSelect_NotACall()
    {
        Assert.Empty(Find("rs.Open \"SELECT * FROM Orders\", conn\ncmd.CommandText = \"select 1\"\nconn.Open \"DSN=x;UID=y\""));
    }

    [Fact]
    public void Open_XmlHttpGet_NotACall()
    {
        Assert.Empty(Find("Set x = Server.CreateObject(\"MSXML2.ServerXMLHTTP\")\nx.Open \"GET\", url, False"));
        Assert.Empty(Find("objXML.Open \"POST\", url, False"));
    }

    [Fact]
    public void Open_ConnectionDsn_NotACall()
    {
        Assert.Empty(Find("Set conn = CreateObject(\"ADODB.Connection\")\nconn.Open \"MyDsn\", \"sa\", \"pw\""));
        Assert.Empty(Find("rs.Open \"Provider=SQLOLEDB;Data Source=x\", conn"));
    }

    [Fact]
    public void CommandText_SelectWithStoredProcTypeInScope_NotACall()
    {
        Assert.Empty(Find("cmd.CommandType = adCmdStoredProc\ncmd.CommandText = \"SELECT * FROM Orders WHERE id = 1\""));
    }

    [Fact]
    public void CommandText_Concatenated_NotACall()
    {
        Assert.Empty(Find("cmd.CommandType = 4\ncmd.CommandText = \"usp_Order_\" & kind"));
    }

    [Fact]
    public void ExecWordInPlainText_NotACall()
    {
        Assert.Empty(Find("Response.Write \"Execute failed\""));
        Assert.Empty(Find("conn.Execute \"exec usp_\" & x"));
    }

    [Fact]
    public void Wrapper_ConcatenatedName_NotACall()
    {
        var wrappers = new[] { new AspSpCallFunction { Name = "ExecSP", Argument = 1 } };
        Assert.Empty(Find("ExecSP \"usp_\" & x, conn", wrappers));
    }

    [Fact]
    public void BracketedNameWithSpaces()
    {
        var call = Assert.Single(Find("conn.Execute \"exec [dbo].[usp Get Order] 1\""));
        Assert.Equal("dbo", call.Name.Schema);
        Assert.Equal("usp Get Order", call.Name.Name);
        Assert.Equal(1, call.ArgumentCount);
    }
}
