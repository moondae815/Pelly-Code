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
}
