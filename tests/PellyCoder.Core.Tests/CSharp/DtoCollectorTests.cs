using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CSharp;

public sealed class DtoCollectorTests
{
    private const LanguageVersion V = LanguageVersion.CSharp12;

    private static string? CollectModern(string method)
    {
        var src = File.ReadAllText(CorpusLocation.Get("Modern/Shop.Api/Services/OrderService.cs")).Replace("\r\n", "\n");
        var target = Assert.Single(MemberLocator.FindByName(src, V, null, method));
        return DtoCollector.Collect(new FileSystem(), CorpusLocation.Get("Modern/Shop.Api"), src, target, V);
    }

    private static string? CollectMock(MockFileSystem fs, string source, string method, DtoLimits? limits = null)
    {
        var target = Assert.Single(MemberLocator.FindByName(source, V, null, method));
        return DtoCollector.Collect(fs, "/repo", source, target, V, limits);
    }

    private const string Service = """
        public class Svc
        {
            public void Run(Foo foo, Bar<Baz> bar) { }
        }
        """;

    [Fact]
    public void CancelAsync_DtoHasOrderStatusEnumAndOrder()
    {
        var dto = CollectModern("CancelAsync");

        Assert.NotNull(dto);
        Assert.Contains("enum OrderStatus: Pending", dto);
        Assert.Contains("class Order:", dto);
        Assert.DoesNotContain("OrderService", dto);
    }

    [Fact]
    public void CalculateRefundAsync_DtoHasOrderItem()
    {
        var dto = CollectModern("CalculateRefundAsync");

        Assert.NotNull(dto);
        Assert.Contains("IsGiftItem", dto);
        Assert.Contains("IsRefundable", dto);
    }

    [Fact]
    public void Collect_IsDeterministic() => Assert.Equal(CollectModern("CancelAsync"), CollectModern("CancelAsync"));

    [Fact]
    public void LegacyPublicFields_AreIncluded()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Dto/Foo.cs", new MockFileData("""
            public class Foo
            {
                public int Id;
                public string Name { get; set; }
                private int hidden;
                public static int Shared;
                public const int K = 1;
            }
            """));

        var dto = CollectMock(fs, Service, "Run");

        Assert.Equal("class Foo: int Id; string Name", dto);
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Other.cs", new MockFileData("public class Unrelated { public int A { get; set; } }"));

        Assert.Null(CollectMock(fs, Service, "Run"));
    }

    [Fact]
    public void Limits_MaxTypesAndMembers()
    {
        var fs = new MockFileSystem();
        var members = string.Join("\n", Enumerable.Range(1, 31).Select(i => $"public int P{i} {{ get; set; }}"));
        fs.AddFile("/repo/Foo.cs", new MockFileData($"public class Foo {{ {members} }}"));
        var src = """
            public class Svc
            {
                public void Run(Foo foo, T1 a, T2 b, T3 c, T4 d, T5 e, T6 f) { }
            }
            """;
        for (var i = 1; i <= 6; i++)
            fs.AddFile($"/repo/T{i}.cs", new MockFileData($"public class T{i} {{ public int V{i} {{ get; set; }} }}"));

        var dto = CollectMock(fs, src, "Run")!;
        var lines = dto.Split('\n');

        Assert.Equal(5, lines.Length);
        Assert.Contains("class Foo:", lines[0]);
        Assert.Contains("int P30", lines[0]);
        Assert.DoesNotContain("P31", lines[0]);
        Assert.EndsWith("; …", lines[0]);
        Assert.DoesNotContain("T5", dto);
        Assert.DoesNotContain("T6", dto);
    }

    [Fact]
    public void EnumMembersAboveLimit_GetEllipsis()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Foo.cs", new MockFileData("public enum Foo { A, B, C, D }"));

        var dto = CollectMock(fs, Service, "Run", new DtoLimits(MaxMembers: 2));

        Assert.Equal("enum Foo: A, B; …", dto);
    }

    [Fact]
    public void Cp949AndBrokenFiles_DoNotFail()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Foo.cs", new MockFileData(Encoding.GetEncoding(949).GetBytes("// 주문 정보\npublic class Foo { public int 번호; public int Id { get; set; } }")));
        fs.AddFile("/repo/Bar.cs", new MockFileData("public class Bar { public int Broken"));
        fs.AddFile("/repo/Baz.cs", new MockFileData(new byte[] { 0xFF, 0xFE, 0x00 }));

        var dto = CollectMock(fs, Service, "Run");

        Assert.NotNull(dto);
        Assert.Contains("class Foo:", dto);
        Assert.Contains("int 번호", dto);
        Assert.DoesNotContain("Bar", dto);
    }

    [Fact]
    public void FileCap_StopsScanning()
    {
        var fs = new MockFileSystem();
        for (var i = 1; i <= 4; i++)
            fs.AddFile($"/repo/F{i}.cs", new MockFileData($"public class {(i == 4 ? "Bar" : i == 1 ? "Foo" : "Zed" + i)} {{ public int A{i} {{ get; set; }} }}"));

        var dto = CollectMock(fs, Service, "Run", new DtoLimits(MaxFiles: 3))!;

        Assert.Contains("class Foo:", dto);
        Assert.DoesNotContain("class Bar", dto);
    }

    [Fact]
    public void OversizedFile_IsSkipped()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Foo.cs", new MockFileData("public class Foo { public int A { get; set; } }" + new string(' ', 200)));

        Assert.Null(CollectMock(fs, Service, "Run", new DtoLimits(MaxFileBytes: 100)));
    }

    [Fact]
    public void BinObjExcluded()
    {
        var fs = new MockFileSystem();
        foreach (var dir in new[] { "bin", "obj", ".pelly", ".git" })
            fs.AddFile($"/repo/{dir}/Foo.cs", new MockFileData("public class Foo { public int A { get; set; } }"));
        fs.AddFile("/repo/src/Bar.cs", new MockFileData("public class Bar { public int B { get; set; } }"));

        var dto = CollectMock(fs, Service, "Run");

        Assert.Equal("class Bar: int B", dto);
    }

    [Fact]
    public void TargetsOwnType_IsExcluded()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Svc.cs", new MockFileData("public class Svc { public int A { get; set; } }"));

        Assert.Null(CollectMock(fs, Service, "Run"));
    }

    [Fact]
    public void UnreadableRoot_ReturnsNull()
    {
        Assert.Null(CollectMock(new MockFileSystem(), Service, "Run"));
    }

    [Fact]
    public void SkippedDirectoryNames_AreNotEntered_CaseInsensitively()
    {
        var fs = new MockFileSystem();
        foreach (var dir in new[] { "BIN", "node_modules", "packages", ".vs", "TestResults", "Obj" })
            fs.AddFile($"/repo/{dir}/Foo.cs", new MockFileData("public class Foo { public int A { get; set; } }"));

        Assert.Null(CollectMock(fs, Service, "Run"));
    }

    [Fact]
    public void WideTreeWithoutCsFiles_StopsAtMaxDirectories()
    {
        var fs = new MockFileSystem();
        for (var i = 0; i < 50; i++) fs.AddDirectory($"/repo/a{i:D2}");
        fs.AddFile("/repo/z/Foo.cs", new MockFileData("public class Foo { public int A { get; set; } }"));

        Assert.Null(CollectMock(fs, Service, "Run", new DtoLimits(MaxDirectories: 10)));
        Assert.NotNull(CollectMock(fs, Service, "Run"));
    }

    [Fact]
    public void DeepTreeWithoutCsFiles_StopsAtMaxDirectories()
    {
        var fs = new MockFileSystem();
        var path = "/repo";
        for (var i = 0; i < 30; i++) path += "/d";
        fs.AddFile(path + "/Foo.cs", new MockFileData("public class Foo { public int A { get; set; } }"));

        Assert.Null(CollectMock(fs, Service, "Run", new DtoLimits(MaxDirectories: 10)));
        Assert.NotNull(CollectMock(fs, Service, "Run"));
    }

    [Fact]
    public void SymlinkedDirectory_IsNotFollowed()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/other/Foo.cs", new MockFileData("public class Foo { public int A { get; set; } }"));
        fs.AddDirectory("/repo");
        fs.Directory.CreateSymbolicLink("/repo/link", "/other");
        Assert.NotNull(fs.DirectoryInfo.New("/repo/link").LinkTarget); // the mock models links, so the real check is exercised

        Assert.Null(CollectMock(fs, Service, "Run"));
    }

    [Fact]
    public void ReparsePointDirectory_IsNotFollowed()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/junction/Foo.cs", new MockFileData("public class Foo { public int A { get; set; } }"));
        fs.DirectoryInfo.New("/repo/junction").Attributes |= FileAttributes.ReparsePoint;

        Assert.Null(CollectMock(fs, Service, "Run"));
    }

    [Fact]
    public void CancelledToken_Propagates()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Foo.cs", new MockFileData("public class Foo { public int A { get; set; } }"));
        var target = Assert.Single(MemberLocator.FindByName(Service, V, null, "Run"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => DtoCollector.Collect(fs, "/repo", Service, target, V, ct: cts.Token));
    }

    [Fact]
    public void LaterDeclarationWithData_ReplacesEmptyOne()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/A.cs", new MockFileData("public partial class Foo { }"));
        fs.AddFile("/repo/B.cs", new MockFileData("public partial class Foo { public int X { get; set; } }"));

        Assert.Equal("class Foo: int X", CollectMock(fs, Service, "Run"));
    }

    private const string RepoService = """
        public class Svc
        {
            private readonly Repo _repo;
            public void Run() { var x = _repo.Get(); }
        }
        """;

    [Fact]
    public void Expansion_OneHopThroughDatalessTypeFollowsCalledMethodsOnly()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Repo.cs", new MockFileData("public interface Repo { Item1 Get(); Item2 Other(); }"));
        fs.AddFile("/repo/Items.cs", new MockFileData("public class Item1 { public int A { get; set; } }\npublic class Item2 { public int B { get; set; } }"));

        Assert.Equal("class Item1: int A", CollectMock(fs, RepoService, "Run"));
    }

    [Fact]
    public void Expansion_TypeWithDataMembersDoesNotExpand()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Repo.cs", new MockFileData("public class Repo { public int Count { get; set; } public Item1 Get() => null; }"));
        fs.AddFile("/repo/Items.cs", new MockFileData("public class Item1 { public int A { get; set; } }"));

        Assert.Equal("class Repo: int Count", CollectMock(fs, RepoService, "Run"));
    }

    [Fact]
    public void Expansion_StopsAfterTwoRounds()
    {
        var fs = new MockFileSystem();
        const string src = """
            public class Svc
            {
                private readonly Repo _repo;
                public void Run() { var x = _repo.Get().Next(); }
            }
            """;
        fs.AddFile("/repo/Repo.cs", new MockFileData("public interface Repo { Mid Get(); }\npublic interface Mid { Deep Next(); }"));
        fs.AddFile("/repo/Deep.cs", new MockFileData("public class Deep { public int D { get; set; } }"));

        Assert.Null(CollectMock(fs, src, "Run"));
    }
}
