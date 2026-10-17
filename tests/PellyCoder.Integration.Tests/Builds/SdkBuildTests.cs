using PellyCoder.Core.Builds;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;

namespace PellyCoder.Integration.Tests.Builds;

/// <summary>Copies the Modern corpus to a temp folder and restores it once for all tests in the class.</summary>
public sealed class ModernCorpusFixture : IAsyncLifetime
{
    public string Root { get; private set; } = "";
    public string Csproj => Path.Combine(Root, "Shop.Api", "Shop.Api.csproj");
    public string OrderService => Path.Combine(Root, "Shop.Api", "Services", "OrderService.cs");

    public async ValueTask InitializeAsync()
    {
        Root = CorpusCopy.Create("Modern");
        var restore = await new SystemProcessRunner(TimeProvider.System).RunAsync(
            new ProcessSpec("dotnet", ["restore", Csproj, "--nologo"], Root, TimeSpan.FromMinutes(5)),
            CancellationToken.None);
        Assert.True(restore.ExitCode == 0, "dotnet restore 실패:\n" + restore.StdOut + restore.StdErr);
    }

    public ValueTask DisposeAsync()
    {
        CorpusCopy.Delete(Root);
        return ValueTask.CompletedTask;
    }
}

public sealed class SdkBuildTests(ModernCorpusFixture fx) : IClassFixture<ModernCorpusFixture>
{
    private BuildVerifier Verifier() => new(new SystemProcessRunner(TimeProvider.System), new BuildOptions());
    private static BuildTool Tool => new(BuildToolKind.Dotnet, "dotnet");

    [Fact]
    public async Task CleanBaseline_HasNoErrors()
    {
        var run = await Verifier().BuildAsync(Tool, fx.Csproj, fx.Root, TestContext.Current.CancellationToken);

        Assert.True(run.Errors.Count == 0, string.Join("\n", run.Errors));
        Assert.Equal(0, run.ExitCode);
        Assert.False(run.TimedOut);
    }

    [Fact]
    public async Task InjectedUndefinedIdentifier_IsOneNewError_InTargetFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseline = await Verifier().BuildAsync(Tool, fx.Csproj, fx.Root, ct);

        var original = await File.ReadAllTextAsync(fx.OrderService, ct);
        const string anchor = "var trimmedReason = reason.Trim();";
        Assert.Contains(anchor, original);
        await File.WriteAllTextAsync(fx.OrderService, original.Replace(anchor, anchor + " undefinedVar++;"), ct);
        try
        {
            var current = await Verifier().BuildAsync(Tool, fx.Csproj, fx.Root, ct);

            var fresh = BaselineDiff.New(baseline.Errors, current.Errors);
            var e = Assert.Single(fresh);
            Assert.Equal("Shop.Api/Services/OrderService.cs", e.File);
            Assert.Equal("CS0103", e.Code);
        }
        finally
        {
            await File.WriteAllTextAsync(fx.OrderService, original, ct);
        }
    }
}
