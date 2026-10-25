using System.IO.Abstractions;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Integration.Tests.Impact;

public sealed class SemanticImpactFinderTests(RestoredSameNameFixture fx) : IClassFixture<RestoredSameNameFixture>
{
    private static readonly FileSystem Fs = new();

    private async Task<ImpactResult> FindAsync(ImpactSeed seed)
    {
        var ws = new Workspace(fx.Root, Path.Combine(fx.Root, ".pelly"));
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
}
