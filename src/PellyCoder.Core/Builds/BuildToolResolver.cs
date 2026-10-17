using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;

namespace PellyCoder.Core.Builds;

public enum BuildToolKind { Dotnet, MsBuild }

public sealed record BuildTool(BuildToolKind Kind, string Executable);

public sealed record BuildToolResolution(BuildTool? Tool, string? Error);

/// <summary>Chooses dotnet for SDK projects and MSBuild.exe (config, then vswhere) for legacy ones.</summary>
public sealed class BuildToolResolver(IFileSystem fs, IProcessRunner processes, BuildOptions options, Func<bool>? isWindows = null)
{
    private static readonly TimeSpan VswhereTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<bool> _isWindows = isWindows ?? OperatingSystem.IsWindows;

    public async Task<BuildToolResolution> ResolveAsync(ProjectStyle style, CancellationToken ct)
    {
        switch (style)
        {
            case ProjectStyle.Sdk:
                return new BuildToolResolution(new BuildTool(BuildToolKind.Dotnet, "dotnet"), null);
            case ProjectStyle.None:
                throw new ArgumentException("프로젝트 형식이 없으면 빌드 도구를 고를 수 없습니다.", nameof(style));
        }

        if (!_isWindows())
            return new BuildToolResolution(null, "레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증할 수 있습니다");

        if (!string.IsNullOrWhiteSpace(options.MsbuildPath) && fs.File.Exists(options.MsbuildPath))
            return new BuildToolResolution(new BuildTool(BuildToolKind.MsBuild, options.MsbuildPath), null);

        var found = await FindWithVswhereAsync(ct);
        return found is not null
            ? new BuildToolResolution(new BuildTool(BuildToolKind.MsBuild, found), null)
            : new BuildToolResolution(null, "MSBuild.exe를 찾을 수 없습니다. Visual Studio Build Tools를 설치하거나 build.msbuildPath를 설정하세요 (pelly doctor)");
    }

    // Windows 미확인(실기): vswhere 출력 줄바꿈·인코딩은 CI windows-integration 테스트로만 확인한다.
    private async Task<string?> FindWithVswhereAsync(CancellationToken ct)
    {
        var vswhere = fs.Path.Combine(
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)",
            @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!fs.File.Exists(vswhere)) return null;

        ProcessResult result;
        try
        {
            result = await processes.RunAsync(new ProcessSpec(
                vswhere,
                ["-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"],
                fs.Path.GetDirectoryName(vswhere) ?? ".",
                VswhereTimeout), ct);
        }
        catch (ProcessStartException)
        {
            return null;
        }

        if (result.ExitCode != 0 || result.TimedOut) return null;
        var first = result.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return first is not null && fs.File.Exists(first) ? first : null;
    }
}
