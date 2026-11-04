using PellyCoder.Core.Asp;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.Asp;

public class AspStructureCheckTests
{
    private static AspRunState State(AspPage page, AspProcedure? procedure) =>
        new(page, procedure is null ? AspTargetUnit.Lines : AspTargetUnit.Procedure, new TextSpanInfo(1, 1), procedure,
            new AspIncludeSet([], [], false), "UTF-8", "/site");

    [Fact]
    public void Structure_NewUnclosedIf_Reported()
    {
        var original = AspPage.Parse("<%\nIf a Then\n  b = 1\nEnd If\n%>");
        var patched = AspPage.Parse("<%\nIf a Then\n  b = 1\nIf c Then\n  d = 2\nEnd If\n%>");
        var problem = Assert.Single(AspStructureCheck.NewProblems(original, patched));
        Assert.Equal("ASP005", problem.Code);
    }

    [Fact]
    public void Structure_ExistingProblems_NotCounted()
    {
        var original = AspPage.Parse("<div><div><p>a</p>\n<b>x</b>");
        var patched = AspPage.Parse("<div><div><p>a</p>\n<b>y</b>");
        Assert.Equal(2, original.Problems.Count(p => p.Code == "ASP006"));
        Assert.Equal(2, patched.Problems.Count(p => p.Code == "ASP006"));
        Assert.Empty(AspStructureCheck.NewProblems(original, patched));
    }

    [Fact]
    public void Structure_ShiftedLines_SameProblem_NotNew()
    {
        var original = AspPage.Parse("<%\nIf a Then\n  b = 1\n%>");
        var patched = AspPage.Parse("<%\nx = 1\ny = 2\nIf a Then\n  b = 1\n%>");
        Assert.Single(original.Problems);
        Assert.Single(patched.Problems);
        Assert.Empty(AspStructureCheck.NewProblems(original, patched));
    }

    [Fact]
    public void Shape_IncludeChanged()
    {
        var original = AspPage.Parse("<!-- #include file=\"a.inc\" -->\n<% x = 1 %>");
        var patched = AspPage.Parse("<!-- #include file=\"b.inc\" -->\n<% x = 1 %>");
        var reason = AspStructureCheck.ShapeViolation(original, patched, State(original, null));
        Assert.NotNull(reason);
        Assert.Contains("include", reason);
    }

    private const string Proc = "<%\nFunction Total(a, b)\n  Total = a + b\nEnd Function\n%>";

    [Fact]
    public void Shape_ProcRenamed()
    {
        var original = AspPage.Parse(Proc);
        var patched = AspPage.Parse(Proc.Replace("Total", "Sum"));
        var state = State(original, original.Procedures[0]);
        Assert.NotNull(AspStructureCheck.ShapeViolation(original, patched, state));
        Assert.Null(AspStructureCheck.ShapeViolation(original, AspPage.Parse(Proc.Replace("a + b", "b + a")), state));
    }

    [Fact]
    public void Shape_ParamAdded()
    {
        var original = AspPage.Parse(Proc);
        var patched = AspPage.Parse(Proc.Replace("(a, b)", "(a, b, c)"));
        var reason = AspStructureCheck.ShapeViolation(original, patched, State(original, original.Procedures[0]));
        Assert.NotNull(reason);
        Assert.Contains("매개변수", reason);
    }

    [Fact]
    public void Shape_LinesTarget_NewProcedureAllowed()
    {
        var original = AspPage.Parse(Proc);
        var patched = AspPage.Parse(Proc + "\n<%\nSub Extra()\nEnd Sub\n%>");
        Assert.Null(AspStructureCheck.ShapeViolation(original, patched, State(original, null)));
    }

    [Fact]
    public void Shape_DirectiveChanged()
    {
        var original = AspPage.Parse("<%@ Language=\"VBScript\" %>\n<% x = 1 %>");
        var patched = AspPage.Parse("<%@ Language=\"VBScript\" CodePage=949 %>\n<% x = 1 %>");
        var reason = AspStructureCheck.ShapeViolation(original, patched, State(original, null));
        Assert.NotNull(reason);
        Assert.Contains("지시문", reason);
    }

    [Fact]
    public void Shape_KeywordChanged()
    {
        var original = AspPage.Parse(Proc);
        var patched = AspPage.Parse("<%\nSub Total(a, b)\n  x = a + b\nEnd Sub\n%>");
        var reason = AspStructureCheck.ShapeViolation(original, patched, State(original, original.Procedures[0]));
        Assert.NotNull(reason);
        Assert.Contains("종류", reason);
    }

    [Fact]
    public void Shape_ProcedureTarget_NewProcedureRejected()
    {
        var original = AspPage.Parse(Proc);
        var patched = AspPage.Parse(Proc + "\n<%\nSub Extra()\nEnd Sub\n%>");
        var reason = AspStructureCheck.ShapeViolation(original, patched, State(original, original.Procedures[0]));
        Assert.NotNull(reason);
        Assert.Contains("프로시저 수", reason);
    }
}
