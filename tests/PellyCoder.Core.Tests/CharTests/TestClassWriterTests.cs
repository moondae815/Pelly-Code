using System.Text;
using PellyCoder.Core.CharTests;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class TestClassWriterTests
{
    private const string Member = "[Fact]\npublic void A()\n{\n    Assert.True(true);\n}";

    [Fact]
    public void Insert_AppendsBeforeClosingBrace()
    {
        var text = "using Xunit;\n\nnamespace N\n{\n    public class CTests\n    {\n        [Fact]\n        public void Old() { }\n    }\n}\n";

        var result = TestClassWriter.Insert(text, "CTests", [], [Member]);

        var expected = "using Xunit;\n\nnamespace N\n{\n    public class CTests\n    {\n        [Fact]\n        public void Old() { }\n\n"
            + "        [Fact]\n        public void A()\n        {\n            Assert.True(true);\n        }\n    }\n}\n";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Insert_TwoMembers_SeparatedByBlankLine()
    {
        var text = "namespace N\n{\n    public class CTests\n    {\n        public void Old() { }\n    }\n}\n";
        var result = TestClassWriter.Insert(text, "CTests", [], ["public void A() { }", "public void B() { }"]);
        Assert.Contains("        public void A() { }\n\n        public void B() { }\n    }\n", result);
    }

    [Fact]
    public void Insert_EmptyClass_NoLeadingBlankLine_IndentIsClassPlusFour()
    {
        var text = "namespace N\n{\n    public class CTests\n    {\n    }\n}\n";
        var result = TestClassWriter.Insert(text, "CTests", [], ["public void A() { }"]);
        Assert.Equal("namespace N\n{\n    public class CTests\n    {\n        public void A() { }\n    }\n}\n", result);
    }

    [Fact]
    public void Insert_AddsOnlyMissingUsings()
    {
        var text = "using System;\nusing Xunit;\n\nnamespace N\n{\n    public class CTests\n    {\n    }\n}\n";

        var result = TestClassWriter.Insert(text, "CTests", ["Xunit", "System.Linq"], ["public void A() { }"]);

        Assert.StartsWith("using System;\nusing Xunit;\nusing System.Linq;\n\nnamespace N", result);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "using Xunit;"));
    }

    [Fact]
    public void Insert_NoUsings_AddsBlockAboveNamespace()
    {
        var text = "namespace N\n{\n    public class CTests\n    {\n    }\n}\n";
        var result = TestClassWriter.Insert(text, "CTests", ["Xunit"], ["public void A() { }"]);
        Assert.StartsWith("using Xunit;\n\nnamespace N\n", result);
    }

    [Fact]
    public void Insert_PreservesCrlfBomAndTabs()
    {
        var text = "﻿using Xunit;\r\n\r\nnamespace N\r\n{\r\n\tpublic class CTests\r\n\t{\r\n\t\t[Fact]\r\n\t\tpublic void Old() { }\r\n\t}\r\n}\r\n";

        var result = TestClassWriter.Insert(text, "CTests", ["System.Linq"], [Member]);

        Assert.StartsWith("﻿using Xunit;\r\nusing System.Linq;\r\n\r\nnamespace N", result);
        Assert.DoesNotContain("\n", result.Replace("\r\n", ""));
        Assert.EndsWith("\r\n\r\n\t\t[Fact]\r\n\t\tpublic void A()\r\n\t\t{\r\n\t\t    Assert.True(true);\r\n\t\t}\r\n\t}\r\n}\r\n", result);
        // Original lines stay byte-identical.
        foreach (var line in text.Split("\r\n"))
            Assert.Contains(line, result);
    }

    [Fact]
    public void Insert_ClassOnOneLine_StillWorks()
    {
        var text = "namespace N { public class CTests { } }\n";
        var result = TestClassWriter.Insert(text, "CTests", [], ["public void A() { }"]);
        Assert.Contains("public void A() { }", result);
        Assert.Contains("class CTests", result);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(result, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(tree.GetDiagnostics(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NewFile_MsTest_HasTestClassAndBlockNamespace()
    {
        var text = TestClassWriter.NewFile("Shop.Tests", "OrderServiceTests", TestFrameworks.Info(TestFramework.MsTest));

        var expected = "using Microsoft.VisualStudio.TestTools.UnitTesting;\n\nnamespace Shop.Tests\n{\n    [TestClass]\n    public class OrderServiceTests\n    {\n    }\n}\n";
        Assert.Equal(expected, text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void NewFile_Xunit_NoClassAttribute()
    {
        var text = TestClassWriter.NewFile("N", "CTests", TestFrameworks.Info(TestFramework.XunitV3));
        Assert.Contains("using Xunit;", text);
        Assert.DoesNotContain("[", text);
        Assert.Contains("    public class CTests\n", text);
    }

    [Fact]
    public void NewFile_ExtraUsings_ComeFirst_Deduped()
    {
        var text = TestClassWriter.NewFile("Shop.Api.Tests", "OrdersControllerTests", TestFrameworks.Info(TestFramework.XunitV3),
            ["System", "Shop.Api.Models", "Xunit", "System"]);

        Assert.StartsWith("using System;\nusing Shop.Api.Models;\nusing Xunit;\n\nnamespace Shop.Api.Tests\n{\n", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "using Xunit;"));
    }

    private static readonly IReadOnlySet<string> ShopApi = new HashSet<string> { "Shop.Api.Models", "Shop.Api.Controllers" };

    [Fact]
    public void TargetUsings_FileUsingsThenNamespace_NoAliasStaticOrGlobal()
    {
        const string source = "using System;\nusing static System.Math;\nusing M = System.Text;\nglobal using System.Linq;\n"
            + "using Shop.Api.Models;\n\nnamespace Shop.Api.Controllers\n{\n    using Inner;\n    public class C { }\n}\n";

        var usings = TestClassWriter.TargetUsings(source, Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp12, ShopApi);

        Assert.Equal(["System", "Shop.Api.Models", "Shop.Api.Controllers"], usings);
    }

    [Fact]
    public void TargetUsings_GlobalNamespace_OnlyFileUsings() =>
        Assert.Equal(["System"], TestClassWriter.TargetUsings("using System;\npublic class C { }\n", Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp12, ShopApi));

    /// <summary>Shaped like Legacy Shop.Data/OrderDao.cs: a non-SDK test project does not inherit Dapper or System.Data.SqlClient.</summary>
    [Fact]
    public void TargetUsings_KeepsWorkspaceAndCoreBcl_DropsThirdPartyAndOtherFramework()
    {
        const string source = "using System;\nusing System.Collections.Generic;\nusing System.Data;\nusing System.Data.SqlClient;\nusing System.Linq;\n"
            + "using System.Text;\nusing System.Threading.Tasks;\nusing System.Web.Mvc;\nusing Dapper;\nusing Shop.Data.Dto;\n\nnamespace Shop.Data\n{\n    public class OrderDao { }\n}\n";

        var usings = TestClassWriter.TargetUsings(source, Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp7_3,
            new HashSet<string> { "Shop.Data", "Shop.Data.Dto" });

        Assert.Equal(["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks", "Shop.Data.Dto", "Shop.Data"], usings);
    }

    [Fact]
    public void TargetUsings_NoFilter_KeepsEveryPlainUsing()
    {
        const string source = "using System.Data.SqlClient;\nusing Dapper;\nusing static System.Math;\nusing M = System.Text;\n\nnamespace Shop.Data { public class OrderDao { } }\n";

        Assert.Equal(["System.Data.SqlClient", "Dapper", "Shop.Data"],
            TestClassWriter.TargetUsings(source, Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp12, declaredNamespaces: null));
    }

    [Fact]
    public void TargetUsings_ParentOrChildOfDeclaredNamespace_Kept()
    {
        const string source = "using Shop.Api.Models;\nusing Shop;\nusing Shopify.Client;\n\nnamespace Demo { public class C { } }\n";

        var usings = TestClassWriter.TargetUsings(source, Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp12, new HashSet<string> { "Shop.Api" });

        Assert.Equal(["Shop.Api.Models", "Shop", "Demo"], usings);
    }

    [Theory]
    [InlineData("Legacy/Shop.Data/OrderDao.cs", "OrderDaoTests", new[] { "Shop.Data.Dto", "Shop.Data" }, new[] { "Dapper", "System.Data.SqlClient" })]
    [InlineData("Legacy/Shop.Web/Controllers/OrderController.cs", "OrderControllerTests", new[] { "Shop.Web.Services", "Shop.Web.Controllers" }, new[] { "System.Web.Mvc" })]
    public void NewFile_LegacyCorpusTarget_NoNonWorkspaceUsings(string target, string className, string[] kept, string[] dropped)
    {
        var fs = new System.IO.Abstractions.FileSystem();
        var source = File.ReadAllText(CorpusLocation.Get(target)).Replace("\r\n", "\n");
        var declared = TestClassWriter.DeclaredNamespaces(fs, CorpusLocation.Get("Legacy"), ct: TestContext.Current.CancellationToken);

        var text = TestClassWriter.NewFile("Shop.Tests", className, TestFrameworks.Info(TestFramework.MsTest),
            TestClassWriter.TargetUsings(source, Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp7_3, declared));

        Assert.All(kept, k => Assert.Contains($"using {k};\n", text));
        Assert.All(dropped, d => Assert.DoesNotContain($"using {d};", text));
        Assert.Contains("using System;\n", text);
        Assert.Contains("using Microsoft.VisualStudio.TestTools.UnitTesting;\n", text);
    }

    [Fact]
    public void DeclaredNamespaces_BlockAndFileScoped()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        fs.AddFile("/repo/A.cs", new System.IO.Abstractions.TestingHelpers.MockFileData("namespace Demo.Models\n{\n    namespace Inner { }\n}\n"));
        fs.AddFile("/repo/B.cs", new System.IO.Abstractions.TestingHelpers.MockFileData("using X;\nnamespace Demo.Other;\npublic class C { } // namespace Fake\n"));
        fs.AddFile("/repo/bin/G.cs", new System.IO.Abstractions.TestingHelpers.MockFileData("namespace Generated { }"));

        var declared = TestClassWriter.DeclaredNamespaces(fs, "/repo", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Demo.Models", "Demo.Models.Inner", "Demo.Other"], declared.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NewFile_ThenInsert_Parses()
    {
        var text = TestClassWriter.NewFile("N", "CTests", TestFrameworks.Info(TestFramework.NUnit));
        var result = TestClassWriter.Insert(text, "CTests", ["NUnit.Framework"], ["[Test]\npublic void A() { }"]);
        Assert.Empty(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(result, cancellationToken: TestContext.Current.CancellationToken).GetDiagnostics(TestContext.Current.CancellationToken));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "using NUnit.Framework;"));
    }

    [Fact]
    public void AddCompileInclude_LegacyCorpus_AfterLastCompile()
    {
        var path = CorpusLocation.Get("Legacy/Shop.Tests/Shop.Tests.csproj");
        var original = File.ReadAllText(path);

        var result = TestClassWriter.AddCompileInclude(original, "CustomerTests.cs");

        Assert.NotNull(result);
        var nl = original.Contains("\r\n") ? "\r\n" : "\n";
        var anchor = "    <Compile Include=\"Properties\\AssemblyInfo.cs\" />" + nl;
        var expected = original.Replace(anchor, anchor + "    <Compile Include=\"CustomerTests.cs\" />" + nl);
        Assert.Equal(expected, result);
        Assert.NotEqual(original, result);
    }

    [Fact]
    public void AddCompileInclude_Sdk_ReturnsNull()
    {
        var path = CorpusLocation.Get("Modern/Shop.Api.Tests/Shop.Api.Tests.csproj");
        Assert.Null(TestClassWriter.AddCompileInclude(File.ReadAllText(path), "X.cs"));
    }

    [Fact]
    public void AddCompileInclude_CrlfAndMultiLineElement()
    {
        var text = "<Project>\r\n  <ItemGroup>\r\n    <Compile Include=\"A.cs\" />\r\n    <Compile Include=\"B.cs\">\r\n      <DependentUpon>A.cs</DependentUpon>\r\n    </Compile>\r\n  </ItemGroup>\r\n</Project>\r\n";
        var result = TestClassWriter.AddCompileInclude(text, "C.cs");
        Assert.Equal(text.Replace("    </Compile>\r\n", "    </Compile>\r\n    <Compile Include=\"C.cs\" />\r\n"), result);
    }

    [Fact]
    public void AddCompileInclude_NoCompileItems_AddsItemGroupBeforeProjectEnd()
    {
        var text = "<Project ToolsVersion=\"15.0\">\n  <PropertyGroup />\n</Project>\n";
        var result = TestClassWriter.AddCompileInclude(text, "C.cs");
        Assert.Equal("<Project ToolsVersion=\"15.0\">\n  <PropertyGroup />\n  <ItemGroup>\n    <Compile Include=\"C.cs\" />\n  </ItemGroup>\n</Project>\n", result);
    }

    [Fact]
    public void AddCompileInclude_MultiLineCompileWithoutClosingTag_NewItemGroup()
    {
        var text = "<Project>\n  <ItemGroup>\n    <Compile Include=\"A.cs\"\n      Link=\"x\">\n  </ItemGroup>\n</Project>\n";
        var result = TestClassWriter.AddCompileInclude(text, "C.cs");
        Assert.Equal(text.Replace("</Project>", "  <ItemGroup>\n    <Compile Include=\"C.cs\" />\n  </ItemGroup>\n</Project>"), result);
    }

    [Fact]
    public void AddCompileInclude_NoProjectEnd_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TestClassWriter.AddCompileInclude("<Project>\n  <PropertyGroup />\n", "C.cs"));
        Assert.Contains("</Project>", ex.Message);
    }

    [Fact]
    public void AddCompileInclude_AttributesSpreadAcrossLines_InsertsAfterWholeElement()
    {
        var text = "<Project>\n  <ItemGroup>\n    <Compile Include=\"A.cs\" />\n    <Compile\n      Include=\"B.cs\"\n      Link=\"b\">\n      <Dep>A.cs</Dep>\n    </Compile>\n  </ItemGroup>\n</Project>\n";
        var result = TestClassWriter.AddCompileInclude(text, "C.cs");
        Assert.Equal(text.Replace("    </Compile>\n", "    </Compile>\n    <Compile Include=\"C.cs\" />\n"), result);
    }

    [Fact]
    public void Insert_AliasAndStaticUsings_AddedOnce()
    {
        const string text = "using M = Shop.Models;\nusing static System.Math;\n\nnamespace N;\n\npublic class CTests\n{\n}\n";

        var result = TestClassWriter.Insert(text, "CTests", ["using M=Shop.Models;", "using static System.Math;", "using X = Shop.Other;"], ["public void A() { }"]);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "using M ?= ?Shop.Models;"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "using static System.Math;"));
        Assert.Contains("using X = Shop.Other;\n", result);
    }
}
