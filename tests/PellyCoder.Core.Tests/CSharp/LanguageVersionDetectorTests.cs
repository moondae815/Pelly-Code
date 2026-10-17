using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CSharp;

public sealed class LanguageVersionDetectorTests
{
    private static string Sdk(string body) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{body}</PropertyGroup></Project>";

    private static LanguageTarget Detect(string csproj)
    {
        var path = TestPaths.P(@"C:\p\App.csproj");
        var fs = new MockFileSystem();
        fs.AddFile(path, new MockFileData(csproj));
        return LanguageVersionDetector.Detect(fs, path);
    }

    [Theory]
    [InlineData("<TargetFramework>net8.0</TargetFramework>", LanguageVersion.CSharp12, "C# 12, net8.0")]
    [InlineData("<TargetFramework>net48</TargetFramework>", LanguageVersion.CSharp7_3, "C# 7.3, .NET Framework 4.8")]
    [InlineData("<TargetFramework>net462</TargetFramework>", LanguageVersion.CSharp7_3, "C# 7.3, .NET Framework 4.6.2")]
    [InlineData("<TargetFramework>net45</TargetFramework>", LanguageVersion.CSharp7_3, "C# 7.3, .NET Framework 4.5")]
    [InlineData("<TargetFramework>netstandard2.0</TargetFramework>", LanguageVersion.CSharp7_3, "C# 7.3, netstandard2.0")]
    [InlineData("<TargetFramework>netstandard2.1</TargetFramework>", LanguageVersion.CSharp8, "C# 8, netstandard2.1")]
    [InlineData("<TargetFramework>netcoreapp2.1</TargetFramework>", LanguageVersion.CSharp7_3, "C# 7.3, netcoreapp2.1")]
    [InlineData("<TargetFramework>netcoreapp3.1</TargetFramework>", LanguageVersion.CSharp8, "C# 8, netcoreapp3.1")]
    [InlineData("<TargetFramework>net5.0</TargetFramework>", LanguageVersion.CSharp9, "C# 9, net5.0")]
    [InlineData("<TargetFramework>net6.0</TargetFramework>", LanguageVersion.CSharp10, "C# 10, net6.0")]
    [InlineData("<TargetFramework>net7.0</TargetFramework>", LanguageVersion.CSharp11, "C# 11, net7.0")]
    [InlineData("<TargetFramework>net9.0</TargetFramework>", LanguageVersion.CSharp13, "C# 13, net9.0")]
    [InlineData("<TargetFramework>net10.0</TargetFramework>", LanguageVersion.CSharp14, "C# 14, net10.0")]
    [InlineData("<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>", LanguageVersion.CSharp7_3, "C# 7.3, netstandard2.0")]
    [InlineData("<LangVersion>10</LangVersion><TargetFramework>net48</TargetFramework>", LanguageVersion.CSharp10, "C# 10, .NET Framework 4.8")]
    [InlineData("<LangVersion>7.3</LangVersion><TargetFramework>net8.0</TargetFramework>", LanguageVersion.CSharp7_3, "C# 7.3, net8.0")]
    public void Detect_SdkStyle(string body, LanguageVersion expected, string hint)
    {
        var target = Detect(Sdk(body));

        Assert.Equal(expected, target.Version);
        Assert.Equal(hint, target.Hint);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("preview")]
    [InlineData("latestMajor")]
    [InlineData("default")]
    public void Detect_LangVersionLatest_UsesTfmDefault(string value)
    {
        var target = Detect(Sdk($"<LangVersion>{value}</LangVersion><TargetFramework>net8.0</TargetFramework>"));

        Assert.Equal(LanguageVersion.CSharp12, target.Version);
        Assert.Equal("C# 12, net8.0", target.Hint);
    }

    [Fact]
    public void Detect_LegacyCsproj_V45_Is73()
    {
        var path = CorpusLocation.Get("Legacy/Shop.Data/Shop.Data.csproj");

        var target = LanguageVersionDetector.Detect(new FileSystem(), path);

        Assert.Equal(LanguageVersion.CSharp7_3, target.Version);
        Assert.Equal("C# 7.3, .NET Framework 4.5", target.Hint);
    }

    [Fact]
    public void Detect_LegacyCsproj_WithLangVersion_UsesIt()
    {
        var xml = "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup>" +
                  "<TargetFrameworkVersion>v4.6.1</TargetFrameworkVersion><LangVersion>8.0</LangVersion></PropertyGroup></Project>";

        var target = Detect(xml);

        Assert.Equal(LanguageVersion.CSharp8, target.Version);
        Assert.Equal("C# 8, .NET Framework 4.6.1", target.Hint);
    }

    [Fact]
    public void Detect_NoCsproj_IsDefaultLatest()
    {
        var target = LanguageVersionDetector.Detect(new MockFileSystem(), null);

        Assert.Equal(LanguageVersion.Default, target.Version);
        Assert.Equal("C# (latest)", target.Hint);
    }

    [Fact]
    public void Detect_UnknownTfm_IsDefaultLatestWithTfm()
    {
        var target = Detect(Sdk("<TargetFramework>net99.0</TargetFramework>"));

        Assert.Equal(LanguageVersion.Default, target.Version);
        Assert.Equal("C# (latest), net99.0", target.Hint);
    }

    private static string Props(string inner) => $"<Project>{inner}</Project>";

    private static LanguageTarget DetectWith(string csproj, params (string Path, string Content)[] files)
    {
        var fs = new MockFileSystem();
        var csprojPath = TestPaths.P(@"C:\r\src\App\App.csproj");
        fs.AddFile(csprojPath, new MockFileData(csproj));
        foreach (var (path, content) in files)
            fs.AddFile(TestPaths.P(path), new MockFileData(content));
        return LanguageVersionDetector.Detect(fs, csprojPath);
    }

    [Fact]
    public void DirectoryBuildProps_LangVersion_Applies()
    {
        var t = DetectWith(Sdk("<TargetFramework>net48</TargetFramework>"),
            (@"C:\r\Directory.Build.props", Props("<PropertyGroup><LangVersion>10</LangVersion></PropertyGroup>")));

        Assert.Equal(LanguageVersion.CSharp10, t.Version);
        Assert.Equal("C# 10, .NET Framework 4.8", t.Hint);
    }

    [Fact]
    public void Csproj_Overrides_DirectoryBuildProps()
    {
        var t = DetectWith(Sdk("<LangVersion>9</LangVersion><TargetFramework>net48</TargetFramework>"),
            (@"C:\r\Directory.Build.props", Props("<PropertyGroup><LangVersion>10</LangVersion></PropertyGroup>")));

        Assert.Equal(LanguageVersion.CSharp9, t.Version);
    }

    [Fact]
    public void NearestPropsOnly()
    {
        var t = DetectWith(Sdk("<TargetFramework>net48</TargetFramework>"),
            (@"C:\r\Directory.Build.props", Props("<PropertyGroup><LangVersion>11</LangVersion></PropertyGroup>")),
            (@"C:\r\src\Directory.Build.props", Props("<PropertyGroup><LangVersion>10</LangVersion></PropertyGroup>")));

        Assert.Equal(LanguageVersion.CSharp10, t.Version);
    }

    [Fact]
    public void ConditionedPropertyGroup_IsIgnored()
    {
        var t = DetectWith(Sdk("<TargetFramework>net8.0</TargetFramework>"),
            (@"C:\r\Directory.Build.props",
                Props("<PropertyGroup Condition=\"'$(Configuration)'=='Debug'\"><LangVersion>7.3</LangVersion></PropertyGroup>")));

        Assert.Equal(LanguageVersion.CSharp12, t.Version);
        Assert.Equal("C# 12, net8.0", t.Hint);
    }

    [Fact]
    public void ConditionedProperty_IsIgnored()
    {
        var t = DetectWith(Sdk("<TargetFramework>net8.0</TargetFramework>"),
            (@"C:\r\Directory.Build.props",
                Props("<PropertyGroup><LangVersion Condition=\"'$(X)'=='1'\">7.3</LangVersion></PropertyGroup>")));

        Assert.Equal(LanguageVersion.CSharp12, t.Version);
    }

    [Fact]
    public void PropertyOutsidePropertyGroup_IsIgnored()
    {
        var t = DetectWith(Sdk("<TargetFramework>net8.0</TargetFramework>"),
            (@"C:\r\Directory.Build.props", Props("<ItemGroup><LangVersion>7.3</LangVersion></ItemGroup>")));

        Assert.Equal(LanguageVersion.CSharp12, t.Version);
    }

    [Fact]
    public void LegacyCsproj_WithPropsLangVersion_Applies()
    {
        const string legacy = "<Project ToolsVersion=\"15.0\"><PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup></Project>";
        var t = DetectWith(legacy,
            (@"C:\r\Directory.Build.props", Props("<PropertyGroup><LangVersion>8</LangVersion></PropertyGroup>")));

        Assert.Equal(LanguageVersion.CSharp8, t.Version);
        Assert.Equal("C# 8, .NET Framework 4.8", t.Hint);
    }

    [Fact]
    public void MalformedProps_Throws_NamingThePath()
    {
        var ex = Assert.Throws<System.Xml.XmlException>(() => DetectWith(Sdk("<TargetFramework>net8.0</TargetFramework>"),
            (@"C:\r\Directory.Build.props", "<Project><PropertyGroup>")));

        Assert.Contains("Directory.Build.props", ex.Message);
    }
}
