using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.Testing;

public class CompileRedTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private const string TestSource = """
        namespace Shop.Tests;

        public class OrderTests
        {
            [Fact]
            public void Cancel_Refunds()
            {
                new Order().Cancel(refund: true);
            }

            private static void Helper()
            {
                var x = 1;
            }
        }
        """;

    private static readonly string Project = P("Shop.Tests/Shop.Tests.csproj");
    private static readonly string TestFile = P("Shop.Tests/OrderTests.cs");

    private static MockFileSystem Fs()
    {
        var fs = new MockFileSystem();
        fs.AddFile(Project, new MockFileData("<Project />"));
        fs.AddFile(TestFile, new MockFileData(TestSource));
        fs.AddFile(P("Shop/Order.cs"), new MockFileData("namespace Shop; public class Order { }"));
        return fs;
    }

    private static readonly IReadOnlyList<TestMethodDecl> Decls =
        [new TestMethodDecl("Shop.Tests.OrderTests.Cancel_Refunds", TestFile, Project)];

    private static Diagnostic Error(string file, int line, string code = "CS1061") =>
        new(file, line, 13, "error", code, "x");

    [Fact]
    public void CompileRed_ErrorInsideTest_IsCompileRed()
    {
        var verdict = CompileRed.Judge(Fs(), Root, Project, [Error(TestFile, 8)], Decls);

        Assert.True(verdict.IsCompileRed);
        Assert.Null(verdict.Unverifiable);
    }

    [Fact]
    public void CompileRed_RelativePathWithEitherSeparator_IsCompileRed()
    {
        Assert.True(CompileRed.Judge(Fs(), Root, Project, [Error("Shop.Tests/OrderTests.cs", 8)], Decls).IsCompileRed);
        Assert.True(CompileRed.Judge(Fs(), Root, Project, [Error(@"Shop.Tests\OrderTests.cs", 8)], Decls).IsCompileRed);
    }

    [Fact]
    public void CompileRed_ErrorOutsideTestMethod_Unverifiable()
    {
        var verdict = CompileRed.Judge(Fs(), Root, Project, [Error(TestFile, 13)], Decls);

        Assert.False(verdict.IsCompileRed);
        Assert.Equal(
            $"테스트 프로젝트의 컴파일 오류가 지정한 테스트 안에 없습니다: {Path.Combine("Shop.Tests", "OrderTests.cs")}(13)",
            verdict.Unverifiable);
    }

    [Fact]
    public void CompileRed_NonCompilerError_Unverifiable()
    {
        var verdict = CompileRed.Judge(Fs(), Root, Project, [Error(TestFile, 8), Error(Project, 1, "MSB3202")], Decls);

        Assert.False(verdict.IsCompileRed);
        Assert.Equal("테스트 프로젝트를 원본 상태에서 빌드할 수 없습니다: MSB3202", verdict.Unverifiable);
    }

    [Fact]
    public void CompileRed_ErrorOutsideProject_Unverifiable()
    {
        var verdict = CompileRed.Judge(Fs(), Root, Project, [Error(TestFile, 8), Error(P("Shop/Order.cs"), 1)], Decls);

        Assert.False(verdict.IsCompileRed);
        Assert.Equal($"테스트 프로젝트 밖에 원래 컴파일 오류가 있습니다: {Path.Combine("Shop", "Order.cs")}", verdict.Unverifiable);
    }

    [Fact]
    public void CompileRed_SiblingFolderWithSamePrefix_IsOutsideProject()
    {
        var other = P("Shop.Tests2/X.cs");
        var verdict = CompileRed.Judge(Fs(), Root, Project, [Error(TestFile, 8), Error(other, 1)], Decls);

        Assert.Equal($"테스트 프로젝트 밖에 원래 컴파일 오류가 있습니다: {Path.Combine("Shop.Tests2", "X.cs")}", verdict.Unverifiable);
    }
}
