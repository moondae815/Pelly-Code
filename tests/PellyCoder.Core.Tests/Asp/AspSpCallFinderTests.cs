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
}
