using System.Globalization;
using System.IO.Abstractions.TestingHelpers;
using System.Resources;
using Microsoft.CodeAnalysis.MSBuild;
using PellyCoder.Core.Impact;

namespace PellyCoder.Core.Tests.Impact;

/// <summary>
/// The loader's exception path, driven by a workspace factory that throws like MSBuildWorkspace does when the
/// BuildHost folders are missing next to the app. No MSBuild runs here.
/// </summary>
public class MsBuildSemanticLoaderFailureTests
{
    // A project path that does not exist: the restore pre-check passes it, so the load reaches the workspace.
    private static readonly string[] Projects = [OperatingSystem.IsWindows() ? @"C:\ws\App\App.csproj" : "/ws/App/App.csproj"];

    /// <summary>The message MSBuildWorkspace throws for a missing build host, read from Roslyn's own resources in the current UI culture.</summary>
    private static string BuildHostMissingMessage() =>
        string.Format(CultureInfo.CurrentCulture,
            new ResourceManager("Microsoft.CodeAnalysis.MSBuild.WorkspaceMSBuildResources", typeof(MSBuildWorkspace).Assembly)
                .GetString("The_build_host_could_not_be_found_at_0", CultureInfo.CurrentUICulture)!,
            Path.Combine("app", "BuildHost-netcore", "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll"));

    private static Task<SemanticLoad> LoadThrowing(Func<Exception> exception) =>
        new MsBuildSemanticLoader(new MockFileSystem(), null, () => throw exception())
            .LoadAsync(Projects, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Missing_build_host_exception_is_MsBuildNotFound()
    {
        // The real shape (S8 form 3 without BuildHost-*): System.Exception "The build host could not be found at '…'".
        using var load = await LoadThrowing(() => new Exception(
            "The build host could not be found at '/app/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll'"));

        Assert.Equal(SemanticFailureKind.MsBuildNotFound, load.Failure?.Kind);
        Assert.Equal("빌드 도구 없음", load.Failure?.Display);
    }

    [Fact]
    public async Task Missing_build_host_inside_wrapping_exceptions_is_MsBuildNotFound()
    {
        var inner = new Exception("The build host could not be found at 'x'");

        using var wrapped = await LoadThrowing(() => new InvalidOperationException("load failed", inner));
        using var aggregated = await LoadThrowing(() => new AggregateException(new IOException("other"), inner));

        Assert.Equal(SemanticFailureKind.MsBuildNotFound, wrapped.Failure?.Kind);
        Assert.Equal(SemanticFailureKind.MsBuildNotFound, aggregated.Failure?.Kind);
    }

    [Fact]
    public async Task Other_exception_is_LoadFailed()
    {
        using var load = await LoadThrowing(() => new InvalidOperationException("Build host is not connected."));

        Assert.Equal(SemanticFailureKind.LoadFailed, load.Failure?.Kind);
    }

    [Fact]
    public async Task Messages_are_pinned_to_English_during_the_load_on_a_Korean_machine()
    {
        var before = CultureInfo.CurrentUICulture;
        var vslang = Environment.GetEnvironmentVariable("VSLANG");
        var cliLanguage = Environment.GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
        try
        {
            (string? VsLang, string? CliLanguage)? seen = null;
            using var load = await new MsBuildSemanticLoader(new MockFileSystem(), null, () =>
                {
                    // What the build host process started here would inherit.
                    seen = (Environment.GetEnvironmentVariable("VSLANG"), Environment.GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE"));
                    throw new Exception(BuildHostMissingMessage());
                })
                .LoadAsync(Projects, TestContext.Current.CancellationToken);

            // Roslyn's Korean resource would not classify; the English one does.
            Assert.Equal(SemanticFailureKind.MsBuildNotFound, load.Failure?.Kind);
            Assert.Equal(("1033", "en"), seen);

            // Scoped to the load: nothing leaks to the rest of pelly or the processes it starts later.
            Assert.Equal("ko-KR", CultureInfo.CurrentUICulture.Name);
            Assert.Equal(vslang, Environment.GetEnvironmentVariable("VSLANG"));
            Assert.Equal(cliLanguage, Environment.GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}
