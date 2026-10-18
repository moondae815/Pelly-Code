using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public sealed class WorkCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-eval-copy-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    [Fact]
    public void Copy_ExcludesBinObjPelly()
    {
        var from = Path.Combine(_root, "from");
        foreach (var dir in new[] { "Shop/bin/Debug", "Shop/obj", ".pelly/runs", "Shop/Services", "Shop/Services/obj" })
            Directory.CreateDirectory(Path.Combine(from, dir));
        File.WriteAllText(Path.Combine(from, "Shop", "Shop.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(from, "Shop", "Services", "A.cs"), "class A {}");
        File.WriteAllText(Path.Combine(from, "Shop", "bin", "Debug", "Shop.dll"), "x");
        File.WriteAllText(Path.Combine(from, ".pelly", "runs", "x.json"), "{}");

        var to = Path.Combine(_root, "to");
        WorkCopy.CopyDirectory(from, to);

        Assert.True(File.Exists(Path.Combine(to, "Shop", "Shop.csproj")));
        Assert.True(File.Exists(Path.Combine(to, "Shop", "Services", "A.cs")));
        Assert.False(Directory.Exists(Path.Combine(to, "Shop", "bin")));
        Assert.False(Directory.Exists(Path.Combine(to, "Shop", "obj")));
        Assert.False(Directory.Exists(Path.Combine(to, "Shop", "Services", "obj")));
        Assert.False(Directory.Exists(Path.Combine(to, ".pelly")));
    }
}
