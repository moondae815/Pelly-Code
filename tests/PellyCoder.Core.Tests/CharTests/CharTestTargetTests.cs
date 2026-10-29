using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class CharTestTargetTests
{
    private const string Throw = "throw new System.NotImplementedException(\"pelly-mutation\");";
    private static readonly LanguageVersion V = LanguageVersion.Latest;

    private static MemberCandidate Find(string src, string name) =>
        MemberLocator.FindByName(src, V, null, name).Single();

    private static (string MethodName, string TypeName, string? Error) Check(string src, string name, string? ivt = null) =>
        CharTestTarget.Check(src, Find(src, name), V, ivt);

    private static string Mutate(string src, string name) => CharTestTarget.Mutate(src, Find(src, name), V);

    [Fact]
    public void Check_PublicMethod_Ok()
    {
        var r = Check("public class A\n{\n    public int F() { return 1; }\n}\n", "F");
        Assert.Null(r.Error);
        Assert.Equal("F", r.MethodName);
        Assert.Equal("A", r.TypeName);
    }

    [Fact]
    public void Check_NestedType_UsesInnermostName()
    {
        var r = Check("public class A\n{\n    public class B { public int F() => 1; }\n}\n", "F");
        Assert.Null(r.Error);
        Assert.Equal("B", r.TypeName);
    }

    [Fact]
    public void Check_Property_Error()
    {
        var r = Check("public class A\n{\n    public int P { get; set; }\n}\n", "P");
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Check_Private_Error()
    {
        var r = Check("public class A\n{\n    private int F() { return 1; }\n    int G() { return 1; }\n}\n", "F");
        Assert.Contains("테스트 프로젝트에서 부를 수 없는 메서드입니다. 이 메서드를 부르는 public 메서드를 대상으로 하세요", r.Error);
        Assert.NotNull(Check("public class A\n{\n    int G() { return 1; }\n}\n", "G").Error);
    }

    [Fact]
    public void Check_InternalWithoutIvt_Error_WithIvt_Ok()
    {
        const string src = "public class A\n{\n    internal int F() { return 1; }\n}\n";
        Assert.NotNull(Check(src, "F").Error);
        Assert.Null(Check(src, "F", "Shop.Tests").Error);
    }

    [Fact]
    public void Check_InternalWithIvt_Ok()
    {
        Assert.Null(Check("public class A\n{\n    internal static int F() => 1;\n}\n", "F", "Shop.Tests").Error);
    }

    [Fact]
    public void Check_InternalType_PublicMethod_NeedsIvt()
    {
        const string src = "internal class A\n{\n    public int F() { return 1; }\n}\n";
        Assert.Contains("부를 수 없는 메서드입니다", Check(src, "F").Error);
        Assert.Null(Check(src, "F", "Shop.Tests").Error);
        const string implicitInternal = "class A\n{\n    public int F() { return 1; }\n}\n";
        Assert.NotNull(Check(implicitInternal, "F").Error);
        Assert.Null(Check(implicitInternal, "F", "Shop.Tests").Error);
    }

    [Fact]
    public void Check_NestedPrivateType_PublicMethod_Error()
    {
        const string src = "public class A\n{\n    private class B { public int F() { return 1; } }\n}\n";
        Assert.NotNull(Check(src, "F").Error);
        Assert.NotNull(Check(src, "F", "Shop.Tests").Error);
        const string implicitPrivate = "public class A\n{\n    class B { public int F() { return 1; } }\n}\n";
        Assert.NotNull(Check(implicitPrivate, "F").Error);
        const string prot = "public class A\n{\n    protected class B { public int F() { return 1; } }\n}\n";
        Assert.NotNull(Check(prot, "F").Error);
    }

    [Fact]
    public void Check_IvtName_IsCaseInsensitive()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/w/Api/Api.csproj", new MockFileData("<Project />"));
        fs.AddFile("/w/Api/X.cs", new MockFileData("[assembly: InternalsVisibleTo(\"api.tests\")]\n"));
        Assert.True(CharTestTarget.HasInternalsVisibleTo(fs, "/w/Api/Api.csproj", "Api.Tests"));
    }

    [Fact]
    public void Check_GenericType_Error()
    {
        Assert.NotNull(Check("public class A<T>\n{\n    public int F() { return 1; }\n}\n", "F").Error);
    }

    [Fact]
    public void Check_AbstractType_Error()
    {
        Assert.NotNull(Check("public abstract class A\n{\n    public int F() { return 1; }\n}\n", "F").Error);
    }

    [Fact]
    public void Check_StaticClassAndMethod_Ok()
    {
        Assert.Null(Check("public static class A\n{\n    public static int F() { return 1; }\n}\n", "F").Error);
    }

    [Fact]
    public void Check_Iterator_Error()
    {
        var r = Check("using System.Collections.Generic;\npublic class A\n{\n    public IEnumerable<int> F() { yield return 1; }\n}\n", "F");
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Check_YieldInLocalFunctionOnly_Ok()
    {
        const string src = "using System.Collections.Generic;\npublic class A\n{\n    public int F()\n    {\n        IEnumerable<int> L() { yield return 1; }\n        return 1;\n    }\n}\n";
        Assert.Null(Check(src, "F").Error);
    }

    [Fact]
    public void Check_ExternAndNoBody_Error()
    {
        Assert.NotNull(Check("public class A\n{\n    public static extern int F();\n}\n", "F").Error);
        Assert.NotNull(Check("public partial class A\n{\n    public partial int F();\n}\n", "F").Error);
    }

    [Fact]
    public void Check_Constructor_Error()
    {
        var src = "public class A\n{\n    public A() { }\n}\n";
        Assert.NotNull(Check(src, "A").Error);
    }

    [Fact]
    public void Mutate_Block()
    {
        const string src = "public class A\n{\n    public int F()\n    {\n        return 1;\n    }\n}\n";
        var m = Mutate(src, "F");
        Assert.Contains("public int F()", m);
        Assert.Contains("{ " + Throw + " }", m);
        Assert.DoesNotContain("return 1;", m);
        Assert.False(m.Contains('\r'));
    }

    [Fact]
    public void Mutate_ExpressionBody()
    {
        const string src = "public class A\n{\n    public int F() => 1;\n}\n";
        Assert.Equal("public class A\n{\n    public int F() => " + Throw + "\n}\n", Mutate(src, "F"));
    }

    [Fact]
    public void Mutate_AsyncBlock_YieldThenThrow()
    {
        const string src = "using System.Threading.Tasks;\npublic class A\n{\n    public async Task<int> F()\n    {\n        await Task.Delay(1);\n        return 1;\n    }\n}\n";
        var m = Mutate(src, "F");
        Assert.Contains("{ await System.Threading.Tasks.Task.Yield(); " + Throw + " }", m);
        Assert.DoesNotContain("Task.Delay", m);
    }

    [Fact]
    public void Mutate_AsyncExpressionBody_BlockWithYieldThrow()
    {
        const string src = "using System.Threading.Tasks;\npublic class A\n{\n    public async Task<int> F() => await Task.FromResult(1);\n}\n";
        var m = Mutate(src, "F");
        Assert.Contains("public async Task<int> F() { await System.Threading.Tasks.Task.Yield(); " + Throw + " }", m);
        Assert.DoesNotContain("=>", m);
    }

    [Fact]
    public void Mutate_NonAsyncTaskReturning_PlainThrow()
    {
        const string src = "using System.Threading.Tasks;\npublic class A\n{\n    public Task F() { return Task.CompletedTask; }\n}\n";
        var m = Mutate(src, "F");
        Assert.DoesNotContain("Yield", m);
        Assert.Contains("{ " + Throw + " }", m);
    }

    [Fact]
    public void Mutate_AsyncVoid_YieldThenThrow()
    {
        const string src = "public class A\n{\n    public async void F() { }\n}\n";
        Assert.Contains("Task.Yield(); " + Throw, Mutate(src, "F"));
    }

    [Fact]
    public void Mutate_OnlyTargetChanged()
    {
        const string src = "public class A\n{\n    // keep\n    public int G() { return 2; }\n\n    /// <summary>doc</summary>\n    [System.Obsolete]\n    public int F()\n    {\n        return 1;\n    }\n\n    public int H() => 3;\n}\n";
        var m = Mutate(src, "F");
        var start = src.IndexOf("    {\n        return 1;", StringComparison.Ordinal);
        var end = src.IndexOf("    }\n\n    public int H", StringComparison.Ordinal) + "    }".Length;
        Assert.Equal(src[..start], m[..start]);
        Assert.EndsWith(src[end..], m);
        Assert.Contains("/// <summary>doc</summary>", m);
        Assert.Contains("[System.Obsolete]", m);
    }

    [Fact]
    public void Mutate_OverloadsMutatesOnlyTheTargeted()
    {
        const string src = "public class A\n{\n    public int F(int a) { return a; }\n    public int F(string s) { return 2; }\n}\n";
        var target = MemberLocator.FindByName(src, V, null, "F").Single(c => c.Display.Contains("string"));
        var m = CharTestTarget.Mutate(src, target, V);
        Assert.Contains("return a;", m);
        Assert.DoesNotContain("return 2;", m);
    }

    [Fact]
    public async Task Mutate_ModernCalculateRefund_Compiles()
    {
        var path = CorpusLocation.Get("Modern/Shop.Api/Services/OrderService.cs");
        var src = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Replace("\r\n", "\n");
        var m = Mutate(src, "CalculateRefundAsync");

        Assert.Contains("Task.Yield(); " + Throw, m);
        Assert.NotEqual(src, m);
        var errors = CSharpSyntaxTree.ParseText(m, cancellationToken: TestContext.Current.CancellationToken).GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void HasInternalsVisibleTo_AssemblyAttribute()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/w/Api/Api.csproj", new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        fs.AddFile("/w/Api/AssemblyInfo.cs", new MockFileData("[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Api.Tests\")]\n"));
        Assert.True(CharTestTarget.HasInternalsVisibleTo(fs, "/w/Api/Api.csproj", "Api.Tests"));
        Assert.False(CharTestTarget.HasInternalsVisibleTo(fs, "/w/Api/Api.csproj", "Other.Tests"));
    }

    [Fact]
    public void HasInternalsVisibleTo_PublicKeySuffix_Ok()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/w/Api/Api.csproj", new MockFileData("<Project />"));
        fs.AddFile("/w/Api/X.cs", new MockFileData("using System.Runtime.CompilerServices;\n[assembly: InternalsVisibleTo(\"Api.Tests, PublicKey=00240000\")]\n"));
        Assert.True(CharTestTarget.HasInternalsVisibleTo(fs, "/w/Api/Api.csproj", "Api.Tests"));
    }

    [Fact]
    public void HasInternalsVisibleTo_PrefixOnlyName_False()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/w/Api/Api.csproj", new MockFileData("<Project />"));
        fs.AddFile("/w/Api/X.cs", new MockFileData("[assembly: InternalsVisibleTo(\"Api.Tests.Extra\")]\n"));
        Assert.False(CharTestTarget.HasInternalsVisibleTo(fs, "/w/Api/Api.csproj", "Api.Tests"));
    }

    [Fact]
    public void HasInternalsVisibleTo_CsprojItem()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/w/Api/Api.csproj", new MockFileData(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><InternalsVisibleTo Include=\"Api.Tests\" /></ItemGroup></Project>"));
        Assert.True(CharTestTarget.HasInternalsVisibleTo(fs, "/w/Api/Api.csproj", "Api.Tests"));
    }
}
