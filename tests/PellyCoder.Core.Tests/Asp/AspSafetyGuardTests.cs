using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

public class AspSafetyGuardTests
{
    private static readonly string[] NoSanitizers = [];

    private static IReadOnlyList<AspSafetyFinding> New(string original, string patched, params string[] sanitizers) =>
        AspSafetyGuard.NewFindings(AspPage.Parse(original), AspPage.Parse(patched), sanitizers);

    private static IReadOnlyList<AspSafetyFinding> Of(string text, params string[] sanitizers) =>
        AspSafetyGuard.Findings(AspPage.Parse(text), sanitizers);

    [Fact]
    public void Shell_New_Rejected()
    {
        var findings = New("<%\nx = 1\n%>", "<%\nx = 1\nSet sh = Server.CreateObject(\"WScript.Shell\")\n%>");
        var finding = Assert.Single(findings);
        Assert.Equal("ShellObject", finding.Rule);
        Assert.Equal(3, finding.Line);
        Assert.Contains("WScript.Shell", finding.Statement);
        Assert.Equal("ShellObject", Assert.Single(Of("<% Set a = CreateObject(\"shell.application\") %>")).Rule);
        Assert.Equal("ShellObject", Assert.Single(Of("<% Set n = CreateObject(\"WScript.Network\") %>")).Rule);
    }

    [Fact]
    public void Fso_AllowedWhenOriginalUses()
    {
        const string original = "<%\nSet fso = Server.CreateObject(\"Scripting.FileSystemObject\")\n%>";
        const string patched = "<%\nSet fso = Server.CreateObject(\"Scripting.FileSystemObject\")\n"
            + "Set fso2 = Server.CreateObject(\"Scripting.FileSystemObject\")\n%>";
        Assert.Empty(New(original, patched));

        var fresh = Assert.Single(New("<%\nx = 1\n%>", original));
        Assert.Equal("ShellObject", fresh.Rule);
    }

    [Fact]
    public void Execute_New_Rejected()
    {
        var findings = New("<%\nx = 1\n%>", "<%\nx = 1\nExecute \"y = 2\"\nz = Eval(\"1+1\")\nExecuteGlobal code\n%>");
        Assert.Equal(3, findings.Count);
        Assert.All(findings, f => Assert.Equal("DynamicCode", f.Rule));
        Assert.Equal([3, 4, 5], findings.Select(f => f.Line));
        Assert.Empty(Of("<%\n' Execute this later\ns = \"Eval\"\n%>"));
    }

    [Fact]
    public void AdoExecuteMember_NotDynamicCode()
    {
        Assert.Empty(Of("<%\nSet rs = conn.Execute(sql)\ncmd.Execute\nWith cmd\n  .Execute\nEnd With\n%>"));
    }

    [Fact]
    public void OnErrorResumeNext_Added_Rejected()
    {
        var findings = New("<%\nx = 1\n%>", "<%\non error resume next\nx = 1\n%>");
        var finding = Assert.Single(findings);
        Assert.Equal("OnErrorResumeNext", finding.Rule);
        Assert.Equal(2, finding.Line);
        Assert.Empty(Of("<%\nOn Error GoTo 0\n%>"));
    }

    [Fact]
    public void SqlConcat_RequestDirect_Rejected()
    {
        var finding = Assert.Single(Of("<%\nsql = \"SELECT * FROM t WHERE id = \" & Request(\"id\")\n%>"));
        Assert.Equal("SqlConcatRequest", finding.Rule);
        Assert.Equal(2, finding.Line);
        Assert.Equal("SqlConcatRequest", Assert.Single(Of("<% sql = \"delete from t where a='\" & Request.Form(\"a\") & \"'\" %>")).Rule);
        Assert.Equal("SqlConcatRequest", Assert.Single(Of("<% conn.Execute \"exec dbo.P \" & Request.QueryString(\"a\") %>")).Rule);
        Assert.Equal("SqlConcatRequest", Assert.Single(Of("<% sql = \"UPDATE t SET a=1 WHERE b=\" & Request.Cookies(\"b\") %>")).Rule);
    }

    [Fact]
    public void SqlConcat_TaintedVariable_Rejected()
    {
        const string text = "<%\nid = Request(\"id\")\nsql = \"SELECT * FROM t WHERE id = \" & id\n%>";
        var finding = Assert.Single(Of(text));
        Assert.Equal("SqlConcatRequest", finding.Rule);
        Assert.Equal(3, finding.Line);

        // Tainted in another procedure (or the page body) does not taint this procedure's variable.
        Assert.Empty(Of("<%\nid = Request(\"id\")\nSub Load(id)\n  sql = \"SELECT * FROM t WHERE id = \" & id\nEnd Sub\n%>"));
        Assert.Single(Of("<%\nSub Load()\n  id = Request(\"id\")\n  sql = \"SELECT * FROM t WHERE id = \" & id\nEnd Sub\n%>"));
    }

    [Fact]
    public void SqlConcat_ClngWrapped_Allowed()
    {
        Assert.Empty(Of("<% sql = \"SELECT * FROM t WHERE id = \" & CLng(Request(\"id\")) %>"));
        Assert.Empty(Of("<%\nid = CLng(Request(\"id\"))\nsql = \"SELECT * FROM t WHERE id = \" & id\n%>"));
    }

    [Fact]
    public void SqlConcat_ReplaceQuotes_Allowed()
    {
        Assert.Empty(Of("<% sql = \"SELECT * FROM t WHERE n = '\" & Replace(Request(\"n\"), \"'\", \"''\") & \"'\" %>"));
    }

    [Fact]
    public void SqlConcat_ReplaceOtherArgs_Rejected()
    {
        var finding = Assert.Single(Of("<% sql = \"SELECT * FROM t WHERE n = '\" & Replace(Request(\"n\"), \"a\", \"b\") & \"'\" %>"));
        Assert.Equal("SqlConcatRequest", finding.Rule);
    }

    [Fact]
    public void SqlConcat_SiteSanitizer_Allowed()
    {
        const string text = "<% sql = \"SELECT * FROM t WHERE n = '\" & SafeSQL(Request(\"n\")) & \"'\" %>";
        Assert.Empty(Of(text, "SafeSQL"));
        Assert.Single(Of(text));
    }

    [Fact]
    public void SqlConcat_NonSqlString_Allowed()
    {
        Assert.Empty(Of("<% html = \"<td>\" & Request(\"q\") %>"));
        Assert.Empty(Of("<% x = \"selected\" & Request(\"q\") %>"));
    }

    [Fact]
    public void CommandParameter_NotConcat()
    {
        const string text = "<%\ncmd.CommandText = \"SELECT * FROM t WHERE id = ?\"\n"
            + "cmd.Parameters.Append cmd.CreateParameter(\"@id\", 3, 1, , Request(\"id\"))\n%>";
        Assert.Empty(Of(text));
    }

    [Fact]
    public void Safety_ExistingConcat_NotCounted()
    {
        const string line = "sql = \"SELECT * FROM t WHERE id = \" & Request(\"id\")\n";
        Assert.Empty(New("<%\n" + line + "%>", "<%\nx = 1\n" + line + "%>"));
        var finding = Assert.Single(New("<%\n" + line + "%>", "<%\n" + line + "y = 2\n" + line + "%>"));
        Assert.Equal("SqlConcatRequest", finding.Rule);
        Assert.Equal(4, finding.Line);
    }
}
