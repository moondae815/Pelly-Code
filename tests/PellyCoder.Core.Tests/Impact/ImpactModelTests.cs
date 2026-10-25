using PellyCoder.Core.Impact;

namespace PellyCoder.Core.Tests.Impact;

public class ImpactModelTests
{
    [Theory]
    [InlineData(SemanticFailureKind.MsBuildNotFound, null, null, "빌드 도구 없음")]
    [InlineData(SemanticFailureKind.NotRestored, null, null, "복원 안 됨 (dotnet restore 필요)")]
    [InlineData(SemanticFailureKind.LoadFailed, null, null, "불러오기 실패")]
    [InlineData(SemanticFailureKind.LowResolution, 0.028, null, "해석률 2.8%")]
    [InlineData(SemanticFailureKind.CompileErrors, null, 3, "컴파일 오류 3건")]
    [InlineData(SemanticFailureKind.Timeout, null, null, "시간 초과")]
    [InlineData(SemanticFailureKind.ScopeUnknown, null, null, "영향 범위를 확정할 수 없음")]
    [InlineData(SemanticFailureKind.NoProject, null, null, "프로젝트 없음")]
    public void Display_matches_the_user_facing_text(SemanticFailureKind kind, double? rate, int? count, string expected) =>
        Assert.Equal(expected, new SemanticFailure(kind, rate, count).Display);
}
