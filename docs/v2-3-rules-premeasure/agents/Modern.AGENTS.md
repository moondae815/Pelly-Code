# AGENTS.md

## Pelly rules
<!-- pelly rules init 초안 (2026-10-24). 검토해서 고치거나 지우세요. 형식: - [태그,...] 규칙(100자 이내) [| forbid: 정규식] -->
- [*] 주석은 한국어로 작성한다
<!-- 근거: 주석 30개 중 30개가 한국어 -->
- [async] .Result나 .Wait()로 비동기 결과를 기다리지 않는다 | forbid: \.Result\b|\.Wait\(\)
<!-- 근거: async 메서드 15개, .Result/.Wait()/GetResult() 사용 0건 -->
- [dapper] SQL 값은 Dapper 파라미터로 넘기고 문자열 연결·보간으로 만들지 않는다
<!-- 근거: using Dapper; 파일 2개 -->
