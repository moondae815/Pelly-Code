using PellyCoder.Core.Fsm;

namespace PellyCoder.Core.Tests.Fsm;

public class ExitCodeTextTests
{
    [Theory]
    [InlineData(0, "성공 (패치 적용)")]
    [InlineData(1, "검증 실패 → 롤백")]
    [InlineData(2, "사용자 거부/중단 → 롤백")]
    [InlineData(3, "LOCATE 실패 또는 후보 모호")]
    [InlineData(4, "환경 오류 (LLM 서버·DB·빌드 도구·인수)")]
    [InlineData(5, "안전 가드에 의한 거부")]
    public void Describe_KnownCodes(int code, string text) => Assert.Equal(text, ExitCodeText.Describe(code));

    [Fact]
    public void Describe_UnknownCode() => Assert.Equal("알 수 없는 종료 코드 9", ExitCodeText.Describe(9));
}
