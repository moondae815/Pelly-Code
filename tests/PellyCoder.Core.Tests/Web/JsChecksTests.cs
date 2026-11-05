using Acornima;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class JsChecksTests
{
    private const string Plain = "function fnSearch(a, b) {\n  return a + b;\n}\n";

    [Fact]
    public void Syntax_Valid_NoErrors()
    {
        var v = JsChecks.Syntax(WebTestState.Js(Plain), "a.js", Plain.Replace("a + b", "a - b"));
        Assert.Empty(v.Errors);
        Assert.False(v.LevelExceeded);
        Assert.NotNull(v.Patched);
    }

    [Fact]
    public void Syntax_Broken_DiagnosticWithColumn()
    {
        var v = JsChecks.Syntax(WebTestState.Js(Plain), "a.js", "function fnSearch(a, b) {\n  return a + ;\n}\n");
        var d = Assert.Single(v.Errors);
        Assert.Equal("a.js", d.File);
        Assert.Equal(2, d.Line);
        Assert.True(d.Column > 1);
        Assert.Equal("JS", d.Code);
        Assert.Equal("error", d.Severity);
        Assert.Null(v.Patched);
    }

    [Fact]
    public void Level_ArrowInEs5_Exceeded_FeatureNamed()
    {
        var state = WebTestState.Js(Plain);
        Assert.Equal(EcmaVersion.ES5, state.TargetLevel);
        var v = JsChecks.Syntax(state, "a.js", "function fnSearch(a, b) {\n  var f = (x) => x;\n  return f(a);\n}\n");
        Assert.True(v.LevelExceeded);
        Assert.Empty(v.Errors);
        Assert.False(string.IsNullOrEmpty(v.Feature));
        Assert.Equal(2, v.FeatureLine);
    }

    [Fact]
    public void Level_ArrowInEs2015_Ok()
    {
        var state = WebTestState.Js("function fnSearch(a, b) {\n  let z = 1;\n  return a + b;\n}\n");
        Assert.Equal(EcmaVersion.ES2015, state.TargetLevel);
        var v = JsChecks.Syntax(state, "a.js", "function fnSearch(a, b) {\n  let f = (x) => x;\n  return f(a);\n}\n");
        Assert.False(v.LevelExceeded);
        Assert.Empty(v.Errors);
    }

    [Fact]
    public void Module_ParsedAsModule()
    {
        const string module = "import { x } from \"./x.js\";\nfunction fnSearch(a, b) {\n  return x;\n}\n";
        var state = WebTestState.Js(module);
        Assert.True(state.Module);
        var v = JsChecks.Syntax(state, "a.mjs", module.Replace("return x", "return x + a"));
        Assert.Empty(v.Errors);
        Assert.True(v.Patched!.Module);
    }

    [Fact]
    public void Fallback_NewUnbalancedBrace_Reported()
    {
        const string original = "var a = @@;\nfunction fnSearch(a, b) {\n  return a;\n}\n";
        var state = WebTestState.Js(original);
        Assert.True(state.Fallback);
        var v = JsChecks.Syntax(state, "a.js", "var a = @@;\nfunction fnSearch(a, b) {\n  if (a) {\n  return a;\n}\n");
        var d = Assert.Single(v.Errors);
        Assert.Equal("JS901", d.Code);
        Assert.Null(v.Patched);
    }

    [Fact]
    public void Fallback_ExistingImbalance_NotCounted()
    {
        const string original = "var a = @@;\nif (x) {\nfunction fnSearch(a, b) {\n  return a;\n}\n";
        var state = WebTestState.Js(original);
        Assert.True(state.Fallback);
        var v = JsChecks.Syntax(state, "a.js", original.Replace("return a;", "return b;"));
        Assert.Empty(v.Errors);
    }

    [Fact]
    public void Shape_Renamed()
    {
        var state = WebTestState.Js(Plain, "fnSearch");
        var patched = "function fnFind(a, b) {\n  return a;\n}\n";
        var msg = JsChecks.ShapeViolation(state, JsDocument.Parse(patched, EcmaVersion.ES5, false), patched);
        Assert.Equal("함수 fnSearch를 찾을 수 없습니다(이름을 바꾸지 마십시오)", msg);
    }

    [Fact]
    public void Shape_ParamAdded()
    {
        var state = WebTestState.Js(Plain, "fnSearch");
        var patched = "function fnSearch(a, b, c) {\n  return a;\n}\n";
        var msg = JsChecks.ShapeViolation(state, JsDocument.Parse(patched, EcmaVersion.ES5, false), patched);
        Assert.Equal("함수 fnSearch의 매개변수를 바꿀 수 없습니다: (a, b) → (a, b, c)", msg);
    }

    [Fact]
    public void Shape_NewTopLevel()
    {
        var state = WebTestState.Js(Plain, "fnSearch");
        var patched = "var x = 1;\nfunction fnSearch(a, b) {\n  return a;\n}\n";
        var msg = JsChecks.ShapeViolation(state, JsDocument.Parse(patched, EcmaVersion.ES5, false), patched);
        Assert.Equal("함수 밖에 새 최상위 선언을 만들 수 없습니다: x", msg);
    }

    [Fact]
    public void Shape_Unchanged_Null()
    {
        var state = WebTestState.Js(Plain, "fnSearch");
        var patched = "function fnSearch(a, b) {\n  var t = 1;\n  return a + t;\n}\n";
        Assert.Null(JsChecks.ShapeViolation(state, JsDocument.Parse(patched, EcmaVersion.ES5, false), patched));
    }

    [Fact]
    public void Shape_LinesTarget_Null()
    {
        var state = WebTestState.Js(Plain);
        var patched = "var q = 1;\n";
        Assert.Null(JsChecks.ShapeViolation(state, JsDocument.Parse(patched, EcmaVersion.ES5, false), patched));
    }

    [Fact]
    public void Shape_Fallback_ParamsCompared()
    {
        const string original = "var a = @@;\nfunction fnSearch(a, b) {\n  return a;\n}\n";
        var state = WebTestState.Js(original, "fnSearch");
        Assert.True(state.Fallback);
        var msg = JsChecks.ShapeViolation(state, null, original.Replace("(a, b)", "(a, b, c)"));
        Assert.Equal("함수 fnSearch의 매개변수를 바꿀 수 없습니다: (a, b) → (a, b, c)", msg);
        Assert.Null(JsChecks.ShapeViolation(state, null, original.Replace("return a;", "return b;")));
    }

    private const string TwoInits =
        "function A() {\n  this.init = function (x) {\n    return x;\n  };\n}\n"
        + "function B() {\n  this.init = function (y) {\n    return y;\n  };\n}\n";

    [Fact]
    public void Shape_DuplicateName_BodyEdit_Null()
    {
        var state = WebTestState.Js(TwoInits, "this.init", occurrence: 1);
        Assert.Equal(7, state.Function!.DeclLine);
        var patched = TwoInits.Replace("return y;", "return y + 1;");
        Assert.Null(JsChecks.ShapeViolation(state, JsDocument.Parse(patched, EcmaVersion.ES5, false), patched));
    }

    [Fact]
    public void Shape_DuplicateName_TargetParamChanged_Rejected()
    {
        var state = WebTestState.Js(TwoInits, "this.init", occurrence: 1);
        var patched = TwoInits.Replace("function (y)", "function (y, z)");
        var msg = JsChecks.ShapeViolation(state, JsDocument.Parse(patched, EcmaVersion.ES5, false), patched);
        Assert.Equal("함수 this.init의 매개변수를 바꿀 수 없습니다: (y) → (y, z)", msg);
    }

    [Fact]
    public void Shape_DuplicateName_OneRemoved_Rejected()
    {
        var state = WebTestState.Js(TwoInits, "this.init", occurrence: 1);
        var patched = TwoInits.Replace("this.init = function (x)", "this.start = function (x)");
        Assert.Equal("함수 this.init를 찾을 수 없습니다(이름을 바꾸지 마십시오)",
            JsChecks.ShapeViolation(state, JsDocument.Parse(patched, EcmaVersion.ES5, false), patched));
    }

    [Fact]
    public void Shape_DeclaredTwice_SecondEdited_Null()
    {
        const string original = "function f(a) {\n  return a;\n}\nfunction f(a) {\n  return a * 2;\n}\n";
        var state = WebTestState.Js(original, "f", occurrence: 1);
        var patched = original.Replace("a * 2", "a * 3");
        Assert.Null(JsChecks.ShapeViolation(state, JsDocument.Parse(patched, EcmaVersion.ES5, false), patched));
        var added = original.Replace("function f(a) {\n  return a * 2", "function f(a, b) {\n  return a * 2");
        Assert.Equal("함수 f의 매개변수를 바꿀 수 없습니다: (a) → (a, b)",
            JsChecks.ShapeViolation(state, JsDocument.Parse(added, EcmaVersion.ES5, false), added));
    }

    [Fact]
    public void Shape_Fallback_DuplicateName()
    {
        const string original = "var q = @@;\nfunction f(a) {\n  return a;\n}\nfunction f(b) {\n  return b;\n}\n";
        var state = WebTestState.Js(original, "f", occurrence: 1);
        Assert.True(state.Fallback);
        Assert.Null(JsChecks.ShapeViolation(state, null, original.Replace("return b;", "return b + 1;")));
        Assert.Equal("함수 f의 매개변수를 바꿀 수 없습니다: (b) → (b, c)",
            JsChecks.ShapeViolation(state, null, original.Replace("function f(b)", "function f(b, c)")));
        Assert.Equal("함수 f를 찾을 수 없습니다(이름을 바꾸지 마십시오)",
            JsChecks.ShapeViolation(state, null, original + "function f(c) {\n}\n"));
    }
}
