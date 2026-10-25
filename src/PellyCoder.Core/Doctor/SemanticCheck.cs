using System.IO.Abstractions;
using PellyCoder.Core.Impact;

namespace PellyCoder.Core.Doctor;

/// <summary>
/// The <c>pelly doctor</c> row for semantic impact search (V2-1c): is the build host shipped, and, when a project is given,
/// does it load healthy. Fail only when the build host is missing; a load problem is a Warn because the name-based search
/// takes over.
/// </summary>
public static class SemanticCheck
{
    public const string Name = "의미 분석";

    public static async Task<DoctorCheck> RunAsync(IFileSystem fs, ISemanticLoader loader, string baseDir, string? projectPath, CancellationToken ct)
    {
        try
        {
            OfflineSelfTest.CheckBuildHost(baseDir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(Name, DoctorStatus.Fail, $"{ex.GetType().Name}: {ex.Message}", OfflineSelfTest.BuildHostAction,
                Facts(DoctorFacts.BuildHost, "missing", DoctorFacts.Semantic, "notChecked"));
        }

        if (projectPath is null)
            return new(Name, DoctorStatus.Ok, "보조 프로세스 있음 (--project를 주면 불러오기까지 확인합니다)", null,
                Facts(DoctorFacts.BuildHost, "present", DoctorFacts.Semantic, "notChecked"));

        SemanticFailure? failure;
        try
        {
            using var load = await loader.LoadAsync([fs.Path.GetFullPath(projectPath)], ct);
            failure = load.Failure;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = new SemanticFailure(SemanticFailureKind.LoadFailed);
        }

        return failure is null
            ? new(Name, DoctorStatus.Ok, "프로젝트를 불러와 컴파일까지 확인했습니다", null,
                Facts(DoctorFacts.BuildHost, "present", DoctorFacts.Semantic, "ok"))
            : new(Name, DoctorStatus.Warn, $"이름 기준으로 대신합니다: {failure.Display}", null,
                Facts(DoctorFacts.BuildHost, "present", DoctorFacts.Semantic, "fallback", DoctorFacts.Failure, failure.Kind.ToString()));
    }

    private static Dictionary<string, string> Facts(params string[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2) map[pairs[i]] = pairs[i + 1];
        return map;
    }
}
