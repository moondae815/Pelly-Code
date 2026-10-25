using System.Globalization;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Impact;

public enum ImpactMode { Semantic, Name }

public enum SemanticFailureKind { MsBuildNotFound, NotRestored, LoadFailed, LowResolution, CompileErrors, Timeout, ScopeUnknown, NoProject }

/// <summary>Why the semantic analysis could not be used; <see cref="Display"/> is the Korean text shown to the user.</summary>
public sealed record SemanticFailure(SemanticFailureKind Kind, double? Rate = null, int? Count = null)
{
    public string Display => Kind switch
    {
        SemanticFailureKind.MsBuildNotFound => "빌드 도구 없음",
        SemanticFailureKind.NotRestored => "복원 안 됨 (dotnet restore 필요)",
        SemanticFailureKind.LoadFailed => "불러오기 실패",
        SemanticFailureKind.LowResolution => $"해석률 {((Rate ?? 0) * 100).ToString("0.0", CultureInfo.InvariantCulture)}%",
        SemanticFailureKind.CompileErrors => $"컴파일 오류 {Count ?? 0}건",
        SemanticFailureKind.Timeout => "시간 초과",
        SemanticFailureKind.ScopeUnknown => "영향 범위를 확정할 수 없음",
        SemanticFailureKind.NoProject => "프로젝트 없음",
        _ => Kind.ToString(),
    };
}

/// <summary>A spec task the search starts from. <paramref name="Path"/> is workspace-relative with '/'.</summary>
/// <param name="Kind">The task's kind; V2-1c only searches from cs tasks, the field keeps room for sql seeds.</param>
public sealed record ImpactSeed(string TaskId, int Line, string Path, string? ClassName, string Member, TaskKind Kind = TaskKind.CSharp);

/// <summary>A place a change to a seed can break. <paramref name="Reason"/>: 구현|재정의|기반 멤버|호출|멤버 밖.</summary>
public sealed record ImpactTarget(string SeedTaskId, string Path, string Member, string Reason);

/// <param name="Fallback">Why the semantic analysis was replaced by the name-based one; null for a semantic result.</param>
/// <param name="Incomplete">True when the file walk was cut short or a folder or file could not be read, so targets may be missing.</param>
/// <param name="UnsupportedSeeds">Task ids of seeds that are not a method or property.</param>
public sealed record ImpactResult(ImpactMode Mode, SemanticFailure? Fallback, IReadOnlyList<ImpactTarget> Targets, IReadOnlyList<string> UnsupportedSeeds, bool Incomplete = false);

public interface IImpactFinder
{
    Task<ImpactResult> FindAsync(Workspace ws, IReadOnlyList<ImpactSeed> seeds, CancellationToken ct);
}
