using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.Testing;

public class TestDeclarationHashTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private const string Source = """
        namespace Shop.Tests;
        class OrderTests
        {
            private int _seed = 1;

            [Fact]
            public void Cancel_Throws() { Assert.Equal(1, _seed); }

            [Fact]
            public void Other() { }
        }
        """;

    private static MockFileSystem Fs(string text)
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("Shop.Tests/OrderTests.cs"), new MockFileData(text));
        return fs;
    }

    [Fact]
    public void Of_SameDeclaration_SameHash()
    {
        var hash = TestDeclarationHash.Of(Fs(Source), Root, "Cancel_Throws");

        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, TestDeclarationHash.Of(Fs(Source), Root, "Cancel_Throws"));
        Assert.Equal(hash, TestDeclarationHash.Of(Fs(Source), Root, "OrderTests.Cancel_Throws"));
    }

    [Fact]
    public void Of_BodyChanged_DifferentHash()
    {
        var before = TestDeclarationHash.Of(Fs(Source), Root, "Cancel_Throws");

        var after = TestDeclarationHash.Of(Fs(Source.Replace("Assert.Equal(1, _seed)", "Assert.Equal(2, _seed)")), Root, "Cancel_Throws");

        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Of_OtherMemberInFileChanged_SameHash()
    {
        var before = TestDeclarationHash.Of(Fs(Source), Root, "Cancel_Throws");
        var changed = Source.Replace("_seed = 1", "_seed = 2").Replace("public void Other() { }", "public void Other() { var x = 1; }");

        Assert.Equal(before, TestDeclarationHash.Of(Fs(changed), Root, "Cancel_Throws"));
    }

    [Fact]
    public void Of_NotFound_Null()
    {
        Assert.Null(TestDeclarationHash.Of(Fs(Source), Root, "Missing_Test"));
        Assert.Null(TestDeclarationHash.Of(Fs(Source.Replace("[Fact]\n    public void Cancel_Throws", "public void Cancel_Throws")), Root, "Cancel_Throws"));
    }

    [Fact]
    public void Of_MatchesTargetFileHashOfTestFirstState()
    {
        var fs = Fs(Source);
        var file = P("Shop.Tests/OrderTests.cs");

        Assert.Equal(PellyCoder.Core.Fsm.TestFirstState.HashOf(fs, file, file, ["Shop.Tests.OrderTests.Cancel_Throws"]),
            TestDeclarationHash.Of(fs, Root, "Cancel_Throws"));
    }
}
