using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

public class AspNameCheckTests
{
    private static readonly AspIncludeSet NoIncludes = new([], [], false);

    private static AspIncludeSet Includes(params (string Path, string Text)[] files) =>
        new(files.Select(f => new AspIncludedFile(f.Path, AspPage.Parse(f.Text))).ToArray(), [], false);

    private static AspNameResult Check(string original, string patched, AspIncludeSet? includes = null) =>
        AspNameCheck.NewUnknown(AspPage.Parse(original), AspPage.Parse(patched), includes ?? NoIncludes, AspBuiltins.Default);

    [Fact]
    public void Names_InventedFunction_Error()
    {
        var result = Check("<%\nx = Trim(a)\n%>", "<%\nx = Trim(a)\ny = FormatOrderNo(x)\n%>");
        var unknown = Assert.Single(result.Unknown);
        Assert.Equal(new AspUnknownName("FormatOrderNo", 3), unknown);
        Assert.False(result.Downgraded);
        Assert.Null(result.DowngradeReason);
    }

    [Fact]
    public void Names_OptionExplicit_UndeclaredVariable_Error()
    {
        var original = "<%\nOption Explicit\nDim total\ntotal = 1\n%>";
        var patched = "<%\nOption Explicit\nDim total\ntotal = 1\ncount = total + 1\n%>";
        var unknown = Assert.Single(Check(original, patched).Unknown);
        Assert.Equal(new AspUnknownName("count", 5), unknown);
    }

    [Fact]
    public void Names_NoOptionExplicit_AssignedElsewhere_Ok()
    {
        var original = "<%\nSub Show()\n  Response.Write \"a\"\nEnd Sub\nmsg = \"hi\"\n%>";
        var patched = "<%\nSub Show()\n  Response.Write msg\nEnd Sub\nmsg = \"hi\"\n%>";
        Assert.Empty(Check(original, patched).Unknown);
        Assert.Single(Check("<%\nOption Explicit\n" + original[3..], "<%\nOption Explicit\n" + patched[3..]).Unknown);
    }

    [Fact]
    public void Names_DeclaredInInclude_Ok()
    {
        var includes = Includes(("/site/inc/func.inc", "<%\nConst SITE_NAME = \"a\"\nFunction FormatOrderNo(n)\n  FormatOrderNo = n\nEnd Function\n%>"));
        var original = "<!-- #include file=\"inc/func.inc\" -->\n<%\nOption Explicit\nDim x\n%>";
        var patched = "<!-- #include file=\"inc/func.inc\" -->\n<%\nOption Explicit\nDim x\nx = FormatOrderNo(1) & SITE_NAME\n%>";
        var result = Check(original, patched, includes);
        Assert.Empty(result.Unknown);
        Assert.False(result.Downgraded);
    }

    [Fact]
    public void Names_Parameter_Ok()
    {
        var original = "<%\nOption Explicit\nSub Show(orderId, status)\n  Dim label\n  Response.Write orderId\nEnd Sub\n%>";
        var patched = "<%\nOption Explicit\nSub Show(orderId, status)\n  Dim label\n  label = status\n  Response.Write orderId & label\nEnd Sub\n%>";
        Assert.Empty(Check(original, patched).Unknown);

        // Another procedure's parameter is not visible.
        var other = "<%\nOption Explicit\nSub Show(orderId, status)\nEnd Sub\nSub Other()\n  Response.Write status\nEnd Sub\n%>";
        var unknown = Assert.Single(Check("<%\nOption Explicit\nSub Show(orderId, status)\nEnd Sub\nSub Other()\nEnd Sub\n%>", other).Unknown);
        Assert.Equal("status", unknown.Name);
    }

    [Fact]
    public void Names_ClassFieldsVisibleOnlyInsideTheirClass()
    {
        const string original = "<%\nOption Explicit\nClass Cart\n  Private items\n  Public Function Count()\n    Count = 0\n  End Function\nEnd Class\nClass Other\n  Sub Show()\n  End Sub\nEnd Class\nDim c\nSet c = New Cart\n%>";
        var patched = original
            .Replace("    Count = 0\n", "    Count = UBound(items) + Me.Size\n")
            .Replace("  Sub Show()\n", "  Sub Show()\n    Response.Write items\n");
        var unknown = Assert.Single(Check(original, patched).Unknown);
        Assert.Equal(new AspUnknownName("items", 11), unknown);
    }

    [Fact]
    public void Names_FunctionReturnAssignment_Ok()
    {
        var original = "<%\nOption Explicit\nFunction GetAmount(n)\n  GetAmount = n\nEnd Function\n%>";
        var patched = "<%\nOption Explicit\nFunction GetAmount(n)\n  If n < 0 Then GetAmount = 0 Else GetAmount = n\nEnd Function\n%>";
        Assert.Empty(Check(original, patched).Unknown);
    }

    [Fact]
    public void Names_MemberAfterDot_Ignored()
    {
        var original = "<%\nOption Explicit\nDim rs\n%>";
        var patched = "<%\nOption Explicit\nDim rs\nrs.MoveNextCustom\nResponse.Write rs.Fields.Item(\"a\").Value\n%>";
        Assert.Empty(Check(original, patched).Unknown);
    }

    [Fact]
    public void Names_AdoConstant_OnlyWithAdovbs()
    {
        const string body = "<%\nOption Explicit\nDim rs\n%>";
        const string use = "<%\nOption Explicit\nDim rs\nrs.Open \"q\", 1, adOpenStatic\n%>";
        Assert.Equal("adOpenStatic", Assert.Single(Check(body, use).Unknown).Name);

        var inc = "<!-- #include virtual=\"/inc/ADOVBS.INC\" -->\n";
        var withAdovbs = Includes(("/site/inc/ADOVBS.INC", "<%\n%>"));
        Assert.Empty(Check(inc + body, inc + use, withAdovbs).Unknown);

        var nested = Includes(("/site/inc/common.inc", "<!-- #include file=\"adovbs.inc\" -->\n"), ("/site/inc/adovbs.inc", "<% %>"));
        var commonInc = "<!-- #include file=\"inc/common.inc\" -->\n";
        Assert.Empty(Check(commonInc + body, commonInc + use, nested).Unknown);

        var metadata = "<!--METADATA TYPE=\"typelib\" NAME=\"Microsoft ActiveX Data Objects 2.5 Library\" UUID=\"{00000205-0000-0010-8000-00AA006D2EA4}\" -->\n";
        Assert.Empty(Check(metadata + body, metadata + use).Unknown);
    }

    [Fact]
    public void Names_AdoConstant_OptionEnabled_Ok()
    {
        const string body = "<%\nOption Explicit\nDim rs\n%>";
        const string use = "<%\nOption Explicit\nDim rs\nrs.Open \"q\", 1, adOpenStatic\n%>";
        var result = AspNameCheck.NewUnknown(AspPage.Parse(body), AspPage.Parse(use), NoIncludes, AspBuiltins.Default,
            adoConstantsKnown: true);
        Assert.Empty(result.Unknown);
    }

    [Fact]
    public void Names_AdoConstant_OriginalAlreadyUses_Ok()
    {
        const string original = "<%\nOption Explicit\nDim rs\nrs.Open \"q\", 1, adOpenStatic\n%>";
        const string patched = "<%\nOption Explicit\nDim rs\nrs.Open \"q\", 1, adOpenStatic\nrs.CursorLocation = adUseClient\n%>";
        Assert.Empty(Check(original, patched).Unknown);
        // A non-ADO unknown name in the original does not make ADO constants known.
        Assert.Equal("adUseClient", Assert.Single(Check("<%\nOption Explicit\nDim x\nx = Legacy(1)\n%>",
            "<%\nOption Explicit\nDim x\nx = Legacy(1)\nx = adUseClient\n%>").Unknown).Name);
    }

    [Fact]
    public void Names_ExistingUnknown_NotCounted()
    {
        var original = "<%\nOption Explicit\nx = Legacy1(a)\ny = Legacy1(b)\nResponse.Write z\n%>";
        var patched = "<%\nOption Explicit\n' moved\nx = Legacy1(a)\ny = Legacy1(b)\nResponse.Write z\nw = Legacy1(c)\n%>";
        var result = Check(original, patched);
        // x, y, a, b, z already unknown in the original; only the third Legacy1 call, w and c are new.
        Assert.Equal(
            [new AspUnknownName("Legacy1", 7), new AspUnknownName("w", 7), new AspUnknownName("c", 7)],
            result.Unknown.OrderBy(u => u.Line).ThenBy(u => u.Name == "Legacy1" ? 0 : u.Name == "w" ? 1 : 2));
        Assert.Empty(Check(original, original.Replace("x = Legacy1(a)", "x = Legacy1(a) + 1")).Unknown);
    }

    [Fact]
    public void Names_UnresolvedInclude_Downgraded()
    {
        var includes = new AspIncludeSet([], ["inc/missing.inc"], false);
        var page = "<!-- #include file=\"inc/missing.inc\" -->\n<%\nx = 1\n%>";
        var patched = "<!-- #include file=\"inc/missing.inc\" -->\n<%\nx = Helper(1)\n%>";
        var result = Check(page, patched, includes);
        Assert.True(result.Downgraded);
        Assert.Equal("include를 다 찾지 못해 이름 검사를 경고로 낮췄습니다.", result.DowngradeReason);
        Assert.Equal("Helper", Assert.Single(result.Unknown).Name);

        var truncated = Check(page, patched, new AspIncludeSet([], [], true));
        Assert.True(truncated.Downgraded);
        Assert.Contains("include를 다 찾지 못해", truncated.DowngradeReason);
    }

    [Fact]
    public void Names_Execute_Downgraded()
    {
        var result = Check("<%\nExecute \"x = 1\"\n%>", "<%\nExecute \"x = 1\"\ny = x\n%>");
        Assert.True(result.Downgraded);
        Assert.Equal("Execute·Eval이 있어 이름 검사를 경고로 낮췄습니다.", result.DowngradeReason);
    }
}
