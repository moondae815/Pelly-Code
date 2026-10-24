using System.IO.Abstractions;
using System.Xml;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Models;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>
/// One build of a group's affected projects. <see cref="Verifiable"/> false means the build cannot judge the group
/// (<see cref="Reason"/> says why, <see cref="FailureKind"/> is <c>GroupUnverifiable</c>) and <see cref="Errors"/> is empty.
/// </summary>
public sealed record GroupBuild(bool Verifiable, string? Reason, string? FailureKind, IReadOnlyList<Diagnostic> Errors);

public interface IGroupVerifier
{
    Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct);
}

/// <summary>
/// Builds a group's affected projects one after another and merges their errors (design §6 "작업 그룹" 3, 5).
/// The same rules apply to the group's baseline and verification builds: a legacy project off Windows, a missing build
/// tool, a timeout, a build that cannot start, or any non-compiler error leaves the group unverifiable.
/// </summary>
public sealed class GroupVerifier(IFileSystem fs, BuildVerifier builds, BuildToolResolver tools, Func<bool> isWindows) : IGroupVerifier
{
    public const string UnverifiableKind = "GroupUnverifiable";

    /// <summary>The errors of <paramref name="current"/> that <paramref name="baseline"/> did not have.</summary>
    public static IReadOnlyList<Diagnostic> NewErrors(GroupBuild baseline, GroupBuild current) =>
        BaselineDiff.New(baseline.Errors, current.Errors);

    public async Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct)
    {
        // Decide everything that needs no build first, so an unverifiable group never starts a build.
        var styles = new List<ProjectStyle>(projects.Count);
        foreach (var project in projects)
        {
            ProjectStyle style;
            try
            {
                style = ProjectStyleDetector.Detect(fs, project);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                return Unverifiable(ws, project, $"프로젝트 파일을 읽을 수 없습니다 ({ex.Message})");
            }
            if (style == ProjectStyle.Legacy && !isWindows())
                return Unverifiable(ws, project, "레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증할 수 있습니다");
            styles.Add(style);
        }

        var resolved = new Dictionary<ProjectStyle, BuildTool>();
        foreach (var style in styles.Distinct())
        {
            var resolution = await tools.ResolveAsync(style, ct);
            if (resolution.Tool is not { } tool)
                return Unverifiable(ws, projects[styles.IndexOf(style)], resolution.Error!);
            resolved[style] = tool;
        }

        var errors = new List<Diagnostic>();
        // A referenced project is built again inside every project that references it: count each error once.
        var seen = new HashSet<Diagnostic>();
        for (var i = 0; i < projects.Count; i++)
        {
            var tool = resolved[styles[i]];
            BuildRun run;
            try
            {
                run = await builds.BuildAsync(tool, projects[i], ws.Root, ct);
            }
            catch (ProcessStartException ex)
            {
                return Unverifiable(ws, projects[i], BuildMessages.CannotStart(tool, ex));
            }

            if (run.TimedOut)
                return Unverifiable(ws, projects[i], "빌드가 제한 시간 안에 끝나지 않았습니다 (build.timeoutSeconds)");
            // NETSDK/MSB/NU or the synthetic BUILD error: the compiler never judged the code.
            if (run.Errors.FirstOrDefault(e => !BuildMessages.IsCompilerError(e)) is { } blocker)
                return Unverifiable(ws, projects[i],
                    BuildMessages.Unverifiable(blocker, "프로젝트를 복원(dotnet restore 또는 Visual Studio 복원)하고 빌드되는지 먼저 확인하세요"));

            errors.AddRange(run.Errors.Where(seen.Add));
        }

        return new GroupBuild(true, null, null, errors);
    }

    private static GroupBuild Unverifiable(Workspace ws, string project, string detail)
    {
        var relative = Path.GetRelativePath(ws.Root, project).Replace('\\', '/');
        return new GroupBuild(false, $"그룹 빌드 검증 불가 ({relative}): {detail}", UnverifiableKind, []);
    }
}
