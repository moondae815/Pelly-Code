using Acornima;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class JsSafetyGuardTests
{
    private static JsDocument P(string text) => JsDocument.Parse(text, EcmaVersion.ES2015, false);

    private static IReadOnlyList<WebSafetyFinding> New(string original, string patched) =>
        JsSafetyGuard.NewFindings(P(original), P(patched));

    [Fact]
    public void Eval_New_Rejected()
    {
        var f = Assert.Single(New("function f() {}\n", "function f() {\n  eval(x);\n}\n"));
        Assert.Equal("JsEval", f.Rule);
        Assert.Equal(2, f.Line);
        Assert.Equal("JsEval", Assert.Single(New("function f() {}\n", "function f() { window.eval(x); }\n")).Rule);
    }

    [Fact]
    public void Eval_Existing_NotCounted()
    {
        const string original = "function f() {\n  eval(x);\n}\n";
        Assert.Empty(New(original, "// note\n" + original));
        Assert.Single(New(original, original + "function g() { eval(x); }\n")); // same text elsewhere is only one more
    }

    [Fact]
    public void FunctionConstructor_Rejected()
    {
        Assert.Equal("JsFunctionConstructor", Assert.Single(New("", "var f = new Function(\"a\", \"return a\");\n")).Rule);
    }

    [Fact]
    public void StringTimer_Rejected()
    {
        Assert.Equal("JsStringTimer", Assert.Single(New("", "setTimeout(\"f()\", 10);\n")).Rule);
        Assert.Equal("JsStringTimer", Assert.Single(New("", "window.setInterval(\"f(\" + x + \")\", 10);\n")).Rule);
    }

    [Fact]
    public void StringTimer_TemplateLiteral_Rejected() =>
        Assert.Equal("JsStringTimer", Assert.Single(New("", "setTimeout(`f()`, 1);\n")).Rule);

    [Fact]
    public void StringTimer_TemplateWithExpression_Rejected() =>
        Assert.Equal("JsStringTimer", Assert.Single(New("", "setTimeout(`f(${x})`, 1);\n")).Rule);

    [Fact]
    public void FunctionTimer_Allowed() => Assert.Empty(New("", "setTimeout(f, 10);\nsetTimeout(function () {}, 5);\n"));

    [Fact]
    public void DocumentWrite_Rejected()
    {
        Assert.Equal("JsDocumentWrite", Assert.Single(New("", "document.write(\"x\");\n")).Rule);
        Assert.Equal("JsDocumentWrite", Assert.Single(New("", "document.writeln(\"x\");\n")).Rule);
    }

    [Fact]
    public void ExecScript_Rejected() => Assert.Equal("JsExecScript", Assert.Single(New("", "window.execScript(\"x\");\n")).Rule);

    [Fact]
    public void ActiveX_Rejected() => Assert.Equal("JsActiveX", Assert.Single(New("", "var a = new ActiveXObject(\"Msxml2.XMLHTTP\");\n")).Rule);

    [Fact]
    public void FallbackText_Eval_Rejected()
    {
        var f = Assert.Single(JsSafetyGuard.NewFindingsText("var a = @@;\n", "var a = @@;\n  eval(x);\n"));
        Assert.Equal("JsEval", f.Rule);
        Assert.Equal(2, f.Line);
        Assert.Equal("JsStringTimer", Assert.Single(JsSafetyGuard.NewFindingsText("", "setTimeout('f()', 1);\n")).Rule);
    }

    [Fact]
    public void FallbackText_ExistingEval_NotCounted() =>
        Assert.Empty(JsSafetyGuard.NewFindingsText("var a = @@;\neval(x);\n", "// c\nvar a = @@;\n  eval(x);\n"));

    [Fact]
    public void EvalInString_NotCounted()
    {
        Assert.Empty(JsSafetyGuard.NewFindingsText("", "var s = \"eval(\";\n// eval(x)\n/* new Function( */\n"));
    }

    [Fact]
    public void DocumentWrite_ArgumentEdited_NotCounted()
    {
        const string original = "function f() {\n  document.write(\"<b>a</b>\");\n}\n";
        Assert.Empty(New(original, original.Replace("<b>a</b>", "<b>b</b>")));
    }

    [Fact]
    public void Eval_ArgumentEdited_NotCounted() =>
        Assert.Empty(New("function f() {\n  eval(x);\n}\n", "function f() {\n  eval(y + 1);\n}\n"));

    [Fact]
    public void StringTimer_ArgumentEdited_NotCounted() =>
        Assert.Empty(New("setTimeout(\"f()\", 10);\n", "setTimeout(\"g()\", 20);\n"));

    [Fact]
    public void DocumentWrite_SecondCallAdded_Rejected()
    {
        const string original = "function f() {\n  document.write(\"a\");\n}\n";
        var f = Assert.Single(New(original, "function f() {\n  document.write(\"a\");\n  document.write(\"b\");\n}\n"));
        Assert.Equal("JsDocumentWrite", f.Rule);
        Assert.Equal(3, f.Line);
        Assert.Contains("\"b\"", f.Text);
    }

    [Fact]
    public void DocumentWrite_EditedAndAdded_ReportsOnlyTheGrowth()
    {
        // Two calls with new text, one more call than before: one finding, the later new one.
        const string original = "document.write(\"a\");\n";
        var f = Assert.Single(New(original, "document.write(\"b\");\ndocument.write(\"c\");\n"));
        Assert.Equal(2, f.Line);
        // The added call repeats the original text: the edited one is the new text and is reported.
        var g = Assert.Single(New(original, "document.write(\"z\");\ndocument.write(\"a\");\n"));
        Assert.Equal(1, g.Line);
    }

    [Fact]
    public void FallbackText_ArgumentEdited_NotCounted()
    {
        Assert.Empty(JsSafetyGuard.NewFindingsText("var a = @@;\ndocument.write(\"a\");\n", "var a = @@;\ndocument.write(\"b\");\n"));
        Assert.Empty(JsSafetyGuard.NewFindingsText("var a = @@;\neval(x);\n", "var a = @@;\neval(y);\n"));
    }

    [Fact]
    public void FallbackText_DocumentWrite_SecondCallAdded_Rejected()
    {
        var f = Assert.Single(JsSafetyGuard.NewFindingsText("var a = @@;\ndocument.write(\"a\");\n",
            "var a = @@;\ndocument.write(\"a\");\ndocument.write(\"b\");\n"));
        Assert.Equal("JsDocumentWrite", f.Rule);
        Assert.Equal(3, f.Line);
    }
}
