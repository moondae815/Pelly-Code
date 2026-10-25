using System.IO.Abstractions;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Integration.Tests.Impact;

public sealed class SemanticImpactFinderTests(RestoredSameNameFixture fx) : IClassFixture<RestoredSameNameFixture>
{
    private static readonly FileSystem Fs = new();

    private Task<ImpactResult> FindAsync(ImpactSeed seed) => FindAsync(fx.Root, seed);

    private static async Task<ImpactResult> FindAsync(string root, ImpactSeed seed)
    {
        var ws = new Workspace(root, Path.Combine(root, ".pelly"));
        return await ImpactFinders.Default(Fs).FindAsync(ws, [seed], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Interface_seed_finds_implementations_and_callers_but_not_the_same_named_interface()
    {
        var result = await FindAsync(new ImpactSeed("T1", 5, "Data/IOrderDao.cs", "IOrderDao", "GetOrder"));

        Assert.Equal(ImpactMode.Semantic, result.Mode);
        Assert.Null(result.Fallback);
        Assert.False(result.Incomplete);
        Assert.Empty(result.UnsupportedSeeds);
        Assert.Equal(
        [
            new ImpactTarget("T1", "Data/OrderDao.cs", "GetOrder", "구현"),
            // Explicit implementation: named by its identifier like MemberLocator, not "SameName.Data.IOrderDao.GetOrder".
            new ImpactTarget("T1", "Web.Tests/FakeOrderDao.cs", "GetOrder", "구현"),
            new ImpactTarget("T1", "Web/OrderService.cs", "GetOrder", "호출"),
            new ImpactTarget("T1", "Web/OrderService.cs", "Cancel", "호출"),
        ], result.Targets);
        Assert.DoesNotContain(result.Targets, t => t.Path == "Web/OrderController.cs");
        Assert.DoesNotContain(result.Targets, t => t.Path == "Web/IOrderService.cs");
    }

    [Fact]
    public async Task Class_seed_goes_up_to_its_interface()
    {
        var result = await FindAsync(new ImpactSeed("T2", 5, "Data/OrderDao.cs", "OrderDao", "GetOrder"));

        Assert.Equal(ImpactMode.Semantic, result.Mode);
        Assert.Contains(new ImpactTarget("T2", "Data/IOrderDao.cs", "GetOrder", "기반 멤버"), result.Targets);
        Assert.Contains(new ImpactTarget("T2", "Web.Tests/FakeOrderDao.cs", "GetOrder", "구현"), result.Targets);
        Assert.Contains(new ImpactTarget("T2", "Web/OrderService.cs", "GetOrder", "호출"), result.Targets);
        Assert.Contains(new ImpactTarget("T2", "Web/OrderService.cs", "Cancel", "호출"), result.Targets);
        Assert.DoesNotContain(result.Targets, t => t.Path == "Data/OrderDao.cs");
        Assert.DoesNotContain(result.Targets, t => t.Path == "Web/OrderController.cs");
    }

    [Fact]
    public async Task Partial_class_and_overloads_report_every_location()
    {
        var result = await FindAsync(new ImpactSeed("T3", 5, "Data/OrderDao.cs", null, "GetOrder"));

        Assert.Equal(ImpactMode.Semantic, result.Mode);
        // The overload declared in the other partial file is a location of the seed member the edit does not touch.
        Assert.Contains(new ImpactTarget("T3", "Data/OrderDao.Part2.cs", "GetOrder", "구현"), result.Targets);
    }

    [Fact]
    public async Task Seed_not_in_the_solution_is_unsupported()
    {
        var result = await FindAsync(new ImpactSeed("T4", 5, "Data/OrderDao.cs", "OrderDao", "NoSuchMember"));

        Assert.Equal(ImpactMode.Semantic, result.Mode);
        Assert.Equal(["T4"], result.UnsupportedSeeds);
        Assert.Empty(result.Targets);
    }

    [Fact]
    public async Task Explicit_implementation_seed_resolves_and_goes_up()
    {
        var result = await FindAsync(new ImpactSeed("T5", 7, "Web.Tests/FakeOrderDao.cs", "FakeOrderDao", "GetOrder"));

        Assert.Empty(result.UnsupportedSeeds);
        Assert.Contains(new ImpactTarget("T5", "Data/IOrderDao.cs", "GetOrder", "기반 멤버"), result.Targets);
        Assert.Contains(new ImpactTarget("T5", "Data/OrderDao.cs", "GetOrder", "구현"), result.Targets);
    }

    [Fact]
    public async Task Partial_method_reports_the_other_part()
    {
        var fromDefinition = await FindAsync(new ImpactSeed("T6", 7, "Data/OrderDao.cs", "OrderDao", "Count"));
        var fromImplementation = await FindAsync(new ImpactSeed("T7", 7, "Data/OrderDao.Part2.cs", "OrderDao", "Count"));

        Assert.Equal([new ImpactTarget("T6", "Data/OrderDao.Part2.cs", "Count", "구현")], fromDefinition.Targets);
        Assert.Equal([new ImpactTarget("T7", "Data/OrderDao.cs", "Count", "구현")], fromImplementation.Targets);
    }

    [Fact]
    public async Task Virtual_seed_reports_its_override_and_the_override_goes_up()
    {
        var down = await FindAsync(new ImpactSeed("T8", 5, "Data/Exporters.cs", "BaseExporter", "Export"));
        var up = await FindAsync(new ImpactSeed("T9", 5, "Data/CsvExporter.cs", "CsvExporter", "Export"));

        Assert.Equal(ImpactMode.Semantic, down.Mode);
        Assert.Equal([new ImpactTarget("T8", "Data/CsvExporter.cs", "Export", "재정의")], down.Targets);
        Assert.Equal([new ImpactTarget("T9", "Data/Exporters.cs", "Export", "기반 멤버")], up.Targets);
    }

    [Fact]
    public async Task Places_outside_the_workspace_root_are_not_targets()
    {
        // Workspace = Web.Tests only: Data and Web are loaded as references but lie outside the root.
        var result = await FindAsync(Path.Combine(fx.Root, "Web.Tests"), new ImpactSeed("T10", 7, "FakeOrderDao.cs", "FakeOrderDao", "GetOrder"));

        Assert.Equal(ImpactMode.Semantic, result.Mode);
        Assert.Empty(result.UnsupportedSeeds);
        Assert.Empty(result.Targets);
    }
}
