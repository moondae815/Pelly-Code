using Acornima;
using PellyCoder.Core.Models;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class JsDocumentTests
{
    private static JsDocument P(string src, EcmaVersion v = EcmaVersion.Latest) => JsDocument.Parse(src, v, false);

    [Fact]
    public void Functions_AllShapes()
    {
        var d = P("function a() {}\nvar b = function () {};\nconst c = () => 1;\nvar O = { d: function () {} };\nO.e = function () {};\nO.prototype.f = function () {};\n");
        Assert.Null(d.Error);
        Assert.Equal(new[] { "a", "b", "c", "O.d", "O.e", "O.prototype.f" }, d.Functions.Select(f => f.QualifiedName).ToArray());
        Assert.Null(d.Functions[0].Owner);
        Assert.Equal("O.prototype", d.Functions[5].Owner);
    }

    [Fact]
    public void Functions_AnonymousExcluded()
    {
        var d = P("$(function () { var x = 1; });\n");
        Assert.Empty(d.Functions);
    }

    [Fact]
    public void Functions_LeadingCommentIncluded()
    {
        var d = P("var z = 1;\n// first\n/* second */\nfunction a(p, q) {\n  return p;\n}\n");
        var f = Assert.Single(d.Functions);
        Assert.Equal(2, f.StartLine);
        Assert.Equal(4, f.DeclLine);
        Assert.Equal(6, f.EndLine);
        Assert.Equal("function a(p, q) {", f.Declaration);
        Assert.Equal(new[] { "p", "q" }, f.Parameters.ToArray());
    }

    [Fact]
    public void FreeNames_LocalAndParamResolved()
    {
        var d = P("var g = 1;\nfunction f(p) { var l = p; try { } catch (e) { l = e; } return l + g + missing; }\n");
        var free = d.FreeNames.Select(n => n.Name).ToArray();
        Assert.Equal(new[] { "missing" }, free);
        Assert.Equal("f", d.FreeNames[0].Function);
        Assert.Equal(2, d.FreeNames[0].Line);
    }

    [Fact]
    public void FreeNames_MemberAndKeyExcluded()
    {
        var d = P("var o = { k: 1, 'q': 2 };\nfunction f() { return o.zz + arguments.length; }\nlbl: for (;;) { break lbl; }\n");
        Assert.Empty(d.FreeNames);
    }

    [Fact]
    public void FreeNames_HoistedFunctionResolved()
    {
        var d = P("use();\nfunction use() { return helper(); }\nfunction helper() {}\n");
        Assert.Empty(d.FreeNames);
        Assert.Contains("use", d.TopLevelNames);
        Assert.Contains("helper", d.TopLevelNames);
    }

    [Fact]
    public void Calls_CalleePaths()
    {
        var d = P("eval(x);\nwindow.setTimeout(\"f()\", 1);\nsetTimeout(\"a\" + b, 1);\n$(\"#a\").on(\"click\", f);\nvar a = new ActiveXObject(\"x\");\n");
        Assert.Equal("eval", d.Calls[0].Callee);
        Assert.Equal(JsArgKind.Other, d.Calls[0].FirstArg);
        Assert.Equal("window.setTimeout", d.Calls[1].Callee);
        Assert.Equal(JsArgKind.StringLiteral, d.Calls[1].FirstArg);
        Assert.Equal(JsArgKind.StringConcat, d.Calls.First(c => c.Callee == "setTimeout").FirstArg);
        Assert.Contains(d.Calls, c => c.Callee == ".on" && c.Line == 4);
        var n = d.Calls.Single(c => c.Callee == "ActiveXObject");
        Assert.True(n.IsNew);
        Assert.Equal("new ActiveXObject(\"x\")", n.Text);
    }

    [Fact]
    public void CutsStatement_Partial_True()
    {
        var d = P("function f() {\n  a();\n  b();\n}\n");
        Assert.True(d.CutsStatement(new TextSpanInfo(1, 2)));
        Assert.True(d.CutsStatement(new TextSpanInfo(3, 4)));
        Assert.True(d.CutsStatement(new TextSpanInfo(2, 4)));
    }

    [Fact]
    public void CutsStatement_InsideFunctionBody_False()
    {
        var d = P("function f() {\n  a();\n  b();\n}\n");
        Assert.False(d.CutsStatement(new TextSpanInfo(2, 3)));
        Assert.False(d.CutsStatement(new TextSpanInfo(1, 4)));
    }

    [Fact]
    public void Error_ColumnOneBased()
    {
        var d = P("var f = (a) => a+1;\n", EcmaVersion.ES5);
        Assert.NotNull(d.Error);
        Assert.Equal(1, d.Error!.Line);
        Assert.Equal(14, d.Error.Column);
        Assert.Empty(d.Functions);
        Assert.Empty(d.Calls);
    }
}
