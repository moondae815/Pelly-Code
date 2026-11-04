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

    private const string Select = "\"SELECT * FROM t WHERE id = \"";

    [Fact]
    public void TaintedVariable_ViaTrim_Rejected()
    {
        var finding = Assert.Single(Of("<%\nn = Trim(Request(\"n\"))\nsql = \"SELECT * FROM t WHERE n = '\" & n & \"'\"\n%>"));
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void Variable_ViaReplaceQuotes_Allowed()
    {
        Assert.Empty(Of("<%\nn = Replace(Request(\"n\"), \"'\", \"''\")\nsql = \"SELECT * FROM t WHERE n = '\" & n & \"'\"\n%>"));
    }

    [Fact]
    public void Variable_ConcatLiteral_Rejected()
    {
        Assert.Single(Of("<%\nn = Request(\"n\") & \"\"\nsql = " + Select + " & n\n%>"));
    }

    [Fact]
    public void Reassigned_ThroughClng_Allowed()
    {
        Assert.Empty(Of("<%\nid = Request(\"id\")\nid = CLng(id)\nsql = " + Select + " & id\n%>"));
    }

    [Fact]
    public void UseBeforeTaintingAssignment_NotFlagged()
    {
        Assert.Empty(Of("<%\nsql = " + Select + " & id\nid = Request(\"id\")\n%>"));
    }

    [Fact]
    public void IncrementalSql_RequestAppended_Rejected()
    {
        var finding = Assert.Single(Of("<%\nsql = \"SELECT * FROM t\"\nsql = sql & \" WHERE id = \" & Request(\"id\")\n%>"));
        Assert.Equal("SqlConcatRequest", finding.Rule);
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void IncrementalSql_TaintedVariableAppended_Rejected()
    {
        const string text = "<%\nn = Request(\"n\")\nsql = \"SELECT * FROM t WHERE 1 = 1\"\n"
            + "If n <> \"\" Then sql = sql & \" AND n = '\" & n & \"'\"\nsql = sql & \" ORDER BY n\"\n%>";
        var finding = Assert.Single(Of(text));
        Assert.Equal(4, finding.Line);
    }

    [Fact]
    public void IncrementalSql_ClngAppended_Allowed()
    {
        Assert.Empty(Of("<%\nsql = \"SELECT * FROM t\"\nsql = sql & \" WHERE id = \" & CLng(Request(\"id\"))\n%>"));
    }

    [Fact]
    public void PlusConcat_Rejected()
    {
        Assert.Single(Of("<% sql = " + Select + " + Request(\"id\") %>"));
        Assert.Single(Of("<%\nsql = \"SELECT * FROM t\"\nsql = sql + Request(\"id\")\n%>"));
        Assert.Empty(Of("<%\nid = Request(\"id\")\ntotal = id + 1\n%>"));
    }

    [Fact]
    public void NonSqlVariable_Append_Allowed()
    {
        Assert.Empty(Of("<%\nhtml = \"<ul>\"\nhtml = html & Request(\"q\")\nhtml = html & \"</ul>\"\n%>"));
    }

    [Fact]
    public void DottedSanitizer_Allowed()
    {
        Assert.Empty(Of("<% sql = " + Select + " & Util.SafeSQL(Request(\"id\")) %>", "SafeSQL"));
        Assert.Empty(Of("<%\nWith Util\n  sql = " + Select + " & .SafeSQL(Request(\"id\"))\nEnd With\n%>", "SafeSQL"));
        Assert.Single(Of("<% sql = " + Select + " & Util.CLng(Request(\"id\")) %>"));
    }

    [Fact]
    public void ConcatInsideCallArgument_Rejected()
    {
        Assert.Single(Of("<% Set rs = conn.Execute(" + Select + " & Request(\"x\")) %>"));
    }

    [Fact]
    public void SingleLineIf_TaintsVariable()
    {
        Assert.Single(Of("<%\nIf c Then id = Request(\"id\")\nsql = " + Select + " & id\n%>"));
    }

    [Fact]
    public void BranchElse_DoesNotClearTaint_Rejected()
    {
        Assert.Single(Of("<%\nIf c Then id = Request(\"id\") Else id = 0\nsql = " + Select + " & id\n%>"));
        Assert.Single(Of("<%\nid = Request(\"id\")\nIf c Then id = CLng(id)\nsql = " + Select + " & id\n%>"));
    }

    [Fact]
    public void MultiLineIfElse_DoesNotClearTaint_Rejected()
    {
        const string text = "<%\nIf c Then\n  id = Request(\"id\")\nElse\n  id = 0\nEnd If\nsql = " + Select + " & id\n%>";
        var finding = Assert.Single(Of(text));
        Assert.Equal(7, finding.Line);
        Assert.Single(Of("<%\nid = Request(\"id\")\nSelect Case m\n  Case 1\n    id = CLng(id)\nEnd Select\nsql = " + Select + " & id\n%>"));
        Assert.Single(Of("<%\nid = Request(\"id\")\nFor i = 1 To 2\n  id = CLng(id)\nNext\nsql = " + Select + " & id\n%>"));
    }

    [Fact]
    public void UnconditionalClngAfterBlock_Allowed()
    {
        const string text = "<%\nIf c Then\n  id = Request(\"id\")\nElse\n  id = 0\nEnd If\nid = CLng(id)\nsql = " + Select + " & id\n%>";
        Assert.Empty(Of(text));
    }

    [Fact]
    public void ColonJoined_SqlVariable_Rejected()
    {
        Assert.Single(Of("<% sql = \"SELECT * FROM t\" : sql = sql & Request(\"x\") %>"));
        Assert.Single(Of("<% If c Then sql = \"SELECT * FROM t\" : sql = sql & Request(\"x\") %>"));
    }

    [Fact]
    public void ColonJoined_Taint_Rejected()
    {
        Assert.Single(Of("<% id = Request(\"id\") : sql = " + Select + " & id %>"));
        Assert.Single(Of("<% If c Then id = Request(\"id\") : sql = " + Select + " & id %>"));
    }
    [Fact]
    public void WhereAccumulator_DirtyBaseline_NewTaintedFragment_Rejected()
    {
        const string original = "<%\nw = \" WHERE 1=1\"\n"
            + "If Request(\"a\") <> \"\" Then w = w & \" AND a='\" & Request(\"a\") & \"'\"\n"
            + "sql = \"SELECT * FROM t\" & w\n%>";
        const string patched = "<%\nw = \" WHERE 1=1\"\n"
            + "If Request(\"a\") <> \"\" Then w = w & \" AND a='\" & Request(\"a\") & \"'\"\n"
            + "If Request(\"b\") <> \"\" Then w = w & \" AND b='\" & Request(\"b\") & \"'\"\n"
            + "sql = \"SELECT * FROM t\" & w\n%>";
        var finding = Assert.Single(New(original, patched));
        Assert.Equal("SqlConcatRequest", finding.Rule);
        Assert.Equal(4, finding.Line);
        Assert.Single(Of(original));
    }

    [Fact]
    public void WhereAccumulator_CleanFragment_Allowed()
    {
        Assert.Empty(Of("<%\nw = \" WHERE 1=1\"\nw = w & \" AND b=\" & CLng(Request(\"b\"))\nsql = \"SELECT * FROM t\" & w\n%>"));
    }
    private const string DirtyId = "<%\nid = Request(\"id\")\nsql = \"SELECT * FROM t WHERE id=\" & id\n";

    [Fact]
    public void DirtyIdPage_NewResponseWriteOfId_NotFlagged()
    {
        const string patched = DirtyId
            + "Response.Write \"<a href='edit.asp?id=\" & id & \"'>edit</a>\"\n"
            + "Response.Redirect \"view.asp?id=\" & id\n%>";

        Assert.Empty(New(DirtyId + "%>", patched));
        Assert.Single(Of(DirtyId + "%>")); // the raw SQL concatenation itself is still a finding
    }

    [Fact]
    public void DirtyIdPage_UrlChain_DoesNotSpread()
    {
        const string original = "<%\nq = Request(\"q\")\npage = Request(\"page\")\n"
            + "sql = \"SELECT * FROM t WHERE q='\" & q & \"'\"\n%>";
        const string patched = "<%\nq = Request(\"q\")\npage = Request(\"page\")\n"
            + "sql = \"SELECT * FROM t WHERE q='\" & q & \"'\"\n"
            + "url = \"list.asp?p=\" & page & \"&q=\" & q\n"
            + "Response.Write \"<a href='\" & url & \"'>\" & q & \"</a>\"\n%>";

        Assert.Empty(New(original, patched));
    }
    [Fact]
    public void ConcatAssignedInput_ThenSql_RejectedOnce_EchoAllowed()
    {
        const string text = "<%\nname = \"Mr \" & Request(\"n\")\nsql = \"SELECT * FROM t WHERE n='\" & name & \"'\"\n"
            + "Response.Write \"<b>\" & name & \"</b>\"\n%>";

        Assert.Equal(3, Assert.Single(Of(text)).Line);
    }
}
