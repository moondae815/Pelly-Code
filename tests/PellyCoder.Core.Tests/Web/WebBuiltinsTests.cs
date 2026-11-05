using Acornima;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class WebBuiltinsTests
{
    private static readonly WebBuiltins B = WebBuiltins.Default;

    [Fact]
    public void Globals_Known()
    {
        foreach (var n in new[] { "window", "document", "alert", "confirm", "JSON", "Math", "Date", "parseInt", "undefined",
                     "NaN", "Infinity", "setTimeout", "location", "navigator", "console", "XMLHttpRequest",
                     "encodeURIComponent", "escape", "event" })
            Assert.True(B.IsGlobal(n), n);
        Assert.False(B.IsGlobal("Window"));
        Assert.False(B.IsGlobal("myVar"));
    }

    [Fact]
    public void EsApi_Levels()
    {
        Assert.Equal(EcmaVersion.ES2016, B.EsApiLevel(".includes"));
        Assert.Equal(EcmaVersion.ES2015, B.EsApiLevel(".startsWith"));
        Assert.Equal(EcmaVersion.ES2015, B.EsApiLevel(".endsWith"));
        Assert.Equal(EcmaVersion.ES2015, B.EsApiLevel(".find"));
        Assert.Equal(EcmaVersion.ES2015, B.EsApiLevel(".findIndex"));
        Assert.Equal(EcmaVersion.ES2017, B.EsApiLevel(".padStart"));
        Assert.Equal(EcmaVersion.ES2015, B.EsApiLevel("Array.from"));
        Assert.Equal(EcmaVersion.ES2015, B.EsApiLevel("Object.assign"));
        Assert.Equal(EcmaVersion.ES2017, B.EsApiLevel("Object.entries"));
        Assert.Equal(EcmaVersion.ES2017, B.EsApiLevel("Object.values"));
        foreach (var k in new[] { "Promise", "Map", "Set", "Symbol", "fetch" })
            Assert.Equal(EcmaVersion.ES2015, B.EsApiLevel(k));
        Assert.Null(B.EsApiLevel(".push"));
    }

    [Fact]
    public void Jquery_AddedRemoved()
    {
        Assert.Equal((new Version(1, 3), (Version?)null), B.Jquery("closest")!.Value);
        Assert.Equal((new Version(1, 3), new Version(1, 9)), B.Jquery("live")!.Value);
        Assert.Equal(new Version(1, 4, 2), B.Jquery("delegate")!.Value.Added);
        Assert.Equal(new Version(1, 4, 1), B.Jquery("parseJSON")!.Value.Added);
        Assert.Equal(new Version(1, 5), B.Jquery("Deferred")!.Value.Added);
        Assert.Equal(new Version(1, 5), B.Jquery("promise")!.Value.Added);
        Assert.Equal(new Version(1, 6), B.Jquery("prop")!.Value.Added);
        Assert.Equal(new Version(1, 7), B.Jquery("on")!.Value.Added);
        Assert.Equal(new Version(1, 7), B.Jquery("off")!.Value.Added);
        Assert.Equal(new Version(1, 8), B.Jquery("addBack")!.Value.Added);
        Assert.Equal((null, new Version(1, 9)), B.Jquery("browser")!.Value);
        Assert.Equal(new Version(3, 0), B.Jquery("size")!.Value.Removed);
        Assert.Equal(new Version(3, 0), B.Jquery("bind")!.Value.Removed);
        Assert.Equal(new Version(3, 0), B.Jquery("unbind")!.Value.Removed);
        Assert.Null(B.Jquery("noSuchThing"));
    }

    [Fact]
    public void CssProperty_Known()
    {
        Assert.True(B.IsCssProperty("color"));
        Assert.True(B.IsCssProperty("zoom"));
        Assert.True(B.IsCssProperty("transition"));
        Assert.True(B.IsCssProperty("filter"));
        Assert.False(B.IsCssProperty("behavior"));
        Assert.False(B.IsCssProperty("colour"));
    }
}
