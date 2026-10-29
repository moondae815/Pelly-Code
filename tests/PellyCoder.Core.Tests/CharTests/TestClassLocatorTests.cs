using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CharTests;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class TestClassLocatorTests
{
    private const string Csproj = "/w/Tests/Tests.csproj";

    private static MockFileSystem Fs(string csproj = "<Project Sdk=\"Microsoft.NET.Sdk\" />")
    {
        var fs = new MockFileSystem();
        fs.AddFile(Csproj, new MockFileData(csproj));
        return fs;
    }

    private static string Cls(string ns, string cls) => $"namespace {ns}\n{{\n    public class {cls} {{ }}\n}}\n";

    [Fact]
    public void Find_ModernOrderService_ExistingClass()
    {
        var fs = new FileSystem();
        var csproj = CorpusLocation.Get("Modern/Shop.Api.Tests/Shop.Api.Tests.csproj");

        var loc = TestClassLocator.Find(fs, csproj, "OrderService", LanguageVersion.Latest);

        Assert.Null(loc.Error);
        Assert.False(loc.IsNew);
        Assert.Equal("OrderServiceTests", loc.ClassName);
        Assert.Equal(Path.GetFullPath(CorpusLocation.Get("Modern/Shop.Api.Tests/OrderServiceTests.cs")), Path.GetFullPath(loc.FilePath));
        Assert.Equal("Shop.Api.Tests", loc.Namespace);
    }

    [Fact]
    public void Find_NoClass_NewFileWithMajorityNamespace()
    {
        var fs = Fs("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Root.Ns</RootNamespace></PropertyGroup></Project>");
        fs.AddFile("/w/Tests/A.cs", new MockFileData(Cls("Maj.Ns", "ATests")));
        fs.AddFile("/w/Tests/Sub/B.cs", new MockFileData(Cls("Maj.Ns", "BTests")));
        fs.AddFile("/w/Tests/C.cs", new MockFileData(Cls("Other.Ns", "CTests")));

        var loc = TestClassLocator.Find(fs, Csproj, "Order", LanguageVersion.Latest);

        Assert.Null(loc.Error);
        Assert.True(loc.IsNew);
        Assert.Equal("OrderTests", loc.ClassName);
        Assert.Equal(fs.Path.Combine("/w/Tests", "OrderTests.cs"), loc.FilePath);
        Assert.Equal("Maj.Ns", loc.Namespace);
    }

    [Fact]
    public void Find_NoClass_NoFiles_FallsBackToRootNamespaceThenAssemblyName()
    {
        var withRoot = Fs("<Project><PropertyGroup><RootNamespace>Root.Ns</RootNamespace><AssemblyName>Asm</AssemblyName></PropertyGroup></Project>");
        Assert.Equal("Root.Ns", TestClassLocator.Find(withRoot, Csproj, "Order", LanguageVersion.Latest).Namespace);

        var withAsm = Fs("<Project><PropertyGroup><AssemblyName>Asm.Name</AssemblyName></PropertyGroup></Project>");
        Assert.Equal("Asm.Name", TestClassLocator.Find(withAsm, Csproj, "Order", LanguageVersion.Latest).Namespace);

        var bare = Fs("<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Assert.Equal("Tests", TestClassLocator.Find(bare, Csproj, "Order", LanguageVersion.Latest).Namespace);
    }

    [Fact]
    public void Find_FileExistsWithOtherClass_Error()
    {
        var fs = Fs();
        fs.AddFile("/w/Tests/OrderTests.cs", new MockFileData(Cls("N", "SomethingElse")));

        var loc = TestClassLocator.Find(fs, Csproj, "Order", LanguageVersion.Latest);

        Assert.NotNull(loc.Error);
        Assert.StartsWith("같은 이름의 파일이 있지만 OrderTests 클래스가 아닙니다: ", loc.Error);
        Assert.Contains("OrderTests.cs", loc.Error);
    }

    [Fact]
    public void Find_SkipsBinObj()
    {
        var fs = Fs();
        fs.AddFile("/w/Tests/bin/Debug/OrderTests.cs", new MockFileData(Cls("Bin.Ns", "OrderTests")));
        fs.AddFile("/w/Tests/obj/X.cs", new MockFileData(Cls("Obj.Ns", "XTests")));

        var loc = TestClassLocator.Find(fs, Csproj, "Order", LanguageVersion.Latest);

        Assert.Null(loc.Error);
        Assert.True(loc.IsNew);
        Assert.NotEqual("Bin.Ns", loc.Namespace);
        Assert.NotEqual("Obj.Ns", loc.Namespace);
    }

    [Fact]
    public void Find_ClassInDifferentFileName_UsesThatFile()
    {
        var fs = Fs();
        fs.AddFile("/w/Tests/Orders.cs", new MockFileData(Cls("N", "OrderTests")));

        var loc = TestClassLocator.Find(fs, Csproj, "Order", LanguageVersion.Latest);

        Assert.False(loc.IsNew);
        Assert.EndsWith("Orders.cs", loc.FilePath);
        Assert.Equal("N", loc.Namespace);
    }

    [Fact]
    public void Find_PartialClass_PrefersFileNamedAfterClass()
    {
        var fs = Fs();
        var partial = "namespace N\n{\n    public partial class OrderTests { }\n}\n";
        fs.AddFile("/w/Tests/A.Part.cs", new MockFileData(partial));
        fs.AddFile("/w/Tests/OrderTests.cs", new MockFileData(partial));
        Assert.EndsWith("OrderTests.cs", TestClassLocator.Find(fs, Csproj, "Order", LanguageVersion.Latest).FilePath);

        var fs2 = Fs();
        fs2.AddFile("/w/Tests/A.Part.cs", new MockFileData(partial));
        fs2.AddFile("/w/Tests/B.Part.cs", new MockFileData(partial));
        Assert.EndsWith("A.Part.cs", TestClassLocator.Find(fs2, Csproj, "Order", LanguageVersion.Latest).FilePath);
    }
}
