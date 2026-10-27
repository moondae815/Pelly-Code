using System.IO.Abstractions;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Specs.Drafting;
using PellyCoder.Core.Workspaces;
using PellyCoder.Integration.Tests.E2E;
using PellyCoder.Integration.Tests.Impact;

namespace PellyCoder.Integration.Tests.Specs;

/// <summary>SpecDrafter with the real semantic finder on a restored copy of tests/fixtures/impact/SameName.</summary>
public sealed class SpecDraftImpactTests(RestoredSameNameFixture fx) : IClassFixture<RestoredSameNameFixture>
{
    [Fact]
    public async Task RealSemanticFinder_AddsCallerOfChangedSignature()
    {
        var fs = new FileSystem();
        var ws = new Workspace(fx.Root, Path.Combine(fx.Root, ".pelly"));
        var backend = new CannedLlmBackend(["thought", "- [ ] T1 cs Data/IOrderDao.cs#GetOrder bool includeDeleted 매개변수를 더한다 [G1]\n"]);
        var drafter = new SpecDrafter(fs, new DraftModel(backend, QwenChatTemplate.LoadDefault(), TimeProvider.System), ImpactFinders.Default(fs), null, 160);

        var (outcome, failure) = await drafter.DraftAsync(ws,
            new DraftRequest("getorder-deleted", "삭제된 주문도 읽을 수 있게 한다", [Path.Combine(fx.Root, "Data"), Path.Combine(fx.Root, "Web")]),
            TestContext.Current.CancellationToken);

        Assert.Null(failure);
        var o = outcome!;
        Assert.Equal(ImpactMode.Semantic, o.Check.Impact!.Mode);
        Assert.False(o.ImpactSkippedForErrors);
        Assert.Contains(new AddedImpactTask(o.Added.Single(a => a.Member == "Cancel").TaskId, "Web/OrderService.cs", "Cancel", "T1"), o.Added);
        Assert.Contains(o.Added, a => a is { Path: "Data/OrderDao.cs", Member: "GetOrder" });
        Assert.All(o.Added, a => Assert.Equal("T1", a.FromTaskId));
        Assert.Equal(o.Added.Select((_, i) => "T" + (i + 2)), o.Added.Select(a => a.TaskId));

        var parsed = ChangeSpecParser.Parse(o.Document);
        Assert.All(parsed.Tasks, t => Assert.Equal("G1", t.Group));
        Assert.Equal(1 + o.Added.Count, parsed.Tasks.Count);
        Assert.False(o.Check.HasErrors);
        // Every semantic target is now either a task or (constructor, ambiguous...) still a warning; the draft file is gone.
        Assert.DoesNotContain(o.Check.Issues, i => i.Code == "ImpactMissing" && i.Message.Contains("OrderService.cs#Cancel", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(fx.Root, ".pelly", "drafts", "getorder-deleted.md")));
        Assert.False(Directory.Exists(Path.Combine(fx.Root, "changes")));
    }
}
