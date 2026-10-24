using System.Diagnostics;
using System.Runtime.InteropServices;

public class PublishLayoutTests
{
    private static async Task<string> PublishAsync(bool buildHostOutside)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "s8-publish-" + Guid.NewGuid().ToString("N"));
        var args = new List<string>
        {
            "publish", Path.Combine(TestPaths.Spike, "S8SemanticLoad", "S8SemanticLoad.csproj"),
            "-c", "Release", "-r", RuntimeInformation.RuntimeIdentifier, "--self-contained",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", outDir,
        };
        if (buildHostOutside) args.Add("-p:S8BuildHostOutside=true");
        using var p = Process.Start(new ProcessStartInfo("dotnet", args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var log = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, log);
        return outDir;
    }

    [Fact] public async Task Singlefile_buildhost_form_keeps_buildhost_folders_outside()
    {
        var dir = await PublishAsync(buildHostOutside: true);
        Assert.True(File.Exists(Path.Combine(dir, "BuildHost-netcore", "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "BuildHost-net472", "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe")));

        var plain = await PublishAsync(buildHostOutside: false);
        Assert.False(File.Exists(Path.Combine(plain, "BuildHost-netcore", "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll")));
    }
}
