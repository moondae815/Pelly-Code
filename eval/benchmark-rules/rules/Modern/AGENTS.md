# AGENTS.md

## Pelly rules
<!-- pelly rules init 초안(2026-10-24)에 팀 관례 한 줄(SYSDATETIME)을 사람이 더했습니다. -->
- [*] 새 주석은 꼭 필요할 때만 달고, 쓸 때는 한국어로 작성한다
<!-- 근거: 주석 30개 중 30개가 한국어 -->
- [async] .Result나 .Wait()로 비동기 결과를 기다리지 않는다 | forbid: \.Result\b|\.Wait\(\)
<!-- 근거: async 메서드 15개, .Result/.Wait()/GetResult() 사용 0건 -->
- [dapper] SQL 값은 Dapper 파라미터로 넘기고 문자열 연결·보간으로 만들지 않는다
<!-- 근거: using Dapper; 파일 2개 -->
- [dbo] SQL에서 현재 시각은 GETDATE() 대신 SYSDATETIME()을 쓴다
<!-- 팀 관례: Shop.Api·Shop.Jobs의 SQL은 모두 SYSDATETIME()을 쓴다 -->
