using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.CharTests;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class TestFrameworkTests
{
    private const string Csproj = "/repo/T/T.csproj";

    private static MockFileSystem WithPackages(params string[] includes)
    {
        var items = string.Concat(includes.Select(i => $"<PackageReference Include=\"{i}\" Version=\"1.0.0\" />"));
        return new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Csproj] = new($"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{items}</ItemGroup></Project>"),
        });
    }

    [Fact]
    public void Detect_ModernCorpus_IsXunitV3()
    {
        var path = CorpusLocation.Get("Modern/Shop.Api.Tests/Shop.Api.Tests.csproj");
        var (framework, error) = TestFrameworks.Detect(new FileSystem(), path);
        Assert.Null(error);
        Assert.Equal(TestFramework.XunitV3, framework);
    }

    [Fact]
    public void Detect_LegacyCorpus_IsMsTestFromPackagesConfig()
    {
        var path = CorpusLocation.Get("Legacy/Shop.Tests/Shop.Tests.csproj");
        var (framework, error) = TestFrameworks.Detect(new FileSystem(), path);
        Assert.Null(error);
        Assert.Equal(TestFramework.MsTest, framework);
    }

    [Fact]
    public void Detect_XunitV2PackageReference()
    {
        var (framework, error) = TestFrameworks.Detect(WithPackages("xunit", "xunit.runner.visualstudio"), Csproj);
        Assert.Null(error);
        Assert.Equal(TestFramework.XunitV2, framework);
    }

    [Fact]
    public void Detect_NUnit()
    {
        var (framework, error) = TestFrameworks.Detect(WithPackages("NUnit", "NUnit3TestAdapter"), Csproj);
        Assert.Null(error);
        Assert.Equal(TestFramework.NUnit, framework);
    }

    [Fact]
    public void Detect_Two_Error()
    {
        var (framework, error) = TestFrameworks.Detect(WithPackages("xunit", "NUnit"), Csproj);
        Assert.Null(framework);
        Assert.StartsWith("테스트 프레임워크가 여럿입니다: ", error);
    }

    [Fact]
    public void Detect_None_Error()
    {
        var (framework, error) = TestFrameworks.Detect(WithPackages("Newtonsoft.Json"), Csproj);
        Assert.Null(framework);
        Assert.Equal("테스트 프레임워크를 찾을 수 없습니다", error);
    }

    [Fact]
    public void Info_MsTest_HasTestClassAttribute()
    {
        var info = TestFrameworks.Info(TestFramework.MsTest);
        Assert.Equal("TestMethod", info.TestAttribute);
        Assert.Equal("TestClass", info.ClassAttribute);
        Assert.Equal(["Microsoft.VisualStudio.TestTools.UnitTesting"], info.Usings);
    }

    [Fact]
    public void Info_DataTestAttributes_PerFramework()
    {
        Assert.Equal(["Theory"], TestFrameworks.Info(TestFramework.XunitV2).DataTestAttributes);
        Assert.Equal(["Theory"], TestFrameworks.Info(TestFramework.XunitV3).DataTestAttributes);
        Assert.Equal(["DataTestMethod"], TestFrameworks.Info(TestFramework.MsTest).DataTestAttributes);
        Assert.Equal(["TestCase", "TestCaseSource"], TestFrameworks.Info(TestFramework.NUnit).DataTestAttributes);
    }

    [Fact]
    public void Libraries_DetectsMockingAndAssertionPackages()
    {
        var fs = WithPackages("xunit.v3", "Moq", "fluentassertions", "Shouldly", "NSubstitute", "FakeItEasy", "Other");
        Assert.Equal(TestLibraries.Moq | TestLibraries.NSubstitute | TestLibraries.FakeItEasy | TestLibraries.FluentAssertions | TestLibraries.Shouldly,
            TestFrameworks.Libraries(fs, Csproj));
    }

    [Fact]
    public void Libraries_None() => Assert.Equal(TestLibraries.None, TestFrameworks.Libraries(WithPackages("xunit.v3"), Csproj));

    [Theory]
    [InlineData(TestFramework.XunitV3, "Assert.Equal(\"Hi Kim\", result);")]
    [InlineData(TestFramework.MsTest, "Assert.AreEqual(\"Hi Kim\", result);")]
    [InlineData(TestFramework.NUnit, "Assert.That(result, Is.EqualTo(\"Hi Kim\"));")]
    public void AssertEqual_FrameworkForm(TestFramework framework, string expected) =>
        Assert.Equal(expected, TestFrameworks.Info(framework).AssertEqual("\"Hi Kim\"", "result"));
}
