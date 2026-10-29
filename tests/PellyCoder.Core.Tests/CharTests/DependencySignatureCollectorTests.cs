using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class DependencySignatureCollectorTests
{
    private const LanguageVersion V = LanguageVersion.CSharp12;

    private static string? CollectModern(string method)
    {
        var src = File.ReadAllText(CorpusLocation.Get("Modern/Shop.Api/Services/OrderService.cs")).Replace("\r\n", "\n");
        var target = Assert.Single(MemberLocator.FindByName(src, V, null, method));
        return DependencySignatureCollector.Collect(new FileSystem(), CorpusLocation.Get("Modern/Shop.Api"), src, target, V, ct: TestContext.Current.CancellationToken);
    }

    private static string? CollectMock(MockFileSystem fs, string source, string method, DtoLimits? limits = null)
    {
        var target = Assert.Single(MemberLocator.FindByName(source, V, null, method));
        return DependencySignatureCollector.Collect(fs, "/repo", source, target, V, limits, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Dependencies_ModernOrderService_IncludesIOrderRepository()
    {
        var deps = CollectModern("CalculateRefundAsync");

        Assert.NotNull(deps);
        Assert.Contains("Shop.Api.Data: interface IOrderRepository {", deps);
        Assert.Contains("Task<Order> GetByIdAsync(int orderId);", deps);
        Assert.Contains("Task AddRefundAsync(int orderId, decimal amount);", deps);
        Assert.DoesNotContain("=>", deps);
        Assert.DoesNotContain("return", deps);
        Assert.DoesNotContain("ILogger", deps);
        Assert.DoesNotContain("OrderService", deps);
    }

    [Fact]
    public void Dependencies_NoProjectInterfaces_Null()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Clock.cs", new MockFileData("public class Clock { public int Now() => 1; }"));
        const string svc = """
            public class Svc
            {
                private readonly Clock _clock;
                private readonly ILogger _log;
                public Svc(Clock clock, ILogger log) { _clock = clock; _log = log; }
                public int Run() => _clock.Now();
            }
            """;

        Assert.Null(CollectMock(fs, svc, "Run"));
    }

    [Fact]
    public void Dependencies_AbstractClass_RendersAbstractMembersWithoutBodies()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Store.cs", new MockFileData("""
            public abstract class Store
            {
                public abstract int Load(string key);
                public virtual void Save(string key, int value) { }
                private void Hidden() { }
                public static Store Create() => null;
            }
            """));
        const string svc = """
            public class Svc
            {
                private readonly Store _store;
                public Svc(Store store) { _store = store; }
                public int Run() => _store.Load("a");
            }
            """;

        var deps = CollectMock(fs, svc, "Run");

        Assert.Equal("abstract class Store { public abstract int Load(string key); public virtual void Save(string key, int value); }", deps);
    }

    [Fact]
    public void Dependencies_FieldTypeUsedByTarget_IncludedEvenWithoutConstructor()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/IClock.cs", new MockFileData("public interface IClock { System.DateTime Now { get; } }"));
        const string svc = """
            public class Svc
            {
                public IClock Clock { get; set; }
                public int Run() => Clock.Now.Day;
            }
            """;

        Assert.Equal("interface IClock { System.DateTime Now { get; } }", CollectMock(fs, svc, "Run"));
    }

    [Fact]
    public void Dependencies_MemberLimit_TruncatesFieldTypes()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/IBig.cs", new MockFileData("public interface IBig { void A(); void B(); void C(); }"));
        const string svc = """
            public class Svc
            {
                private IBig _big;
                public void Run() { _big.A(); }
            }
            """;

        Assert.Equal("interface IBig { void A(); void B(); … }", CollectMock(fs, svc, "Run", new DtoLimits(MaxMembers: 2)));
    }

    [Fact]
    public void Dependencies_ConstructorParameterType_NeverTruncated()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/IBig.cs", new MockFileData("public interface IBig { void A(); void B(); void C(); }"));
        const string svc = """
            public class Svc
            {
                public Svc(IBig big) { }
                public void Run() { }
            }
            """;

        Assert.Equal("interface IBig { void A(); void B(); void C(); }", CollectMock(fs, svc, "Run", new DtoLimits(MaxMembers: 2)));
    }

    [Fact]
    public void Dependencies_InheritedInterfaceMembers_Rendered()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/IBase.cs", new MockFileData("public interface IBase<T> { T Get(int id); }\npublic interface IAudit { void Log(string m); }"));
        fs.AddFile("/repo/IRepo.cs", new MockFileData("public interface IRepo : IBase<Order>, IAudit, System.IDisposable { void Save(Order o); }"));
        const string svc = """
            public class Svc
            {
                public Svc(IRepo repo) { }
                public void Run() { }
            }
            """;

        Assert.Equal("interface IRepo { void Save(Order o); Order Get(int id); void Log(string m); }", CollectMock(fs, svc, "Run"));
    }

    [Fact]
    public void Dependencies_AbstractClass_KeepsProtectedModifier()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/Job.cs", new MockFileData("public abstract class Job { protected abstract int Step(); protected internal virtual void Done() { } }"));
        const string svc = """
            public class Svc
            {
                public Svc(Job job) { }
                public void Run() { }
            }
            """;

        Assert.Equal("abstract class Job { protected abstract int Step(); protected internal virtual void Done(); }", CollectMock(fs, svc, "Run"));
    }

    [Fact]
    public void Dependencies_NamespacedType_PrefixedWithNamespace()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/IClock.cs", new MockFileData("namespace Demo.Time\n{\n    public interface IClock { int Now(); }\n}\n"));
        const string svc = """
            public class Svc
            {
                public Svc(IClock clock) { }
                public void Run() { }
            }
            """;

        Assert.Equal("Demo.Time: interface IClock { int Now(); }", CollectMock(fs, svc, "Run"));
    }

    [Fact]
    public void NamespacesOf_FindsDeclaringNamespaces()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/repo/A.cs", new MockFileData("namespace Demo.Models { public class Order { } public enum Kind { A } }"));
        fs.AddFile("/repo/B.cs", new MockFileData("namespace Demo.Other;\npublic record Order(int Id);"));
        fs.AddFile("/repo/C.cs", new MockFileData("public class Order { }"));

        var map = DependencySignatureCollector.NamespacesOf(fs, "/repo", ["Order", "Kind", "Missing"], V, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Demo.Models", "Demo.Other"], map["Order"]);
        Assert.Equal(["Demo.Models"], map["Kind"]);
        Assert.False(map.ContainsKey("Missing"));
    }
}
