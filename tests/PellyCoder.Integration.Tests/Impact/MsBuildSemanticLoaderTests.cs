using System.IO.Abstractions;
using Microsoft.CodeAnalysis;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Builds;

namespace PellyCoder.Integration.Tests.Impact;

/// <summary>Copies the SameName fixture to a temp folder and restores it once for all tests in the class.</summary>
public sealed class RestoredSameNameFixture : IAsyncLifetime
{
    public string Root { get; private set; } = "";

    public async ValueTask InitializeAsync() => Root = await CreateRestoredAsync();

    /// <summary>A restored copy of SameName; <paramref name="edit"/> may change the copy before the restore.</summary>
    internal static async Task<string> CreateRestoredAsync(Action<string>? edit = null)
    {
        var root = CorpusCopy.CreateFrom(SameNameSource.Path());
        edit?.Invoke(root);
        // Restore the one project that references the other two, not SameName.sln: on macOS the temp root is
        // /var/... (a link to /private/var/...), and the solution restore then saw Data.csproj under both spellings
        // and restored it twice in parallel, failing with "Web.csproj.nuget.g.props already exists".
        var restore = await new SystemProcessRunner(TimeProvider.System).RunAsync(
            new ProcessSpec("dotnet", ["restore", Path.Combine(root, "Web.Tests", "Web.Tests.csproj"), "--nologo"], root, TimeSpan.FromMinutes(5)),
            CancellationToken.None);
        Assert.True(restore.ExitCode == 0, "dotnet restore 실패:\n" + restore.StdOut + restore.StdErr);
        return root;
    }

    public ValueTask DisposeAsync()
    {
        CorpusCopy.Delete(Root);
        return ValueTask.CompletedTask;
    }
}

internal static class SameNameSource
{
    /// <summary>The checked-in tests/fixtures/impact/SameName folder (found by walking up from the test binaries).</summary>
    public static string Path()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = System.IO.Path.Combine(dir.FullName, "tests", "fixtures", "impact", "SameName");
            if (Directory.Exists(p)) return p;
        }

        throw new DirectoryNotFoundException("tests/fixtures/impact/SameName");
    }

    public static IReadOnlyList<string> Projects(string root) =>
    [
        System.IO.Path.Combine(root, "Data", "Data.csproj"),
        System.IO.Path.Combine(root, "Web", "Web.csproj"),
        System.IO.Path.Combine(root, "Web.Tests", "Web.Tests.csproj"),
    ];
}

public sealed class MsBuildSemanticLoaderTests(RestoredSameNameFixture fx) : IClassFixture<RestoredSameNameFixture>
{
    private static readonly FileSystem Fs = new();

    [Fact]
    public async Task Loads_restored_sdk_projects_healthily()
    {
        using var load = await new MsBuildSemanticLoader(Fs).LoadAsync(SameNameSource.Projects(fx.Root), TestContext.Current.CancellationToken);

        Assert.Null(load.Failure);
        Assert.NotNull(load.Solution);
        Assert.Equal(["Data", "Web", "Web.Tests"],
            load.Solution.Projects.Where(p => p.Language == LanguageNames.CSharp).Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Unrestored_project_is_NotRestored()
    {
        var root = CorpusCopy.CreateFrom(SameNameSource.Path());
        try
        {
            using var load = await new MsBuildSemanticLoader(Fs).LoadAsync(SameNameSource.Projects(root), TestContext.Current.CancellationToken);

            Assert.Equal(SemanticFailureKind.NotRestored, load.Failure?.Kind);
            Assert.Null(load.Solution);
        }
        finally
        {
            CorpusCopy.Delete(root);
        }
    }

    [Fact]
    public async Task Unrestored_project_is_NotRestored_before_opening_the_workspace()
    {
        // A 1 ms limit would turn any MSBuild work into Timeout: NotRestored proves the filesystem pre-check ran first.
        var root = CorpusCopy.CreateFrom(SameNameSource.Path());
        try
        {
            using var load = await new MsBuildSemanticLoader(Fs, TimeSpan.FromMilliseconds(1))
                .LoadAsync(SameNameSource.Projects(root), TestContext.Current.CancellationToken);

            Assert.Equal(SemanticFailureKind.NotRestored, load.Failure?.Kind);
        }
        finally
        {
            CorpusCopy.Delete(root);
        }
    }

    [Fact]
    public async Task Timeout_is_reported()
    {
        using var load = await new MsBuildSemanticLoader(Fs, TimeSpan.FromMilliseconds(1))
            .LoadAsync(SameNameSource.Projects(fx.Root), TestContext.Current.CancellationToken);

        Assert.Equal(SemanticFailureKind.Timeout, load.Failure?.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new MsBuildSemanticLoader(Fs).LoadAsync(SameNameSource.Projects(fx.Root), cts.Token));
    }

    [Fact]
    public async Task Legacy_on_non_windows_is_low_resolution_or_load_failure()
    {
        // macOS only: Windows has the .NET Framework reference assemblies, so legacy may load healthily there.
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS 전용: 레거시 프로젝트가 참조 어셈블리 없이 열리는 경우");
        var root = CorpusCopy.Create("Legacy");
        try
        {
            using var load = await new MsBuildSemanticLoader(Fs)
                .LoadAsync([Path.Combine(root, "Shop.Data", "Shop.Data.csproj")], TestContext.Current.CancellationToken);

            Assert.NotNull(load.Failure);
            Assert.Contains(load.Failure.Kind, new[]
            {
                SemanticFailureKind.LowResolution, SemanticFailureKind.CompileErrors,
                SemanticFailureKind.LoadFailed, SemanticFailureKind.MsBuildNotFound,
            });
        }
        finally
        {
            CorpusCopy.Delete(root);
        }
    }
}
