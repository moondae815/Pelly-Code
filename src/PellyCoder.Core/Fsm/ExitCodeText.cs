namespace PellyCoder.Core.Fsm;

/// <summary>Korean one-line meaning of each process exit code (PRD FR-00).</summary>
public static class ExitCodeText
{
    public static string Describe(int code) => code switch
    {
        0 => "성공 (패치 적용)",
        1 => "검증 실패 → 롤백",
        2 => "사용자 거부/중단 → 롤백",
        3 => "LOCATE 실패 또는 후보 모호",
        4 => "환경 오류 (LLM 서버·DB·빌드 도구·인수)",
        5 => "안전 가드에 의한 거부",
        _ => $"알 수 없는 종료 코드 {code}",
    };
}
