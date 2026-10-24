# AGENTS.md

## Pelly rules
<!-- pelly rules init 초안(2026-10-24)에 팀 관례 한 줄(SYSDATETIME)을 사람이 더했습니다. -->
- [procedure] 프로시저 본문 처음에 SET NOCOUNT ON을 둔다
<!-- 근거: 프로시저 10개 중 10개에 SET NOCOUNT ON -->
- [*] 현재 시각은 GETDATE() 대신 SYSDATETIME()을 쓴다 | forbid: (?i)\bGETDATE\s*\(
<!-- 팀 관례: 코퍼스 SQL 객체는 모두 SYSDATETIME()을 쓴다 -->
