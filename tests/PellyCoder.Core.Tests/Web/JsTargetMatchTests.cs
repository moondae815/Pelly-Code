using Acornima;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public sealed class JsTargetMatchTests
{
    private static IReadOnlyList<JsFunction> Fns(string text) => JsDocument.Parse(text, EcmaVersion.ES5, false).Functions;

    private const string Two = "function a(x) {}\nfunction twice() { return 1; }\nfunction twice() { return 2; }\n";

    [Fact]
    public void SameName_Found()
    {
        var originals = Fns(Two);
        var patched = Fns(Two.Replace("a(x)", "a(x, y)"));
        var found = JsTargetMatch.Find(originals, patched, originals[0], false);
        Assert.Equal(["x", "y"], found!.Parameters);
    }

    [Fact]
    public void SameNameTwice_PositionKept()
    {
        var originals = Fns(Two);
        var patched = Fns(Two.Replace("return 2;", "return 3;"));
        var found = JsTargetMatch.Find(originals, patched, originals[2], false);
        Assert.Equal(3, found!.DeclLine);
    }

    [Fact]
    public void Renamed_TopLevel_FoundWhenAllowed()
    {
        var originals = Fns(Two);
        var found = JsTargetMatch.Find(originals, Fns(Two.Replace("function a(", "function b(")), originals[0], true);
        Assert.Equal("b", found!.Name);
    }

    [Fact]
    public void Renamed_NotAllowed_Null()
    {
        var originals = Fns(Two);
        Assert.Null(JsTargetMatch.Find(originals, Fns(Two.Replace("function a(", "function b(")), originals[0], false));
    }

    [Fact]
    public void Renamed_TwoNewNames_Null()
    {
        var originals = Fns(Two);
        var patched = Fns(Two.Replace("function a(x) {}", "function b(x) {}\nfunction c() {}"));
        Assert.Null(JsTargetMatch.Find(originals, patched, originals[0], true));
    }

    [Fact]
    public void Renamed_OwnerMethod_SameOwnerOnly()
    {
        const string text = "var Obj = { a: function () {} };\nvar Other = { z: function () {} };\n";
        var originals = Fns(text);
        var target = originals.Single(f => f.QualifiedName == "Obj.a");
        Assert.Equal("Obj.b", JsTargetMatch.Find(originals, Fns(text.Replace("{ a:", "{ b:")), target, true)!.QualifiedName);
        var moved = Fns("var Obj = { };\nvar Other = { z: function () {}, b: function () {} };\n");
        Assert.Null(JsTargetMatch.Find(originals, moved, target, true));
    }

    [Fact]
    public void Signature_Display() =>
        Assert.Equal("fnSearch(f, page)", new JsSignature("fnSearch", ["f", "page"]).Display);
}
