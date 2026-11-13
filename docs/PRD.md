# Pelly-Coder CLI 제품 요구사항 정의서 (PRD)
> **타겟 모델:** `Qwen3.6-35B-A3B` (4-bit Quantization)  
> **타겟 런타임:** .NET 10 (LTS) CLI  
> **운영 환경:** 폐쇄망 Windows 11 x64 개발 PC + 폐쇄망 내부 원격 Ollama 서버  
> **개발 환경:** 외부 개발 노트북(macOS) → 검증 후 폐쇄망으로 이관  
> **문서 버전:** v1.10 (2026-10-19)

### 변경 이력
| 버전 | 날짜 | 주요 변경 |
| :--- | :--- | :--- |
| v1.0 | 2026-10-17 | 초안 |
| v1.1 | 2026-10-17 | 표기 오류 수정(`pelly`, `<think>`), KPI 재정의(불변 조건/벤치마크/기준 하드웨어), .NET 10 단일화, 범위·Non-goals 추가, CLI UX·LOCATE·컨텍스트 예산·토큰 계산·런타임 백엔드 명세, Baseline 빌드·퍼지 매칭 규칙·인코딩·롤백 명세, SQL 안전장치(ScriptDom 가드·최소 권한·호스트 화이트리스트), 비기능 요구사항, 평가·테스트 전략, 로드맵 M0 추가, 미결 사항 정리 |
| v1.2 | 2026-10-17 | 기본 LLM 서버를 Ollama로 변경(`/api/generate` raw 모드, keep_alive, Flash Attention + KV 양자화), 주 사용 환경을 Windows로 지정, Windows 특화 요구사항(파일 잠금, 콘솔 인코딩, 경로, 통합 인증) 및 CI 매트릭스 추가, 미결 사항 Q8~Q10 추가 |
| v1.3 | 2026-10-17 | 폐쇄망 운영·외부 개발 후 이관 구조 반영: 원격 Ollama 서버(관리자 관리), 완전 오프라인 동작, self-contained 배포 패키지, 이관 절차(§11), 외부 개발 환경 구성(§8.5), 로그 반출용 비식별 리포트. 레거시 .NET Framework 프로젝트 빌드 검증을 v1에 포함(`MSBuild.exe` + `vswhere`, Q8 해소). 로드맵에 M4(폐쇄망 이관·현장 검증) 추가, 미결 사항 Q9~Q13 갱신 |
| v1.4 | 2026-10-17 | §12 v2 로드맵 추가: 작업 명세 파일과 순차 실행(`pelly apply`), 테스트 우선 검증, 프로젝트 규칙 파일. 진입 조건·v2 KPI·v2에서도 하지 않을 것 명시 |
| v1.5 | 2026-10-17 | IDE 연동(Visual Studio·Rider External Tools, `--line`, `--pause-on-exit`) 추가, v2 로드맵에 V2-0 GUI 승인 창 추가 |
| v1.6 | 2026-10-17 | M0 스파이크 결과 반영: think 단계를 교정 시도부터 사용(`llm.thinkMode`), think 예산은 서버 `num_predict`로 제한, SQL 최소 권한 `VIEW DEFINITION`, 토크나이저 확정, 미결 사항 Q1~Q4 해소 |
| v1.7 | 2026-10-18 | M2a 반영(상세는 설계 문서 §3 D24·D25, §7.3, §8.4, §8.7.1, §8.9): FR-00 옵션 정리(`--line`, `--strict-baseline`, `--warn-as-error`, `--method`·요청문 선택, 요청문 생략 시 대화형 입력), FR-06 Baseline을 LOCATE 끝에서 시작하고 CS 외 오류면 `BuildUnverifiable`로 중단, 모델 제어 토큰 문자열 차단(종료 코드 4), 멤버 모양 검사 구체화 |
| v1.8 | 2026-10-18 | M2b 반영(상세는 설계 문서 §3 D26~D28, §5, §8.1, §8.4, §8.6, §8.10, §8.12, §10.6): FR-00 `pelly sql` 옵션 정리, FR-07 `dotnet user-secrets` 제외(R3)·허용 목록이 비면 접속 안 함(R4)·서버/데이터베이스 키 중복과 Failover Partner 거부·원본 대상의 금지 구문은 LOCATE에서 종료 5(R2)·금지 구문 범위(`ALTER DATABASE` 변형, `EXEC … OPENDATASOURCE`), §8.1 벤치마크 30건(M2b, R9)·50건(M3) |
| v1.9 | 2026-10-18 | M3a 반영(상세는 설계 문서 §8.2, §8.5, §8.7, §8.9, §8.10, §8.12~§8.14, §11): FR-00 `--test`(C# 전용)·`--verbose`·`--pause-on-exit`·`report`(`--since`·`--sanitize`·`--out`) 구현 상태, FR-02 DTO 슬라이스(enum·public 필드·시그니처 한 단계), FR-06 5번 `--test` 구체화(프로젝트 종류별 실행, 새로 실패한 테스트만, `test.timeoutSeconds`, 요청과 모순되는 테스트의 알려진 한계 25%)와 변화 없음 가드, FR-08 전체 doctor(`--project`·`--sanitize`·숨은 `--offline-selftest`), FR-09 비식별 허용 목록(`TEST`, 앵커·ASCII 숫자·버전은 숫자로 시작), §5 대기 시간 `QueueSeconds`, 로드맵 M3를 M3a(완료)·M3b로 분리 (M3a R1~R11) |
| v1.10 | 2026-10-19 | M3b 반영(상세는 설계 문서 §8.1, §8.2, §8.13, §9.2, §10.4, §10.6, §11): §2.2·§8.1 검증 불변 조건은 독립 빌드·컴파일 결과로만 판정하고 동작 판정 실패는 모델 실패(`Judge`)로 센다, 외부 벤치마크 50건(Legacy 18 · Modern 12 동작 판정 · SQL 20)과 3회 반복 기준선(평균 47.67건, 위반 0), §7.2·§8.2 튜닝 후보 3개 모두 R4로 거절돼 기본값 유지와 회귀 차단 절차(`--repeat 3` + `compare`), §11.1 실제 반입 패키지 구성(`pelly-0.9.0-win-x64.zip` 13개 파일, SBOM 도구 버전 고정, 라이선스 게이트와 잠정 예외 하나, 버전 기록은 모델 digest — GGUF SHA-256은 M4), CI 실행 6회(잡 키 5개), 로드맵 M3b 결과 (M3b T1~T11) |
| v1.11 | 2026-10-19 | M4 준비 반영(상세는 설계 문서 §10.6, §11): §8.1 벤치마크 스위트(`suite.json` 루트 목록, 작업 `file` = `<루트 이름>/<경로>`, 내장 스위트 `eval/benchmark/suite.json`, `--suite`·`--keep-work`)와 사전 검사 `validate`(정적, `--deep`은 원본에서 판정 시험 실행), 실행 조건에 Ollama 버전·모델 digest 기록(요약 `formatVersion` 2, 1도 읽음, `compare`는 digest·Ollama 버전이 다르면 판정 거부), `record`(버전 기록, `modelGgufSha256` 추가), `export`(비식별 반출), FR-08 doctor SQL 행의 인증 방식(Windows 인증 / SQL 로그인)과 `--sanitize`에서 로그인 이름 제거, §11.1 두 번째 반입 zip `pelly-field-kit-0.9.1-win-x64.zip`(`pelly-eval.exe` 단일 파일 + 현장 절차 문서 + 스위트 템플릿), `pelly`의 값이 필요한 옵션에 값이 없으면 종료 4와 .NET 텔레메트리 끔 환경 변수(0.9.1), §9 M4 현장 키트 절차와 반입 전 사람 결정 다섯 가지, 버전 `0.9.1` (M4 준비 T1~T10) |
| v1.12 | 2026-10-19 | 반입 전 사람 결정 반영: ① `Microsoft.Data.SqlClient.SNI.runtime` 라이선스 예외를 사람이 승인(잠정 해제, 1.0.0 게이트 통과 조건 충족) ② pelly 자체 `LICENSE`는 Payletter 사내 전용(All rights reserved)으로 두 zip 맨 위에 넣음(반입 zip 14개 파일·현장 키트 14개 파일) ③ S7 Rider 실기 확인은 하지 않고 「실기 미확인」으로 종결(현장 개발 PC는 Visual Studio, VS는 M4 현장 확인표) ④ 폐쇄망 정보 Q9~Q13은 질문지 `docs/m4-field-questionnaire.md`로 담당자에게 받음 |
| v1.13 | 2026-10-24 | V2-3 프로젝트 규칙 구현: 규칙을 `pelly.rules.md` 대신 작업 공간 루트 `AGENTS.md`의 `## Pelly rules` 절로 두고 `pelly rules init`·`pelly rules check` 추가, 규칙을 사용자 메시지(CONTEXT 뒤)에 넣어 시스템 부분을 고정, `budget.system` 600 → 450·`budget.rules` 150, `forbid`는 모든 규칙을 원본 대비 추가된 줄에서 검사하고 정규식 시간 초과는 종료 코드 4, "규칙 도입 효과(+5%p)" KPI는 미측정 (설계 결정 1–6은 `docs/superpowers/plans/2026-10-24-pelly-v2-3-project-rules.md`) |
| v1.14 | 2026-10-24 | V2-1a 구현: 명세 파일·spec new/check·순차 apply. 그룹·영향 탐색은 V2-1b·c |
| v1.15 | 2026-10-25 | V2-1b 구현: 작업 그룹(시그니처·이름 변경, 영향 범위 빌드, 오류 귀속·재실행, 그룹 되돌리기). 영향 탐색·벤치마크는 V2-1c |
| v1.16 | 2026-10-25 | V2-1c 구현: 영향 탐색(심볼·이름 대체 표시), BuildHost 배포, doctor 의미 분석 행, 명세 완료율 report, apply E2E 벤치마크 |
| v1.17 | 2026-10-26 | V2-0 구현: `pelly ui cs`·`pelly ui sql` 승인 창(Avalonia 12.1.4, 요청 입력·진행·diff 승인·다시 실행, `--yes`·`--pause-on-exit` 불가), Avalonia 네이티브 DLL 셋을 `pelly.exe` 옆에 둠(반입 zip 고정 파일 17개, `pelly.exe` +9.3 MB), 두 번째 라이선스 예외(`Avalonia.Angle.Windows.Natives`, 2026-10-26 사람 승인)와 패키지별 예외 범위(`packages`), 빌드 시점 수집 끄기(`AVALONIA_TELEMETRY_OPTOUT=1`). Windows 실행은 미확인 |
| v1.18 | 2026-10-26 | V2-1d 구현: 운영 DB 읽기 전용 정의 읽기(`VIEW DEFINITION`, `sql.production.*`), `pelly sql pull`·`pelly sql check`, 받은 뒤 운영 변경 시 `pelly sql` 중단(`SqlProdChanged`), SQL 영향 탐색, doctor 「SQL 운영(읽기 전용)」 행. 사람 결정: 운영 읽기 접속 허용, pull 기본 출처는 운영, 영향 탐색 세 가지 모두 이번 단계, §12 로드맵에 올림 |
| v1.19 | 2026-10-27 | PowerShell 자동완성: `pelly completion powershell`이 명령·옵션·정해진 값(`--think`)을 Tab으로 완성하는 정적 스크립트를 출력(명령 목록에서 생성, ASCII, 5.1·7, 파일 자리는 기본 경로 완성). 반입 zip 변화 없음. 설계 `docs/superpowers/specs/2026-10-27-powershell-completion-design.md` |
| v1.20 | 2026-10-27 | V2-4 `pelly spec draft` 구현: 모델이 변경 요청과 범위 색인(서명만)으로 작업 줄 초안을 쓰고, 기존 `spec check`(문법·대상·문구 안 이름)와 영향 탐색을 거친 뒤 **사람이 승인해야** `changes/<id>.md`로 씀. 호출부·구현체 전파는 모델이 아니라 영향 탐색이 작업 줄로 더함. §12.7 첫 줄 조정, §12.1에 초안 원칙과 토큰 예산 예외(계획 호출 생각 2048 + 답 1024), 새 §12.8. 근거는 S6 작업 분해 프로브(`docs/s6-planning-probe.md`), 설계 `docs/superpowers/specs/2026-10-27-spec-draft-design.md`. Windows 실행은 미확인 |
| v1.21 | 2026-10-28 | M4 현장 모델 기준선: 질문지 회신으로 현장 모델이 `qwen3.6:35b-a3b-q4_K_M`(ID `07d35212591f`)임을 확인(Q10 해소)하고 같은 파일로 외부 기준선을 다시 잼 — 50건 × 3회 평균 47.00(94%), 위반 0, 모순 요청 우회 7/20. 기준선 문서 `docs/m4-field-model-baseline.md`(이전 `docs/m4-gguf-baseline.md`는 대체됨) |
| v1.22 | 2026-10-29 | V2-5 `pelly test` 구현: 테스트가 없는 C# 메서드 하나의 현재 동작을 고정하는 테스트(특성 테스트) 1~5개를 모델이 쓰고, 코드가 형태·단언·환경 의존·빌드·현재 코드 통과·재실행 일치·변이 검출(본문 → 예외)로 판정한 뒤 **사람이 승인해야** 기존 테스트 프로젝트에 남김. §12.4 「테스트 작성」 줄 조정, §12.2에 V2-5 행, 새 §12.9. 근거는 2026-10-28 `Report_AndSanitize` 조사(`docs/m4-field-model-baseline.md` 3절), 설계 `docs/superpowers/specs/2026-10-29-pelly-test-characterization-design.md`, 예비 측정 `docs/chartest-premeasure.md`(Mac, Modern만). Windows 실행과 현장 모델 확인은 미확인 |
| v1.23 | 2026-11-04 | V2-6 `pelly asp` 구현(1단계 A1): Classic ASP(VBScript) 파일(`.asp`·`.inc`) 하나에서 `Function`·`Sub` 하나 또는 줄 범위를 모델이 고치고, 코드가 실행 없이 패치·인코딩·구조·안전·이름·규칙·VBScript 컴파일(Windows `cscript`, 컴파일만)·SP 존재(개발 DB)로 판정한 뒤 **사람이 승인해야** 씀. 모든 검사는 원본 기준선 대비 새로 생긴 것만 셈. 편집 영역 밖은 원본 바이트 그대로(CP949, 줄 끝 섞임). 「실행 검증 없음」을 검토와 기록에 고정 문구로 남김. §1.4 범위 표에 ASP 줄, §12.2에 V2-6 행, 새 §12.10. 설계 `docs/superpowers/specs/2026-11-04-pelly-asp-design.md`, 예비 측정 `docs/asp-premeasure.md`(Mac, 컴파일 검사 건너뜀). Windows 실행과 현장 확인(`deploy/field-results.md` 16절)은 미확인 |
| v1.24 | 2026-11-05 | V2-7 `pelly js`·`pelly css` 구현(1단계 W1): 따로 있는 클라이언트 JavaScript(`.js`)·CSS(`.css`) 파일 하나에서 함수·규칙 하나 또는 줄 범위를 모델이 고치고, 코드가 브라우저 실행·외부 도구 없이 패치·인코딩·문법(JS는 `Acornima` 해석기, CSS는 손 토큰 해석기)·**문법 수준**(기본 원본 수준, `web.jsTarget`)·대상 모양·안전으로 막고, 이름·API(ES 판·jQuery 버전)·CSS 속성·덮어쓰기는 경고만 한 뒤 **사람이 승인해야** 씀. 모든 검사는 원본 기준선 대비 새로 생긴 것만 셈. 편집 영역 밖은 원본 바이트 그대로이고 사이트 루트·순수 ASCII 인코딩은 `asp.siteRoot`·`asp.encoding`을 그대로 읽음(사람 결정 2026-11-04). 「브라우저 실행 검증 없음」을 검토와 기록에 고정 문구로 남김. §1.4 범위 표에 JS·CSS 줄, §11.1 반입 구성에 `Acornima` 1.9.0 버전(BSD-3-Clause)과 `pelly.exe` 크기 변화, §12.2 로드맵에 V2-7 행, 새 §12.11. 설계 `docs/superpowers/specs/2026-11-04-pelly-js-css-design.md`, 예비 측정 `docs/web-premeasure.md`(Mac). Windows 실행과 현장 확인(`deploy/field-results.md` 17절)은 미확인 |
| v1.25 | 2026-11-06 | V2-7 `pelly js`·`pelly css` 2단계(W2): 페이지(`asp.extensions` + `.htm`·`.html`) 안 클라이언트 `<script>`·`<style>` 블록 하나에서 함수·규칙·줄 범위를 W1과 같은 판정(패치·인코딩·문법·문법 수준·대상 모양·안전은 막고 이름·API·속성은 경고)으로 고침. 블록마다 원본 기준선을 따로 두어 새로 생긴 것만 세고, `<%= %>`는 자리 표시로 해석하며 `<% 문장 %>`이 섞인 블록은 괄호 짝 비교로 낮추고, 블록 경계·블록 안 서버 코드·블록 밖 바이트가 바뀌면 거절(`WebBlockChanged`·`WebServerCodeChanged`·`WebOutsideBlock`)하며 서버 값이 필요하면 `pelly asp --lines`를 안내. `pelly asp`는 범위가 걸친 블록에 `JsSyntax`·`JsLevel`·`JsSafety`·`CssSyntax`·`CssSafety`(막음)·`JsNames`·`JsApi`(경고) 행을 더하고(`asp-v2`, 규칙 검사는 여전히 페이지 안 JS를 보지 않음), `pelly edit`은 대상 위치로 js·css·asp를 고름. 이름 검사는 같은 페이지 다른 블록·include·`<script src>`의 선언을 알고 jQuery 기준은 설정 → 페이지 → 사이트 순. 프롬프트 `js-page-v1`·`css-page-v1`, `summary.json` `web`에 `container`·`block`·`jquerySource`, `pelly report`에 「페이지 안 K건」, §12.11 W2 단락. 설계 `docs/superpowers/specs/2026-11-06-pelly-web-w2-design.md`. Windows 실행과 현장 확인(`deploy/field-results.md` 17절)은 미확인 |
| v1.26 | 2026-11-09 | V2-6 `pelly asp` 2단계(A2): 명세(`pelly spec`·`apply`)에 `asp` 작업(`<파일>#Proc`·`#Class.Proc`·`#L<시작>-<끝>`, 줄 범위는 asp만)을 더해 「SP 변경 + 그 SP를 부르는 ASP 수정」을 한 명세로 순서대로 실행. `spec check`는 `AspTargetRequired`·`LinesOutOfFile`·`LinesNotSupported`·`AspLinesAfterEdit`(같은 파일을 먼저 고치는 작업 뒤의 줄 범위)를 오류로 더하고 `KindMismatch`는 `asp.extensions`를 씀. 그룹(`[G1]`)에 cs·sql·asp를 섞을 수 있고(V2-1의 「그룹은 cs만」이 바뀜, `SqlInGroup` 없어짐) 하나라도 실패하면 그룹 전체를 되돌리며 C# 빌드·테스트 검증은 그룹의 cs 작업에만 하고 cs가 없는 그룹은 검증을 건너뜀. 그룹 없는 sql 적용 뒤 asp가 실패하면 다음 빈 그룹 번호를 안내하는 경고. SP → ASP 영향: 사이트의 SP 호출 색인(`asp.siteRoot`·`asp.extensions`·`asp.spCallFunctions`)으로 명세에 없는 호출을 `ImpactMissing`(SP당 최대 10개, include 표시)으로 경고하며, `spec check`는 `--impact`일 때, `apply`는 미완료 sql 작업이 있으면 `sql.production`과 상관없이 돌고 「계속할까요?」 확인에 합쳐짐(목록 `.pelly/impact/<id>-sql.json`·`impact-sql.json`의 `asp` 배열). asp 작업은 이번 apply에서 적용했거나 이미 완료한 sql 작업 SP의 **파일 기준 서명**을 모델 입력과 SP 검사에 쓰고(개발 DB 없이도, 해석 실패는 경고 후 DB 값), 적용 뒤 매개변수가 바뀐 SP의 고치지 않은 호출을 경고하고 `apply.json`의 `aspCallersNotUpdated`와 `pelly report`에 셈. `spec draft`는 asp 작업을 제안하지 않으며 경고만 보이고, 예비 측정에서 `SqlInGroup`으로 실행되지 않던 초안(`docs/spec-draft-premeasure.md`)이 이제 실행될 수 있음. 한계: 동적 SP 이름, `rs.Open "EXEC x @a=1"`, 결과 열 변경은 찾지 못함. §12.10 A2 단락, 설계 `docs/superpowers/specs/2026-11-09-pelly-asp-a2-design.md`. Windows 실행과 현장 확인(`deploy/field-results.md` 16절)은 미확인 |
| v1.33 | 2026-11-13 | 반입 실행 파일에서 .NET 런타임을 뺌(framework-dependent): 현장 개발 PC에 .NET 10 SDK가 있으므로(질문지 3-2) `pelly.exe`·`pelly-eval.exe`가 PC의 .NET 10 런타임으로 실행됨. `pelly.exe` 약 138MB → 65MB, 반입 zip 64MB → 약 29MB, 현장 키트 52MB → 약 18MB. SBOM에서 런타임 팩을 빼고 실행 호스트 팩만 남김. CI가 실행 파일이 100MB를 넘으면 실패(런타임이 다시 들어간 경우). 설치 조건은 `deploy/install.md` |
| v1.32 | 2026-11-13 | 현장 확인 안내 `deploy/field-checklist.md`: 흩어진 현장 확인(ASP·JS·CSS·`pelly test`·`ui`·자동완성·spec draft·운영 DB 계정·질문지·기준선 측정·반출)을 할 순서대로 모으고 항목마다 명령과 결과 칸 번호를 붙임. 결과는 그대로 `field-results.md`에 적음. 반입 zip과 현장 키트 zip에 둘 다 넣고(반입 zip에는 `field-results.md`도), 문서 안 `pelly` 명령의 옵션이 실제 명령에 있는지 테스트로 확인 |
| v1.31 | 2026-11-11 | `pelly doctor`에 「사이트 색인」 행(색인한 페이지·`.js` 수, 크기 합계, 1MB가 넘어 뺀 파일 수, 한도에 걸려 잘리면 Warn). 현장 확인표 W14(사이트 규모) |
| v1.30 | 2026-11-11 | W3 정리: JS 호출 색인이 `function*`·메서드 단축 정의를 호출로 세지 않음, 같은 이름 함수 중 이름을 바꾼 대상을 재시도에서 다시 찾음, 대상을 지웠을 때 「대상 함수를 지우지 마십시오」, 어긋남 경고가 `asp` 작업을 「(asp)」로 표시, 맨 이름 `#fnSearch`가 `Common.fnSearch`로 풀린 경우의 이름 바꾸기 따라가기, 참조만 있는 파일도 영향 탐색, 적용 뒤 확인이 파일을 한 번만 읽음. 폼 필드 검사: 주석·JS 문자열 안 `<form>`은 보내는 쪽이 아님, JS로 정한 action 주소의 쿼리 키를 보냄으로 셈, 「작업 중 경고 N건 중 M건 해결」을 경고끼리 비교해 셈, include로 쓰는 `.asp`를 `.inc`처럼 주소를 풂, 깨진 캐시 다시 만들기, 같은 페이지로 가는 JS action 여럿의 필드 합침, include로도 쓰고 직접도 여는 `.asp`는 두 곳 기준 모두로 주소를 풂(사이트 색인 캐시 판 6) |
| v1.29 | 2026-11-10 | V2-7 3단계 W3b: 폼 필드 이름 짝 검사. 서버 페이지(와 include)의 `Request.Form("k")`·`Request("k")` 읽기와 그 페이지로 보내는 폼(필드, `<form action>`, JS의 `frm.action =` 대입, action 없으면 자기 자신)·jQuery ajax(`data` 객체 키)를 사이트 색인에 담고(캐시 판 5), 편집 전후를 비교해 새로 생긴 어긋남만 경고: 「보내지 않음」(고친 보내는 쪽이 키를 빠뜨림, 비슷한 이름 표시)과 「아무도 보내지 않음」(고친 페이지가 새로 읽는 키). 같은 파일에서 같은 페이지로 보내는 폼은 묶어 보고, 키가 변수인 읽기·필드를 다 알 수 없는 보내는 쪽·주소를 모르는 보내는 쪽·보내는 쪽이 없는 페이지는 건너뜀. `pelly asp`의 경고 행 `FormFields`, `pelly js`의 `Fields`(「폼 필드」), apply 끝 다시 계산(`apply.json`의 `formFieldMismatches`, `pelly report`의 「폼 필드 어긋남 N건」, 「작업 중 폼 필드 경고 N건 중 M건은 뒤 작업이 해결함」). 종료 코드는 바꾸지 않음. 설계 `docs/superpowers/specs/2026-11-10-pelly-web-w3-design.md` §5 |
| v1.28 | 2026-11-10 | V2-7 3단계 W3a: 명세(`pelly spec`·`apply`)에 `js`·`css` 작업(`#함수`·`#선택자`·`#L<시작>-<끝>`, 페이지 블록 포함)을 더하고, 같은 파일을 먼저 고치는 작업 뒤의 `#L` 오류를 종류와 상관없이 적용. 명세의 `js` 작업과 `pelly js --signature`는 대상 함수의 이름·매개변수를 바꿀 수 있음(새 최상위 선언은 여전히 거절, `summary.json`의 `web.signatureChange`). 사이트 색인에 JS 호출(`.js`·페이지 `<script>`·이벤트 속성·`javascript:`)과 `<script src>` 관계를 더함(캐시 판 4). `spec check --impact`가 명세에 없는 부르는 곳을 `JsImpactNotInSpec`으로 경고(apply 시작 확인에는 넣지 않음), apply 뒤에 서명이 바뀐 함수의 남은 호출을 경고(`apply.json`의 `jsCallersNotUpdated`, `pelly report`), 뒤 작업에 바뀐 서명 전달과 옛 이름 대상 따라가기, 그룹 밖 어긋남 경고 `JsCallerMismatch`, `pelly asp`의 경고 행 `JsSignature`. 부르는 곳의 자동 치환·CSS 선택자 변경은 하지 않음. 폼 필드 이름 짝 검사는 W3b. 설계 `docs/superpowers/specs/2026-11-10-pelly-web-w3-design.md` |
| v1.27 | 2026-11-09 | 페이지 요소(id·name) 검사: `pelly js`(페이지 대상)와 `pelly asp`에 경고 행 「요소」(`summary.json` 키 `web.checks.Elements`, `asp.checks.JsElements`)를 더함. 페이지의 `<script>` 블록이 `getElementById`·`getElementsByName`·`$("#id")`·`$("[name=x]")`로 찾는 id·name이 페이지·include·이 파일을 include하는 페이지·`<script src>` 파일(속성, `.id =`, `setAttribute`)에 없을 때, 편집 때문에 새로 끊긴 (종류, 값)만 경고하고 막지 않음. id 대신 name만 있거나 대소문자만 다르면 힌트를 붙이고, `pelly asp`는 HTML만 고쳐 id를 지운 편집도 잡음. 서버 코드가 든 값(`id="row<%= i %>"`)은 앞부분으로 맞춤. 속성 접근(`f.custId`)·`onclick=`·따로 있는 `.js`는 보지 않음(`.js`는 「건너뜀(페이지가 아님)」). 프롬프트·설정은 그대로. §12.11 W2 단락, 설계 `docs/superpowers/specs/2026-11-09-pelly-web-element-check-design.md` |

---

## 1. 제품 개요 (Product Overview)

### 1.1 프로젝트 명칭 및 정의
* **프로젝트 명칭:** **Pelly-Coder CLI** (Small-Active MoE 특화 결정론적 코딩 에이전트)
* **실행 명령어:** `pelly`
* **배포 형태:**
  * **폐쇄망 (운영):** `win-x64` 단일 실행 파일 zip 패키지. .NET 런타임은 넣지 않고 대상 PC의 .NET 10 런타임(.NET 10 SDK에 포함, 현장 개발 PC에 설치됨 — 질문지 3-2)으로 실행합니다(framework-dependent, v1.33부터. 그 전에는 self-contained)(§11).
  * **외부 개발 환경:** .NET Global Tool (패키지 ID: `PellyCoder.Cli`)
* **대상 모델:** `Qwen3.6-35B-A3B` (GGUF `Q4_K_M`, 폐쇄망 원격 Ollama 서버로 서빙)
* **실행 방식:** 일반 JIT 실행. Roslyn·`Spectre.Console.Cli`의 리플렉션 의존성 때문에 **Native AOT와 트리밍은 v1 범위에서 제외**합니다.

### 1.2 기획 배경 및 문제 정의
`Qwen3.6-35B-A3B`는 전체 35B 파라미터 중 토큰당 3B만 활성화되는 구조로 사내 GPU 서버에서 높은 추론 속도를 제공합니다. 폐쇄망에서는 외부 코딩 AI 서비스를 쓸 수 없으므로 이 모델이 현실적인 선택지입니다. 하지만 기존 자율 에이전트(ReAct 방식)에 4비트 양자화 모델을 그대로 연결하면 다음 한계가 나타날 것으로 예상합니다. (아래 수치는 가설이며 **M0에서 측정으로 확인**합니다.)
1. **컨텍스트 열화 (Lost in the Middle):** 입력 프롬프트가 길어질수록(가설: 8K 토큰 초과) 기존 변수명이나 아키텍처 규칙을 망각함.
2. **Tool Calling 파싱 실패:** 다중 중첩 JSON 스키마 안에 C# 코드나 SQL 쿼리를 이스케이프(`\"`, `\n`)하여 생성할 때 구문 오류 및 무한 재시도 루프 발생.
3. **논리적 환각 (Hallucination):** 활성 파라미터 3B의 추론 한계로 존재하지 않는 .NET API, Dapper 확장 메서드, 잘못된 테이블 컬럼명을 생성함.

### 1.3 제품 목표
모델의 자율성에 의존하는 대신 **엄격한 유한 상태 머신(FSM) 워크플로우, Roslyn AST 기반 마이크로 컨텍스트(Micro-Context), 컴파일러/DB 엔진 기반의 결정론적 검증 루프**를 결합하여, **"초기 응답 지연 1초 미만, 검증을 통과하지 못한 코드는 절대 남기지 않는"** 폐쇄망용 코딩 CLI 도구를 구축합니다.

### 1.4 범위 (Scope) 및 제외 범위 (Non-goals)
| 구분 | v1 포함 | v1 제외 (v2 이후 검토) |
| :--- | :--- | :--- |
| **C#** | 단일 파일 내 메서드·생성자·프로퍼티 수정, 같은 파일의 `using` 추가, 같은 클래스 내 `private` 헬퍼 메서드 추가, 한 파일 내 다중 SEARCH/REPLACE 블록. **SDK 스타일 프로젝트와 레거시 .NET Framework 프로젝트 모두 빌드 검증** | 다중 파일 동시 수정, 새 파일 생성, DTO/인터페이스 시그니처 변경, 대규모 리팩터링 |
| **SQL** | 리포지토리 내 `.sql` 파일의 단일 SP/함수/뷰 수정 | **DB 직접 배포(`ALTER` 실행)**, 다중 객체 동시 수정, 마이그레이션 생성 |
| **Classic ASP** (V2-6에서 추가) | `.asp`·`.inc` 파일 하나의 VBScript `Function`·`Sub` 하나 또는 줄 범위 수정, 인코딩(CP949·UTF-8)과 줄 끝 보존, 실행 없는 판정(구조·안전·이름·`cscript` 컴파일·SP 존재) | **실행 검증(IIS·브라우저)**, `global.asa`, 서버 JScript, 다중 파일·새 파일·include·지시문 변경, XSS 검사, COM 구성 요소, ASP.NET 이전 |
| **JavaScript·CSS** (V2-7 단계에서 추가) | 따로 있는 `.js`·`.css` 파일 하나의 함수·규칙 하나 또는 줄 범위 수정, 인코딩(CP949·UTF-8, `@charset`)과 줄 끝 보존, 실행 없는 판정(문법·문법 수준·대상 모양·안전은 막고, 이름·API·속성·덮어쓰기는 경고) | **브라우저 실행 검증**, Node·npm·ESLint 같은 외부 도구, 압축·라이브러리 파일, 페이지 안 `<script>`·`<style>`(W2), TypeScript·JSX·SCSS·LESS, CSS 선택자 변경과 그 쓰는 곳(JS 함수 이름·매개변수 변경은 명세 작업과 영향 경고로 다룸 — W3a), 다중 파일·새 파일, XSS 검사 |
| **환경** | 폐쇄망 원격 Ollama 서버 연동, **인터넷 없이 완전 오프라인 동작** | 인터넷 접속이 필요한 기능 일체(자동 업데이트, 텔레메트리, 모델·토크나이저 다운로드), 외부 클라우드 LLM API |
| **공통** | 단발성 명령 실행 | 대화형 채팅 모드, IDE 플러그인 |

여러 작업에 걸친 변경, 시그니처 변경, 테스트 기반 검증은 v2에서 다룹니다(§12).

---

## 2. 핵심 성과 지표 (Target Metrics & KPIs)

### 2.1 측정 조건
* **KPI 최종 판정 환경 (폐쇄망):** 폐쇄망 Windows 개발 PC + 폐쇄망 Ollama 서버(사양 Q9). 응답 지연은 **네트워크 왕복을 포함**해 측정하며, 서버를 혼자 쓰는 상태(동시 요청 없음)를 기준으로 합니다.
* **사전 측정 환경 (외부):** §8.5 외부 개발 환경. 여기서 나온 수치는 참고치이며 KPI 판정에 쓰지 않습니다.
* **벤치마크 세트:**
  * **외부용:** 실제 코드 구조를 모사한 샘플 코퍼스 기반 30건(M2b: C# 18건 + SQL 12건) → **50건(M3b, 완료: C# 30건 + SQL 20건)**(§8.1 참조)
  * **폐쇄망용:** 실제 코드베이스 기반 같은 규모의 세트(M4에서 폐쇄망 안에서 작성). 현장 키트(§11.1)의 `pelly-eval.exe`와 `deploy/field-*.md`, 스위트 템플릿으로 만들고 `pelly-eval validate`로 점검합니다
* 같은 모델 파일(GGUF SHA-256 일치)과 같은 Ollama 버전으로 측정합니다(Q10). 외부 사전 측정의 모델은 GGUF가 아니라 `nvfp4`라 digest로 기록하고 GGUF 해시 대조는 M4 작업입니다(§11.1).

### 2.2 지표
| 지표 항목 | 목표 수치 (Target) | 측정 기준 및 달성 전략 |
| :--- | :--- | :--- |
| **검증 불변 조건** | **100% (예외 없음)** | 최종 승인된 패치는 항상 빌드/SQL 검증을 통과함. 통과하지 못하면 자동 롤백 (성공률 지표가 아닌 시스템 보장 사항). 벤치마크에서는 **독립 빌드(C#)·NOEXEC 컴파일(SQL) 결과로만** 판정한다. 동작 판정·문자열 판정 실패는 모델 실패이며 위반이 아니다 (M3b T4) |
| **초기 응답 지연 (TTFT)** | **< 1.0초** | 입력 4,096 토큰 이내 제한 + 프롬프트 캐시 사용. 네트워크 왕복 포함. 참고: 외부 Mac(M5 Pro)에서 캐시 없는 4,300토큰 입력은 1.56초(S2) — 폐쇄망 서버 사양(Q9) 확인 후 목표 재검토 |
| **추론 예산 제어 (Thinking)** | **≤ 512 토큰** | 교정 시도부터 THINK 단계 사용(`llm.thinkMode = retry`). 서버 `num_predict = 512`로 제한하고, 잘리면 마지막 완결 줄까지 남긴 뒤 CODE 단계로 전환 (FR-05, 설계 D21·D22) |
| **패치 포맷 파싱 성공률** | **> 99.5%** | JSON Tool Calling 대신 `SEARCH/REPLACE` 텍스트 블록 파서 사용 |
| **최종 작업 성공률** | **≥ 85% (잠정)** | 최초 시도 1회 + 자가 교정 최대 3회 안에 검증 통과 및 판정 기준 충족. 폐쇄망 기준선 측정 후 확정 (Q6) |
| **단일 작업 완료 시간** | **중위값 < 15초 / P90 < 30초** | 코드 탐색 → 패치 생성 → 증분 빌드 검증까지. 대상 프로젝트 단위 빌드 기준. 레거시 프로젝트는 빌드 시간 실측 후 별도 목표 설정 가능 |

---

## 3. 시스템 아키텍처 및 상태 머신 워크플로우

### 3.1 배포 구성
```text
[폐쇄망]
┌──────────────────────────────┐          ┌──────────────────────────┐
│ Windows 11 개발 PC            │  HTTP    │ Ollama 서버 (GPU)         │
│  pelly.exe                   │ ───────> │  Qwen3.6-35B-A3B Q4_K_M  │
│  dotnet SDK / MSBuild.exe    │  NDJSON  │  (서버 관리자 관리)        │
│  대상 소스 코드               │ <─────── │                          │
└──────────────┬───────────────┘          └──────────────────────────┘
               │ SqlClient (Windows 인증)
               ▼
        개발 DB (SQL Server)
```

### 3.2 FSM 다이어그램
```text
[User Request]
      │
      ▼
[Phase 0: PREFLIGHT] ──(Ollama 서버 연결·모델 확인 / 설정 검증 / 제어 토큰 검사)  ·  Baseline 빌드·에러 스냅샷은 LOCATE 끝에서 시작
      │
      ▼
[Phase 1: LOCATE] ──(Roslyn 심볼 검색 / ripgrep / SQL 객체 검색)──> 대상 심볼 특정
      │        └──[후보 다수]──> 사용자 선택 (비대화형이면 종료 코드 3)
      ▼
[Phase 2: SLICE]  ──(Micro-Context Slicer)──> 수정 대상 + 참조 정의만 추출 (예산 §3.3)
      │
      ▼
[Phase 3: EDIT]   ──(2단계 생성: THINK ≤512 → CODE)──> SEARCH/REPLACE 블록
      │
      ▼
[Phase 4: APPLY]  ──(백업 → 퍼지 매칭 → 원자적 쓰기)
      │        └──[매칭 실패]──┐
      ▼                        │
[Phase 5: VERIFY] ──(Roslyn 구문 검사 → dotnet build 또는 MSBuild.exe / SQL 가드 → NOEXEC)
      │                        │
      ├──[신규 에러 있음]──────┴──> 재시도 횟수 < 3 ? ──[예]──> Phase 2(패치된 파일 기준 재SLICE)
      │                                              └─[아니오]──> [ROLLBACK] ──> 종료 코드 1
      ▼
[Phase 6: REVIEW] ──(최종 diff 표시 → 사용자 승인)──[거부]──> [ROLLBACK] ──> 종료 코드 2
      │
      ▼
   [DONE] ──> 종료 코드 0
```
* 재시도 횟수: **최초 시도 1회 + 자가 교정 최대 3회**. SEARCH 매칭 실패도 1회로 셉니다.
* 재시도할 때는 패치가 적용된 상태에서 에러 위치를 포함해 **다시 SLICE**합니다(Fix-forward).
* Ctrl+C 등 어느 단계에서 중단되더라도 ROLLBACK을 실행합니다.

### 3.3 컨텍스트 예산 (`num_ctx = 8192`)
| 구성 요소 | 상한 (토큰) | 비고 |
| :--- | ---: | :--- |
| 시스템 프롬프트 (few-shot 포함) | 600 | |
| 사용자 요청 | 200 | 초과하면 요약 요청 없이 에러로 거부 |
| 코드/스키마 컨텍스트 | 3,000 | 슬라이서가 우선순위대로 잘라냄 |
| 재시도 피드백 | 296 | **누적하지 않고 최신 피드백으로 교체** |
| **입력 합계 (하드 리밋)** | **4,096** | |
| Think 출력 | 512 | FR-05 |
| Code 출력 (`num_predict`) | 2,048 | |
| 여유분 | 1,536 | 템플릿 토큰·오차 흡수 |

### 3.4 토큰 계산 방식
Ollama에는 공개된 토큰화 전용 엔드포인트가 없으므로 다음과 같이 나눕니다.
* **사전 예산 계산 (입력):** `Microsoft.ML.Tokenizers`에 Qwen vocab(`tokenizer.json`)을 로드한 **로컬 토크나이저**로 계산합니다. chat template 토큰을 포함해 계산하고, 오차를 고려해 여유분(§3.3)을 둡니다. 폐쇄망에서는 다운로드할 수 없으므로 **토크나이저 파일을 실행 파일에 내장**합니다.
* **사후 보정:** 응답의 `prompt_eval_count`/`eval_count`를 로그에 기록하고, 로컬 계산값과의 오차를 벤치마크에서 주기적으로 확인합니다.
* **Think 예산 (실시간):** 스트림으로 받은 누적 텍스트를 로컬 토크나이저로 계산합니다. 스트림 청크 수는 참고값으로만 씁니다.

---

## 4. 상세 기능 요구사항 (Functional Requirements)

### FR-00. CLI 명령 및 사용자 경험
* **명령 형식:**
  ```text
  pelly cs     <file.cs>  ["<요청>"] [--class <Name>] [--method <Name>] [--line <n>]
  pelly sql    <file.sql> ["<요청>"] [--object <Name>] [--line <n>]
  pelly doctor [--project <csproj>] [--sanitize [--out <폴더>]]   # 환경 점검 (FR-08)
  pelly report [--since <yyyy-MM-dd>] [--sanitize [--out <폴더>]]   # 실행 기록 요약 (FR-09)
  pelly ui cs  <file.cs>  ["<요청>"] [--class <Name>] [--method <Name>] [--line <n>]   # 승인 창 (V2-0)
  pelly ui sql <file.sql> ["<요청>"] [--object <Name>] [--line <n>]   # 승인 창 (V2-0)
  pelly completion powershell   # PowerShell 자동완성 스크립트 출력
  pelly spec draft <id> ["<요청>"] --scope <경로>… [--dry-run] [--yes] [--force]   # 모델 명세 초안 (V2-4)
  ```
* **대상 지정 (`pelly cs`):** `--method`(+선택 `--class`), `--line <n>`(커서 줄이 속한 멤버, `--class`/`--method`와 함께 쓸 수 없고 1 이상), 또는 둘 다 생략(요청문의 식별자 → 없으면 모델이 멤버 목록에서 선택, 최대 60개). `--method`와 요청문은 모두 선택입니다. 요청문을 생략하면 대화형 실행에서는 입력을 묻고 비대화형이면 종료 코드 4입니다.
* **대상 지정 (`pelly sql`, M2b):** 파일에 프로시저·함수·뷰 정의가 하나면 자동으로 정하고, 여럿이면 `--object <이름>`(예: `dbo.MyProc`) 또는 `--line <n>`(그 줄을 포함하는 정의)으로 지정합니다. `--line`과 `--object`는 함께 쓸 수 없고(종료 4), 여러 객체에서 지정이 없으면 대화형은 선택 UI, 비대화형은 종료 코드 3입니다. SQL에는 모델이 대상을 고르는 단계가 없습니다. 편집은 대상 객체 배치 하나에만 적용되며 `--strict-baseline`·`--warn-as-error`는 없습니다(`pelly cs` 전용). 공통 옵션(`--yes`, `--dry-run`, `--max-retries`, `--config`, `--think`, `--model`, `--endpoint`)은 같습니다. 요청문 생략은 `pelly cs`와 같이 대화형에서만 허용됩니다.
* **빌드 검증 옵션:** `--strict-baseline`(원본에 이미 있던 CS 오류도 종료 코드 4로 처리), `--warn-as-error`(새 경고도 오류로 취급).
* **입력 제한:** 요청문이나 대상 파일에 모델 제어 토큰 문자열(`<|im_end|>` 등)이 있으면 서버를 부르기 전에 종료 코드 4로 끝납니다.
* **공통 옵션:** `--dry-run`(패치만 보여주고 적용 안 함), `--yes`(최종 승인 생략), `--max-retries <n>`(기본 3), `--verbose`(실행 후 단계별 타이밍·토큰·시도 요약과 실행 폴더·`prompt.txt` 경로 출력, 프롬프트 원문은 출력하지 않음), `--pause-on-exit`, `--config <path>`
* **`pelly cs` 전용 옵션:** `--test`(검증 시 테스트 실행, FR-06 5번), `--verify-test <이름>`(패치 전 실패·패치 뒤 통과해야 하는 테스트, 여러 번 가능, V2-2a), `--expect-break <이름>`(이 변경으로 실패해도 되는 기존 테스트, `--verify-test`와 함께, V2-2a), `--warn-as-error`, `--strict-baseline`
* **옵션 구현 상태 (M3a):** `--test`·`--verbose`·`--pause-on-exit`·`report`(`--since`·`--sanitize`·`--out`)는 M3a에서 구현했습니다. `--pause-on-exit`는 실행 결과를 보여준 뒤 키 입력을 기다리며, 입력이 리디렉션되면 기다리지 않고, Ctrl+C로 중단한 실행은 기다리지 않습니다(알려진 한계; 콘솔 키 대기는 Windows 미확인).
* **승인 창 (`pelly ui`, V2-0):** `pelly cs`·`pelly sql`과 같은 실행(같은 FSM·검증·롤백)을 Avalonia 창에서 합니다. 요청 입력, 진행 줄, diff, 「적용」/「거부」가 창 안에 있어 표준 입력이 필요 없습니다. `--yes`와 `--pause-on-exit`는 종료 코드 4로 거부하고, `--dry-run`은 diff와 「확인」만 보이며, 끝난 뒤에도 창이 남아 요청을 고쳐 다시 실행할 수 있습니다. 종료 코드는 마지막 실행의 코드이고 한 번도 실행하지 않고 닫으면 2입니다. 실행 중 창 닫기는 Ctrl+C와 같아 원본 복원 뒤 2입니다. 명령 검증 오류는 창 안에, 명령줄 파싱 오류는 콘솔에 보이고 둘 다 종료 4입니다. 네이티브 DLL 셋이 `pelly.exe` 옆에 없으면 `pelly ui`만 종료 4입니다. 구현 상태는 §12.2의 V2-0 표시를 보십시오.
* **IDE 연동:** 사용자는 Visual Studio와 JetBrains Rider를 혼용합니다. 두 IDE의 External Tools에 `pelly cs "<현재 파일>" --line <커서 줄>`을 등록해, 커서가 있는 메서드를 대상으로 실행합니다. 요청문을 생략하면 콘솔에서 입력받습니다. `--pause-on-exit`는 결과를 보여준 뒤 키 입력을 기다립니다(External Tools 콘솔 창이 바로 닫히지 않게). 설정 가이드는 반입 패키지 문서에 포함합니다.
* **최종 승인 (REVIEW):** 검증을 통과하면 `Spectre.Console`로 컬러 diff를 표시하고 `[y/N]` 확인을 받습니다. 거부하면 원본으로 복구합니다.
* **종료 코드:**

  | 코드 | 의미 |
  | :--- | :--- |
  | 0 | 성공 (패치 적용) |
  | 1 | 검증 실패 → 롤백 |
  | 2 | 사용자 거부/중단 → 롤백 |
  | 3 | LOCATE 실패 또는 후보 모호 (비대화형) |
  | 4 | 환경 오류 (LLM 서버·DB·빌드 도구) |
  | 5 | 안전 가드에 의한 거부 (FR-07) |

### FR-01. 대상 탐색 (`SymbolLocator`)
1. `--class`/`--method`/`--object`가 지정되면 Roslyn(SQL은 ScriptDom)으로 정확히 일치하는 심볼을 찾습니다.
2. 지정되지 않았으면 요청문에서 식별자 후보(PascalCase 토큰, 따옴표·백틱 안 문자열)를 규칙 기반으로 추출해 파일 안 심볼과 대조합니다.
3. 그래도 특정되지 않으면 파일의 **심볼 이름·시그니처 목록만** 모델에 주고 하나를 고르게 합니다(출력은 목록 중 하나의 이름으로 제한).
4. 후보가 여러 개면 `SelectionPrompt`로 사용자에게 선택하게 하고, 비대화형(`--yes`/CI)이면 종료 코드 3으로 끝냅니다.

### FR-02. AST 및 스키마 기반 마이크로 컨텍스트 슬라이서 (Micro-Context Slicer)
* **C# / .NET 코드 슬라이싱 (`RoslynSlicer`):**
  * `Microsoft.CodeAnalysis.CSharp`로 수정 대상 메서드 본문 전체를 추출합니다. 파일 단위 구문 분석만 사용하므로 프로젝트 형식(SDK/레거시)과 관계없이 동작합니다.
  * 같은 클래스의 다른 멤버는 본문(`{ ... }`)을 제거하고 시그니처만 남깁니다. DI 필드 선언부, 파일의 `using` 목록, 입출력 DTO의 데이터 멤버 정의를 발췌해 가상 컨텍스트를 조립합니다.
  * 레거시 코드의 C# 언어 버전(예: C# 7.3)을 프로젝트 파일에서 읽어 시스템 프롬프트에 명시합니다. 사용할 수 없는 최신 문법(record, 파일 범위 namespace 등)을 생성하지 않도록 하기 위함입니다.
  * **DTO 슬라이스 (M3a):** 대상이 쓰는 프로젝트 안 선언 타입을 프로퍼티·public 필드·`enum` 멤버까지 한 줄씩 담습니다(타입 5개, 타입당 멤버 30개 상한). 대상을 품은 타입의 필드·프로퍼티 타입 중 대상이 쓰는 것과, 데이터가 없는 타입(저장소 인터페이스)의 메서드 시그니처에 나온 타입을 한 단계 더 따라갑니다. `bin`·`obj`·`.pelly`·`.git`·`.vs`·`node_modules`·`packages`·`TestResults`는 훑지 않고, 링크는 따라가지 않으며, 스캔은 디렉터리 2000개에서 멈춥니다.
  * 예산을 넘으면 다음 순서로 제거합니다: 다른 멤버 시그니처 → DTO 정의 → `using` 목록. 수정 대상 본문은 절대 자르지 않으며, 본문만으로 예산을 넘으면 종료 코드 4로 알립니다.
* **MS SQL Server 스키마/SP 슬라이싱 (`SqlSchemaSlicer`):**
  * ScriptDom으로 SP 본문을 파싱해 참조 객체를 추출합니다(기존 객체는 `sys.dm_sql_referenced_entities` 병행).
  * 참조 테이블의 `sys.columns`, `sys.indexes` 메타데이터만 조회해 DDL 요약본으로 주입합니다.
* **토큰 하드 리밋:** 입력 합계 **4,096 토큰** 이하 (§3.3).

### FR-03. Zero-JSON Diff 출력 및 패처 (`SearchReplacePatcher`)
* **강제 출력 포맷** (한 응답에 여러 블록 허용):
  ```text
  <<<<<<< SEARCH
  [기존 코드 원본 (공백 및 들여쓰기 포함)]
  =======
  [수정할 새로운 코드]
  >>>>>>> REPLACE
  ```
* **매칭 단계:** ① 정확 일치 → ② 줄끝 공백·줄바꿈 문자 정규화 → ③ 줄 앞 들여쓰기 정규화. 앞 단계에서 찾지 못할 때만 다음 단계로 넘어갑니다.
* **유일 매칭 원칙:** 어느 단계든 일치 위치가 **2곳 이상이면 실패**로 처리합니다(잘못된 위치 패치 방지).
* **들여쓰기 재적용:** ③단계로 매칭됐으면 원본과 SEARCH 블록의 들여쓰기 차이만큼 REPLACE 블록 전체를 보정합니다.
* **매칭 실패 피드백:** "SEARCH 블록 불일치"와 함께 가장 비슷한 원본 구간(±3줄)을 다음 시도에 전달합니다.
* **마커 충돌:** 대상 파일에 이미 `<<<<<<<`, `=======`, `>>>>>>>`로 시작하는 줄이 있으면(병합 충돌 흔적) 작업 전에 경고하고 중단합니다.

### FR-04. 파일 안전장치 (`FileGuard`)
* **인코딩·줄바꿈 보존:** 읽을 때 BOM·인코딩(UTF-8, UTF-8 BOM, EUC-KR/CP949 — `CodePagesEncodingProvider` 등록)과 줄바꿈(CRLF/LF)을 감지하고, 쓸 때 그대로 유지합니다. 매칭은 정규화된 사본에서 수행합니다. 레거시 코드에는 CP949 파일이 많을 수 있으므로 필수 테스트 대상입니다.
* **백업:** 패치 전 원본을 `.pelly/backup/<run-id>/`에 저장합니다.
* **원자적 쓰기:** 같은 폴더의 임시 파일에 쓴 뒤 교체합니다(Windows는 `File.Replace`, 그 외 `File.Move(overwrite: true)`).
* **Windows 파일 잠금:** Visual Studio, 백신, MSBuild 노드가 파일을 잡고 있으면 공유 위반(sharing violation)이 납니다. 짧은 간격으로 최대 5회 재시도하고, 그래도 실패하면 잠금 중인 파일 경로를 알리고 종료 코드 4로 끝냅니다.
* **동시 수정 감지:** 시작 시 파일 해시·mtime을 기록하고, 적용·복구 직전에 다시 확인합니다. 외부(에디터)에서 바뀌었으면 덮어쓰지 않고 중단합니다.
* **형상 관리 연동:** git 저장소이고 대상 파일에 커밋되지 않은 변경이 있으면 경고합니다(진행 여부는 사용자 확인). 폐쇄망에서 git 외 형상 관리 도구(SVN, TFVC 등)를 쓰면 이 경고는 생략하고 백업에만 의존합니다(Q12).

### FR-05. 추론 예산 제어기 (Thinking Budget Controller)
**2단계 생성** 방식을 사용합니다.
* **사용 시점 (`llm.thinkMode`, 기본 `retry`):** 첫 시도는 생각 없이(빈 think 프리필) 바로 코드를 만들고, 빌드·매칭 실패로 교정할 때부터 THINK 단계를 켭니다. `always`(매 시도), `off`(사용 안 함)로 바꿀 수 있습니다. 근거: M0 S3 — 이 모델은 512토큰 안에 생각을 끝내지 못했고(30/30), 생략해도 10건 결과가 같았으며 평균 9.0초 → 1.7초.
1. **THINK 단계:** think 전용 샘플링 값(§7.2)으로 `num_predict = 512`, stop `</think>`로 요청합니다. 서버가 512토큰에서 멈추면(`done_reason = length`) 잘린 것으로 봅니다. 클라이언트가 스트림을 끊으면 다음 요청이 약 2초 늦어지므로 끊지 않습니다(S2).
2. 잘린 경우 think 내용을 마지막 완결 줄까지만 남깁니다.
3. **CODE 단계:** `[원래 프롬프트] + <think>[think 내용]</think>\n<<<<<<< SEARCH\n`(생각을 생략하면 빈 think)을 프리필(Prefill)로 붙여 code 전용 샘플링 값으로 요청합니다.
* Ollama `/api/generate`를 **`raw: true`** 로 호출합니다. 이 모드에서는 Ollama가 템플릿을 적용하지 않으므로, `QwenChatTemplate`이 chat template을 직접 조립하고 프리필 문자열을 끝에 붙입니다. `/api/chat`과 `think` 파라미터는 프리필을 제어할 수 없어 쓰지 않습니다.
* 재요청은 앞부분 프롬프트가 같아 Ollama가 접두사 KV 캐시를 재사용합니다(S2: 2단계 TTFT 중앙값 299ms). 모델 언로드로 인한 재로딩을 막기 위해 모든 요청에 `keep_alive`(기본 `"30m"`)를 지정합니다.
* think 태그 문자열은 모델 chat template에서 확인한 값을 설정으로 주입합니다(미결 사항 Q1).

### FR-06. 결정론적 검증 및 자가 치유 하네스 (Deterministic Verification Harness)
* **Baseline 스냅샷 (LOCATE 끝):** 대상 프로젝트와 빌드 도구가 정해지면(LOCATE 끝) 패치 전 프로젝트 빌드를 시작해 에러 집합(파일, 에러 코드, 메시지 — 줄 번호 제외)을 기록합니다. 빌드는 모델 생성과 병렬로 돌고 파일을 처음 쓰기 전에 완료를 기다립니다. 검증 단계에서는 **새로 생긴 에러만** 실패로 판단합니다. Baseline에 CS 오류만 있으면 사용자에게 알리고 계속 진행합니다(`--strict-baseline`이면 종료 코드 4). NETSDK·MSB·NU 같은 CS가 아닌 오류가 있으면(예: 패키지 복원 안 됨) 컴파일러가 돌지 않은 것이므로 `--strict-baseline`과 무관하게 종료 코드 4로 중단하고 복원을 안내합니다.
* **빌드 도구 선택 (`BuildToolResolver`):**

  | 프로젝트 형식 | 판별 기준 | 빌드 도구 |
  | :--- | :--- | :--- |
  | SDK 스타일 (`net48` 타겟 포함) | `.csproj` 루트가 `<Project Sdk="...">` | `dotnet build` |
  | 레거시 .NET Framework | `Sdk` 속성 없음 (`ToolsVersion`, `packages.config` 등) | Visual Studio / Build Tools의 `MSBuild.exe` |

  * `MSBuild.exe` 경로는 `vswhere.exe -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`로 찾고, 설정 `build.msbuildPath`로 직접 지정할 수도 있습니다.
  * 찾지 못하면 LOCATE 끝(Baseline 시작 전)에서 종료 코드 4로 끝내고, `pelly doctor`에서 설치 안내를 표시합니다.
* **C# 검증 파이프라인 (`BuildVerifier`):**
  1. **사전 검사:** 패치된 파일을 프로젝트의 언어 버전에 맞춘 Roslyn `SyntaxTree`로 파싱해, 구문 오류가 있으면 빌드 없이 바로 피드백합니다.
  2. **빌드:** 대상 파일이 속한 가장 가까운 `.csproj`만 빌드합니다. 패키지 복원은 하지 않습니다(폐쇄망 개발 PC에 이미 복원돼 있다고 가정).
     * SDK 스타일: `dotnet build <project.csproj> --no-restore --nologo -v q -clp:ErrorsOnly`
     * 레거시: `MSBuild.exe <project.csproj> /t:Build /nologo /v:q /clp:ErrorsOnly /nr:true`
     * 두 도구의 에러 출력 형식(`파일(줄,열): error CSxxxx: 메시지`)은 같으므로 파서는 하나로 처리합니다.
  3. **피드백:** 신규 에러를 `[파일명(줄번호): CSxxxx - 메시지]`로 추출하고, **에러 줄 앞뒤 ±3줄 코드**를 함께 모델에 전달합니다(296 토큰 이내, 에러가 많으면 앞에서부터).
  4. **경고 정책:** 신규 경고는 사용자에게만 보고합니다. `--warn-as-error`이면 에러와 똑같이 처리합니다.
  5. **테스트 (선택, `pelly cs --test`, M3a):** 빌드 통과 후 테스트 프로젝트(대상 프로젝트를 참조하고 이름에 `Test`가 있는 프로젝트, 또는 `test.projects`)의 테스트를 실행합니다. 프로젝트 종류별로 xunit v3는 `dotnet run … -- -result-trx`, 그 밖의 Testing Platform은 종료 코드만, VSTest는 `dotnet test --logger trx`, 레거시는 설정 `test.command`(`{project}`·`{dll}`·`{trx}`)로 돌립니다.
     * **패치 전 상태와 비교해 새로 실패한 테스트만** 실패로 봅니다. 패치 전 테스트가 환경 문제로 돌지 않으면 알림을 남기고 빌드로만 검증합니다. 실패한 테스트 이름과 메시지(또는 종료 코드)를 피드백하며, "요청한 변경을 되돌려 테스트를 통과시키지 말라"는 문장을 함께 보냅니다.
     * 테스트 실행 제한은 `test.timeoutSeconds`(기본 300초)이며 빌드 제한과 별개입니다.
     * **변화 없음 가드:** 편집 결과가 원본 대상과 공백 정규화 후 같으면(C#·SQL 공통) 쓰기 전에 거부하고 재시도합니다. 성공으로 끝나는 무변경 수정을 막기 위함입니다.
     * **알려진 한계:** 요청이 기존 테스트와 모순될 때 모델이 요청을 로그 출력으로 바꾸는 식으로 회피하고 테스트를 통과해 종료 0으로 끝나는 일이 약 25%(20회 중 5회, `docs/m3a-e2e.md`) 있습니다. 의미상 회피는 판정할 수 없으므로 `--test`를 써도 diff를 확인해야 합니다.
* **MS SQL 검증 파이프라인 (`SqlSyntaxVerifier`):**
  1. **안전 가드 (FR-07)** 통과 여부를 먼저 확인합니다.
  2. **오프라인 구문 검사:** ScriptDom 파서로 파싱합니다(DB 접속 없음). 파서 버전은 대상 SQL Server 버전에 맞춰 설정 `sql.compatLevel`로 고릅니다.
  3. **객체 존재 검사:** ScriptDom으로 추출한 참조 테이블·뷰·SP를 `sys.objects`에서 조회합니다. 지연 이름 확인(Deferred Name Resolution) 때문에 `NOEXEC`가 놓치는 존재하지 않는 객체를 여기서 Error 208로 보고합니다. `#temp` 테이블은 제외합니다.
  4. **컴파일 검사:** 개발 DB 세션에서 `SET NOEXEC ON;` 상태로 가실행해 구문 오류, 잘못된 컬럼명(Error 207) 등을 잡습니다. 전체를 트랜잭션으로 감싸고 항상 `ROLLBACK`합니다.
  5. **알려진 한계 (사용자에게 경고로 표시):** 동적 SQL(`sp_executesql`, `EXEC(@sql)`) 내부, `#temp` 테이블의 컬럼은 검증되지 않습니다.
* **회귀 차단 (Rollback):** 자가 교정을 최대 횟수까지 시도해도 실패하면 `FileGuard` 백업으로 원본을 복구합니다.

### FR-07. SQL 안전 가드 (`SqlSafetyGuard`)
* **문장 허용 목록:** 배치의 최상위는 **대상 객체에 대한 단일 `CREATE`/`ALTER PROCEDURE|FUNCTION|VIEW` 문**만 허용합니다. `GO` 구분자로 나뉜 다중 배치는 거부합니다.
* **금지 구문 (위치 무관):** `SET NOEXEC`, `SET PARSEONLY`, `SET FMTONLY`, `SHUTDOWN`, `RECONFIGURE`, `xp_cmdshell`, `OPENROWSET`/`OPENDATASOURCE`(`EXEC … OPENDATASOURCE(…)` 형태 포함), `ALTER DATABASE`/`ALTER LOGIN`/`CREATE LOGIN`, `GRANT`/`REVOKE`/`DENY`. `ALTER DATABASE SCOPED CONFIGURATION`·`ALTER DATABASE ENCRYPTION KEY`·`ALTER DATABASE AUDIT SPECIFICATION`도 `ALTER DATABASE` 항목으로 셉니다(보수적 해석, M2b).
* **원본의 금지 구문 (M2b, R2):** "위치 무관"이므로 수정 전 원본 대상 객체에 이미 금지 구문이 있으면 모델을 부르기 전에 종료 코드 5로 끝납니다. 본문에 `GRANT`·`xp_cmdshell` 등이 있는 프로시저는 pelly로 고칠 수 없고, 안내 문구가 그 사실을 밝힙니다. 또 원본 대상 배치에 구문 오류가 있거나 최상위 문장이 둘 이상이면 편집할 수 없어 종료 코드 3입니다.
* **접속 대상 제한:** 설정의 `sql.allowedHosts`/`sql.allowedDatabases` 화이트리스트에 있는 대상에만 접속합니다. 없으면 종료 코드 5로 끝냅니다. **목록이 비어 있으면 아무 데도 접속하지 않습니다**(처음 쓸 때 설정 한 줄이 필요, M2b R4). 연결 문자열에 서버 키나 데이터베이스 키가 둘 이상 있거나 `Failover Partner`가 있으면 허용 목록을 우회할 수 있어 거부합니다(종료 코드 5).
* **최소 권한 계정:** 검증 전용 계정에 **DB 수준 `VIEW DEFINITION`만** 부여합니다. `ALTER`·`SHOWPLAN`은 필요 없습니다. 이 권한이 없으면 객체 존재 검사와 스키마 요약이 동작하지 않습니다(S5).
* **Windows 통합 인증 (기본):** 폐쇄망에서는 `Integrated Security=true`를 기본으로 사용합니다. 연결 문자열에 비밀번호가 없으므로 설정 파일에 두어도 됩니다.
* **SQL 인증 (외부 개발용):** 비밀번호가 필요한 연결 문자열은 환경 변수 `PELLY_SQL_CONNECTION`으로만 받으며, 설정 파일·로그에 평문으로 남기지 않습니다. 설정 파일의 `sql.connectionString`에 `Password`/`Pwd`가 있으면 설정 오류(종료 코드 4)입니다. `dotnet user-secrets`는 전역 도구에서 의미가 없어 지원하지 않습니다(M2b R3). `PELLY_SQL_CONNECTION`이 있으면 `sql.connectionString`보다 우선합니다.

### FR-08. 환경 점검 (`pelly doctor`)
폐쇄망에서는 문제가 생겨도 외부에서 원격 지원을 받기 어려우므로, 사용자가 스스로 환경 문제를 찾을 수 있게 합니다.
* **Ollama 서버:** 연결 가능 여부·지연(`GET /api/version`), 대상 모델 존재(`/api/tags`), 로드 상태(`/api/ps`), 테스트 생성 1회로 TTFT 실측. 서버 환경 변수(Flash Attention, KV 캐시 타입 등)는 API로 확인할 수 없으므로 **서버 관리자 확인 체크리스트**(§7.1)를 함께 표시합니다.
* **빌드 도구:** .NET SDK 버전, `vswhere`·`MSBuild.exe` 경로와 버전, .NET Framework Developer Pack(대상 타겟팅 팩) 설치 여부
* **SQL:** 접속 가능 여부, 화이트리스트 일치, 검증 계정 권한. 인증 방식(Windows 인증 / SQL 로그인)과 접속한 로그인을 함께 보이며, `--sanitize`는 로그인을 지우고 `authMode`만 남깁니다 (M4 준비)
* **pelly 자체:** 버전, 내장 토크나이저 해시, 설정 파일 경로와 적용 값
* **구현 (M3a):** 위 항목 전부를 한 표로 보입니다(Ollama 서버·모델·렌더러·템플릿 일치·토크나이저 일치·응답 시간·pelly·설정·.NET SDK·MSBuild·타겟팅 팩·서버 설정 체크리스트·SQL 연결). `--project <csproj>`는 타겟팅 팩을 확인할 프로젝트를 지정합니다(Windows 전용, Windows 미확인). Fail이 있으면 종료 4입니다.
* **`--sanitize [--out <폴더>]`:** 화면 대신 `json`·`md` 파일로 저장합니다. 행 이름(알려진 목록)·상태와 행마다 정한 사실(버전·지연 ms·로드 여부·일치 여부 등 짧은 토큰)만 남기고 설명·조치 문장, 경로, 연결 문자열은 남기지 않습니다. 버전은 숫자로 시작하는 규칙을, 타겟팅 팩 프레임워크 버전은 `v4.8` 형태의 모니커 규칙을 통과해야 하며 아니면 `invalid`입니다.
* **`--offline-selftest` (숨은 옵션):** 네트워크·설정 없이 내장 토크나이저·Roslyn·ScriptDom·인코딩·리소스와 SqlClient 네이티브 계층(여섯 행, M3b T7)을 확인합니다. 단일 파일 배포본 점검용이며 CI `publish-check`에서 모두 Ok로 통과했습니다.

### FR-09. 실행 기록 요약 및 비식별 리포트 (`pelly report`)
폐쇄망의 실행 로그에는 소스 코드가 들어 있어 외부로 반출할 수 없습니다. 외부에서 개선 작업을 하려면 코드 없이 문제를 파악할 수 있는 자료가 필요합니다.
* `pelly report`는 실행 기록에서 KPI(§2.2)와 단계별 소요 시간, 실패 유형 분포를 집계합니다.
* `--sanitize`를 붙이면 **소스 코드, 프롬프트·응답 원문, 파일 경로, 식별자 이름, 연결 정보를 모두 제거**하고 다음만 남깁니다: 단계별 타이밍, 토큰 수, 재시도 횟수, 종료 코드, 에러 코드(`CSxxxx`, SQL 에러 번호, 테스트 실패를 뜻하는 `TEST`), 매칭 단계, 실패 유형(고정 목록), pelly·Ollama 버전과 프롬프트 버전. 모델 이름·RunId는 남기지 않습니다.
* **허용 목록 규칙 (M3a, 설계 §8.12):** 에러 코드는 `\A(CS|MSB|NU|Msg|SQL)[0-9]{1,6}\z` 또는 `TEST`, 버전은 숫자로 시작하는 40자 이내 토큰, 프롬프트 버전은 `\A(cs|sql)-v[0-9]{1,4}\z`이며 정규식은 모두 처음부터 끝까지 고정(ASCII 숫자)합니다. 통과하지 못한 값은 `invalid`/`Other`로 바꾸거나(에러 코드는 건수만 남기고) 버립니다.
* **구현 (M3a):** `pelly report [--since yyyy-MM-dd]`는 화면에 지표 표와 분포를 보이고, `--sanitize [--out <폴더>]`는 `pelly-report-<시각>.json`·`.md`를 씁니다. 실행 기록 중 `summary.json`만 읽습니다(`events.jsonl` 등은 읽지 않음). 실제 실행으로 소스 식별자·경로·모델 이름이 파일에 없음을 확인했습니다.
* 반출 가능 여부는 사내 보안 규정을 따릅니다(Q11). 리포트 형식은 사람이 읽고 검토할 수 있는 JSON/Markdown으로 합니다.

---

## 5. 비기능 요구사항 (Non-Functional Requirements)

| 항목 | 요구사항 |
| :--- | :--- |
| **오프라인 동작** | 실행 중 인터넷 접속을 일절 하지 않습니다(설정된 Ollama 서버·SQL 서버만 접속). 업데이트 확인, 텔레메트리, 외부 리소스 다운로드 없음. 토크나이저·기본 설정은 실행 파일에 내장 |
| **원격 Ollama 연동** | 엔드포인트는 설정 `llm.endpoint`(예: `http://ollama.internal:11434`). 사내 프록시 설정(`HTTP_PROXY`, `NO_PROXY`)을 따르되, 내부 주소는 프록시를 우회하도록 안내. 서버 앞단에 인증 프록시가 있으면 `llm.headers`로 헤더 지정(Q13). 연결 타임아웃 3초 |
| **공용 서버 대기** | Ollama 서버를 여러 사람이 함께 쓰면 요청이 대기할 수 있습니다. 첫 응답이 3초 넘게 없으면 "서버 응답 대기 중 (N초)" 상태를 표시하고, 대기 시간은 KPI의 TTFT와 분리해 `QueueSeconds`로 기록합니다(`max(0, 클라이언트 TTFT − 모델 로드 − 프롬프트 처리)`, TTFT에서는 뺌, M3a R3). 단독 사용 기준선에서는 약 0.02~0.03초입니다 |
| **설정** | 우선순위: CLI 옵션 > 프로젝트 `pelly.json` > 사용자 `%APPDATA%\pelly\config.json` (macOS/Linux: `~/.config/pelly/config.json`) > 내장 기본값. 주요 키: `llm.endpoint`, `llm.model`, `llm.thinkTag`, `llm.headers`, `budget.*`, `sampling.think/code`, `build.msbuildPath`, `test.command`, `test.projects`, `test.timeoutSeconds`, `sql.allowedHosts`, `sql.allowedDatabases`, `sql.compatLevel` |
| **실행 로그** | `.pelly/runs/<run-id>/`에 프롬프트, 모델 응답 원문, 패치, 빌드/SQL 로그, 단계별 타이밍을 JSONL로 저장(디버깅·KPI 수집용). 연결 문자열·비밀번호는 마스킹. `.pelly/`는 형상 관리 제외 대상으로 안내. 보관 기간 기본 30일 후 자동 삭제 |
| **장애 처리** | 시작 시 Ollama 연결 확인(타임아웃 3초)과 대상 모델 존재 확인. 모델이 로드되지 않았으면 "첫 요청은 모델 로딩으로 느릴 수 있음"을 표시. 스트림 무응답 10초 시 1회 재연결 후 실패하면 종료 코드 4. 빌드 타임아웃 기본 120초(레거시 대형 프로젝트는 설정으로 조정) |
| **중단 처리** | Ctrl+C(`Console.CancelKeyPress`) 시 진행 중인 요청·빌드를 취소하고 롤백. 빌드 프로세스는 자식 프로세스 트리까지 종료(`Process.Kill(entireProcessTree: true)`) |
| **지원 OS** | **운영: Windows 11 x64 (폐쇄망).** 개발: macOS (Apple Silicon), Windows 11 ARM64 VM (§8.5) |
| **Windows 콘솔** | 시작 시 `Console.OutputEncoding`/`InputEncoding`을 UTF-8로 설정해 한글 깨짐 방지. Windows Terminal·PowerShell 기준으로 렌더링을 확인하고, 구형 콘솔(conhost)에서는 `Spectre.Console`의 색상·유니코드 감지에 맡김 |
| **경로 처리** | 경로는 항상 `Path` API로 조합(구분자 하드코딩 금지). 대소문자를 구분하지 않는 비교(Windows) 고려. 260자 초과 경로 대비 |
| **외부 프로세스** | `dotnet`, `MSBuild.exe`, `git` 실행 시 `ProcessStartInfo.ArgumentList`를 사용해 공백·한글 경로 인용 문제 방지. 출력 인코딩은 UTF-8로 지정(레거시 MSBuild 출력이 CP949로 나오는 경우 감지해 변환) |
| **성능 가시성** | `--verbose` 시 실행 후 단계별 소요 시간·토큰·시도 요약과 실행 폴더 경로 출력 (M3a) |

---

## 6. CLI 기술 스택 및 폴더 구조

### 6.1 추천 NuGet 패키지
* **CLI 프레임워크 & UI:** `Spectre.Console` / `Spectre.Console.Cli`
* **GUI 승인 창 (V2-0):** `Avalonia` 12.1.4 (`Avalonia.Desktop`, `Avalonia.Themes.Fluent`; MIT). 그래픽 네이티브 `Avalonia.Angle.Windows.Natives`(ANGLE)는 BSD-3-Clause이며 nuspec이 라이선스를 파일로만 적어 게이트 예외로 승인했습니다(§11.1). `ReactiveUI`·MVVM 툴킷은 쓰지 않습니다. 테스트는 `Avalonia.Headless`를 쓰되 `Avalonia.Headless.XUnit` 12.1.4가 xunit.v3 4.0.2와 맞지 않아 `HeadlessUnitTestSession`으로 직접 띄웁니다.
* **C# AST 코드 슬라이서:** `Microsoft.CodeAnalysis.CSharp` (Roslyn)
* **SQL 파싱 및 안전 가드:** `Microsoft.SqlServer.TransactSql.ScriptDom`
* **DB 스키마/구문 검증:** `Microsoft.Data.SqlClient` + `Dapper`
* **토큰 계산:** `Microsoft.ML.Tokenizers` 2.0.1 (Qwen `vocab.json`·`merges.txt` 내장 리소스, S4에서 서버와 오차 0%)
* **설정:** `Microsoft.Extensions.Configuration` (+ `UserSecrets`, 외부 개발용)
* **LLM 스트리밍 연동:** `System.Net.Http` (Ollama NDJSON 스트림 직접 제어)
* **테스트:** `xUnit`, `Verify` (스냅샷 테스트)
* **배포 점검:** CycloneDX SBOM 생성 도구, 서드파티 라이선스 목록 생성 (§11)

모든 패키지는 폐쇄망 반입 심사를 고려해 **라이선스가 명확하고(MIT/Apache 2.0 등) 실행 중 외부 통신이 없는 것**만 사용합니다.

### 6.2 솔루션 폴더 구조
```text
PellyCoder.sln
├── src/PellyCoder.Cli/
│   ├── Program.cs                      # CLI 엔트리포인트 및 DI 컨테이너 설정
│   ├── Commands/
│   │   ├── CsCommand.cs                # pelly cs
│   │   ├── SqlCommand.cs               # pelly sql
│   │   ├── DoctorCommand.cs            # pelly doctor (FR-08)
│   │   └── ReportCommand.cs            # pelly report (FR-09)
│   ├── Config/
│   │   └── PellyOptions.cs             # 설정 바인딩 및 검증
│   ├── Core/
│   │   ├── FsmOrchestrator.cs          # PREFLIGHT → LOCATE → SLICE → EDIT → APPLY → VERIFY → REVIEW
│   │   ├── RunContext.cs               # 실행 단위 상태 (run-id, 재시도 횟수, 백업 정보)
│   │   └── ContextBudget.cs            # §3.3 예산 계산 및 강제
│   ├── Llm/
│   │   ├── ILlmBackend.cs              # 백엔드 추상화
│   │   ├── OllamaBackend.cs            # /api/generate (raw), keep_alive, 연결·모델 확인
│   │   ├── QwenStreamClient.cs         # 2단계 생성 및 Thinking 예산 제어
│   │   ├── QwenChatTemplate.cs         # raw 모드용 chat template 직접 조립
│   │   ├── TokenCounter.cs             # 로컬 토크나이저 + 서버 카운트 보정
│   │   ├── Prompts.cs                  # 시스템 프롬프트·few-shot 템플릿 (버전 관리)
│   │   └── Resources/tokenizer.json    # 내장 Qwen 토크나이저 (오프라인용)
│   ├── Locators/
│   │   └── SymbolLocator.cs            # 대상 심볼 탐색 (FR-01)
│   ├── Slicers/
│   │   ├── RoslynSlicer.cs             # C# 메서드 및 연관 정의 추출
│   │   └── SqlSchemaSlicer.cs          # SP 참조 테이블 DDL 요약 추출
│   ├── Patchers/
│   │   ├── SearchReplaceParser.cs      # SEARCH/REPLACE 블록 파싱
│   │   ├── SearchReplacePatcher.cs     # 단계별 매칭·유일성 검사·들여쓰기 보정
│   │   └── FileGuard.cs                # 인코딩 보존, 백업, 원자적 쓰기, 동시 수정 감지
│   ├── Verifiers/
│   │   ├── BaselineSnapshot.cs         # 패치 전 에러 집합 기록·비교
│   │   ├── BuildToolResolver.cs        # 프로젝트 형식 판별, dotnet / MSBuild.exe(vswhere) 선택
│   │   ├── BuildVerifier.cs            # 구문 사전 검사 + 프로젝트 단위 빌드
│   │   ├── BuildErrorParser.cs         # CS 에러 정규식 파서 + ±3줄 컨텍스트
│   │   ├── SqlSafetyGuard.cs           # FR-07 허용/금지 구문 검사
│   │   └── SqlSyntaxVerifier.cs        # ScriptDom 파싱 + 객체 존재 + NOEXEC 컴파일
│   └── Logging/
│       ├── RunLogger.cs                # .pelly/runs/ JSONL 기록
│       └── ReportSanitizer.cs          # 비식별 리포트 생성 (FR-09)
├── tests/PellyCoder.Tests/             # 단위·스냅샷 테스트
├── eval/
│   ├── PellyCoder.Eval/                # 벤치마크 실행기 및 KPI 집계 (폐쇄망에서도 동작)
│   ├── benchmark/                      # 작업 정의 (요청, 대상, 판정 기준)
│   └── corpus/                         # 외부용 샘플 코퍼스 (SDK·레거시 솔루션, 샘플 DB 스키마)
└── deploy/
    ├── publish.ps1 / publish.sh        # self-contained 패키지 빌드
    ├── package-manifest.md             # 반입 패키지 구성·체크섬 기록 양식
    └── server-checklist.md             # Ollama 서버 관리자 확인 체크리스트
```

---

## 7. LLM 런타임 튜닝 파라미터

### 7.1 Ollama 서버 (폐쇄망, 서버 관리자 관리)
v1은 Ollama만 지원합니다(`/api/generate`, `raw: true`). 서버 설정은 pelly가 바꿀 수 없으므로 아래 값을 **서버 관리자에게 요청·확인**합니다(`deploy/server-checklist.md`).

| 설정 | 권장 값 | 이유 | 확인 방법 |
| :--- | :--- | :--- | :--- |
| Ollama 버전 | 외부 개발 환경과 동일 버전 | raw 모드·캐시 동작이 버전마다 다를 수 있음 | `/api/version` (doctor 자동) |
| 모델 | `Qwen3.6-35B-A3B` GGUF `Q4_K_M`, 외부와 같은 파일 | 결과 재현성 | 관리자에게 GGUF SHA-256 확인 (Q10, M4에서 버전 기록에 기록) |
| `OLLAMA_FLASH_ATTENTION` | `1` | KV 캐시 양자화를 쓰려면 필요 | 관리자 확인 |
| `OLLAMA_KV_CACHE_TYPE` | `q8_0` (4비트 금지) | 변수명 철자·괄호 짝 기억력 유지하면서 VRAM 절약 | 관리자 확인 |
| `OLLAMA_NUM_PARALLEL` | 동시 사용자 수에 맞춰 협의 | 슬롯 수만큼 KV 메모리(8K × 슬롯)가 늘어남. 1이면 요청이 대기 | 관리자 확인 (Q9) |
| `OLLAMA_HOST` | 폐쇄망 내부 주소로 바인딩 | 개발 PC에서 원격 접속 가능하도록 | doctor 연결 확인 |
| `OLLAMA_KEEP_ALIVE` | 업무 시간 동안 언로드되지 않는 값 | 재로딩 시 TTFT 목표가 깨짐. pelly도 요청마다 `keep_alive: "30m"` 지정 | `/api/ps` (doctor 자동) |

pelly가 요청마다 지정하는 값: `options.num_ctx = 8192`, `keep_alive = "30m"`, 단계별 샘플링 값(§7.2).

### 7.2 파라미터
| 설정 항목 | 필수 적용 값 | 기술적 근거 (Why) |
| :--- | :--- | :--- |
| **Model Weights** | GGUF `Q4_K_M` | 메모리 대역폭 최적화로 고속 토큰 생성 |
| **KV Cache Quantization** | **`FP16` 또는 `Q8_0` (4비트 금지)** | KV 캐시를 4비트로 낮추면 변수명 철자와 괄호 짝 기억력이 손상됨 |
| **Context Window (`num_ctx`)** | **`8192` (8K 고정)** | 메모리 사용량 예측 가능성 확보, §3.3 예산의 기준 |
| **THINK 단계 샘플링** | 모델 카드 권장값 (Qwen3 계열 기준 예: `Temp 0.6` / `Top-P 0.95` / `Top-K 20`) | Qwen3 계열은 thinking 모드에서 greedy에 가까운 디코딩 시 반복 루프 위험을 경고함. Qwen3.6 값은 M0에서 확인 (Q2) |
| **CODE 단계 샘플링** | `Temp 0.1` / `Top-P 0.9` | 창의성을 배제하고 가장 확률 높은 구문 선택 유도 |
| **Repeat Penalty (CODE)** | `1.05` | 과도한 패널티로 필수 예약어(`public`, `SELECT`, `await` 등)를 피하는 현상 방지 |

샘플링 값은 설정 파일로 바꿀 수 있어야 합니다. 폐쇄망 안에서 코드 재반입 없이 튜닝하기 위함입니다.

**M3b 튜닝 결과 (M3b T6):** 이 표의 기본값은 **바뀌지 않았습니다.** `budget.think` 768, code `Temp 0.2`/`Top-P 0.95`, DTO 예시 보강을 50건 × 3회로 쟀지만 §8.2의 채택 규칙을 만족한 것이 없었습니다(`docs/m3b-tuning.md`).

---

## 8. 평가 및 테스트 전략

### 8.1 벤치마크 (`eval/benchmark`)
* **외부 샘플 코퍼스 (`eval/corpus`):** 실제 코드는 반출할 수 없으므로, 실제 코드의 특징을 모사한 샘플 솔루션을 만듭니다.
  * 레거시 .NET Framework 프로젝트(SDK 스타일 아님, `packages.config`, C# 7.3)와 SDK 스타일 프로젝트를 모두 포함
  * CP949 인코딩·CRLF 파일, Dapper 사용 코드, 실제와 비슷한 명명 규칙
  * 샘플 DB 스키마와 SP (SQL Server 컨테이너에 배포)
* **구성:** **M2b에서 30건**(C# 18건 = Legacy 10 + Modern 8, SQL 12건 — 메서드 로직 수정, null 처리 추가, Dapper 쿼리 변경, SP 조건·컬럼 추가, JOIN 변경 등), **M3b에서 50건**으로 확장했습니다(C# 30건 = Legacy 18 + Modern 12, SQL 20건. Legacy 18건은 MVC 8 · ADO.NET 5 · Dapper 5. M3b T4). 시그니처를 바꾸는 요청(async 전환 등)은 멤버 모양 검사가 막는 의도된 동작이라 넣지 않습니다. 모든 작업은 빌드·컴파일 외에 내용 판정을 하나 이상 가집니다.
* **작업 정의:** 요청문, 대상 파일·심볼, 판정 기준(빌드/검증 통과 + 지정 테스트 통과 또는 기대 diff와의 의미 비교)
  * **동작 판정은 Modern C# 12건에만 둡니다** (M3b R1): 코퍼스 밖 판정 프로젝트 둘(`Shop.Api.JudgeTests`, 가짜 Dapper·`SqlConnection`을 쓰는 `Shop.Data.JudgeTests`)이 `judge.tests`(수정 뒤 통과)와 `judge.failingTests`(수정 뒤 실패, 수정 전 동작을 고정한 테스트용)를 판정합니다. Legacy는 Windows가 아니면 빌드·테스트가 안 되고 SQL은 모델 SQL을 실행하면 DB 무변경 원칙과 부딪혀서 문자열 판정을 유지하며, 전체 판정은 M4입니다. 판정 도구 오류는 생략(`JudgeTooling`)으로 따로 세어 불변 조건 위반으로 세지 않습니다.
* **수집 지표:** §2.2의 모든 KPI를 실행 로그(`.pelly/runs`)에서 자동 집계. 검증 불변 조건은 실행기가 결과를 pelly와 별개로 다시 검증해 판정하며, 독립 검증이 생략된 실행(Windows가 아닌 환경의 Legacy 등)은 분모에서 빼고 따로 표시합니다. 실행 방법과 첫 측정은 `eval/benchmark/README.md`, `docs/m2b-benchmark.md`.
* **M3b 50건 결과 (기준선 = 최종 KPI, 2026-10-18, `docs/m3b-baseline.md`):** 3회 반복(`--repeat 3`) 평균 성공 **47.67건**(회차별 48·47·48, 성공률 94.0~96.0%, 목표 ≥ 85% 충족). 범주별 cs-legacy 18.00 · cs-modern 9.67 · sql 20.00. 검증 불변 조건 위반은 3회 모두 **0건**(독립 검증 32건, 생략 18건 = Legacy). 작업 시간 중위값 1.90초, TTFT 중위 0.12~0.25초, 패치 포맷 파싱 100%, 추론 토큰 최대 512. 실패는 cs-modern 3건(003 2/3, 007 0/3, 008 0/3)의 경계값 실수뿐입니다. 이 수치는 개발 Mac(Ollama, nvfp4 모델) 참고치이며 KPI 최종 판정은 M4 폐쇄망입니다(§2.1).
* **스위트와 사전 검사 (M4 준비):** 실행기는 `suite.json`(`roots`: 이름·경로·`exclude`, `supportFiles`, `resultsDir`)이 있는 폴더를 스위트로 읽습니다. 내장 스위트는 `eval/benchmark/suite.json`이고 현장 세트는 같은 모양의 폴더를 만들어 `--suite <폴더>`로 줍니다. 작업의 `file`은 `<루트 이름>/<루트 안 경로>`이며 Legacy 여부는 루트 이름이 아니라 가장 가까운 csproj가 비 SDK 형식인지로 정합니다. `--keep-work <all|failed|none>`은 작업 복사본 보존 범위입니다. `validate`는 실행 전에 정의(id·판정·대상 파일·프로젝트 형식)를 LLM·SQL 없이 점검하고, `validate --deep`은 판정 시험을 원본에서 실제로 돌립니다(`tests`는 실패, `failingTests`는 통과해야 함). 내장 스위트 회귀는 48/50, 위반 0건입니다(`docs/m4-suite-regression.md`).
* **실행 조건과 반출 (M4 준비):** 반복 요약은 `formatVersion` 2이며 실행 조건에 Ollama 버전과 모델 digest를 담습니다(1도 읽습니다). `compare`는 digest나 Ollama 버전이 다른 요약을 판정하지 않습니다. `record`가 버전 기록(`version-record.json`)을 쓰고(`--gguf-sha256`으로 `modelGgufSha256` 기록) `export`가 결과를 비식별해 반출용 폴더로 씁니다.
* **폐쇄망 벤치마크:** M4에서 같은 형식으로 실제 코드 기반 세트를 폐쇄망 안에서 작성합니다. 실행기는 현장 키트 zip의 `pelly-eval.exe` 단일 파일로 반입하며 폐쇄망에서도 그대로 동작합니다(pelly와 같은 설정 규칙으로 Ollama·SQL 서버에만 접속, `deploy/no-network.md`).

### 8.2 회귀 평가
* `Prompts.cs`, 샘플링 값, 슬라이서 로직을 바꿀 때마다 외부 벤치마크 전체를 실행합니다.
* 최종 작업 성공률이 기준선보다 떨어지면 병합하지 않습니다.
* **회귀 차단 절차 (M3b R4, T6):** 프롬프트·샘플링·슬라이서를 바꾼 뒤에는 벤치마크 50건 전체를 같은 조건으로 3회 반복(`--repeat 3`)하고, 기준선 요약과 후보 요약을 `compare`로 판정합니다. 후보는 ① 평균 성공이 기준선보다 1.0건 이상 많고 ② 어떤 범주(cs-legacy·cs-modern·sql)도 평균이 0.5건 넘게 줄지 않고 ③ 3회 모두 검증 불변 조건 위반이 0건이고 ④ 작업 시간 중위값이 기준선의 1.5배 이하일 때만 채택합니다. 하나라도 어기면 채택하지 않고, 만족하는 후보가 없으면 기본값을 유지하고 그것도 결과로 기록합니다.
* **M3b 적용 결과:** 후보 3개(think 768 · code 샘플링 0.2/0.95 · DTO 예시 보강)가 각각 +0.00 · −0.67 · −0.33으로 모두 거절돼 기본값이 유지됐습니다(`docs/m3b-tuning.md`). 후보는 계획 단계에 정한 목록에서만 골랐습니다(측정 뒤 추가 금지).

### 8.3 단위·스냅샷 테스트 (`tests/`)
* **Patcher:** 들여쓰기 오차, CRLF/LF 혼합, BOM, EUC-KR/CP949, 다중 매칭 거부, 마커 충돌
* **Slicer:** 대표 파일에 대한 슬라이스 결과 스냅샷, 예산 초과 시 제거 순서, 언어 버전 감지
* **SqlSafetyGuard:** 금지 구문·다중 배치·`SET NOEXEC OFF` 삽입 시도 거부 케이스
* **BuildToolResolver:** SDK/레거시 프로젝트 판별, `vswhere` 결과 파싱, 경로 직접 지정
* **BuildErrorParser / BaselineSnapshot:** `dotnet build`·`MSBuild.exe` 실제 출력 샘플 기반 파싱·신규 에러 판별
* **QwenStreamClient:** 모의 Ollama 스트림 서버(NDJSON)로 512 토큰 중단·프리필 재요청 동작 검증
* **ReportSanitizer:** 비식별 리포트에 코드·경로·식별자가 남지 않는지 검증 (반출 사고 방지용 필수 테스트)
* **오프라인 동작:** 네트워크를 Ollama·SQL 주소 외에는 차단한 상태에서 전체 E2E가 동작하는지 확인

### 8.4 CI 매트릭스 (외부)
* GitHub Actions에서 **`windows-latest`와 `macos-latest`** 양쪽으로 단위·스냅샷 테스트를 실행합니다. LLM이 필요 없는 테스트만 돌립니다.
* `windows-latest` 러너에는 Visual Studio가 설치돼 있으므로 **레거시 프로젝트 빌드 검증 테스트**(샘플 코퍼스)를 여기서 실행합니다.
* Windows 전용 케이스(CRLF, CP949, 파일 잠금 재시도, 경로 대소문자, 콘솔 인코딩, MSBuild.exe)는 Windows 러너에서만 실행되도록 표시합니다.

### 8.5 외부 개발 환경 구성
폐쇄망 구성(개발 PC와 원격 Ollama 서버 분리)을 노트북 한 대에서 최대한 비슷하게 재현합니다.

```text
[macOS 노트북 (Apple Silicon)]
 ├─ Ollama (Metal GPU) ─ 폐쇄망과 같은 버전·같은 GGUF, OLLAMA_HOST=0.0.0.0
 │      ▲  HTTP (원격 서버 역할)
 │      │
 ├─ Windows 11 ARM64 VM (Parallels / UTM)
 │      ├─ .NET 10 SDK, Visual Studio Build Tools (MSBuild.exe), .NET Framework Developer Pack
 │      ├─ pelly 실행 → 호스트 Ollama 접속
 │      └─ 샘플 코퍼스 빌드 검증 (레거시 포함)
 │
 └─ SQL Server (Docker 컨테이너) ─ 샘플 DB, SQL 인증
```
* **일상 개발:** macOS에서 코드 작성과 단위 테스트를 합니다. 레거시 빌드 검증이 필요한 작업은 VM에서 실행합니다.
* **원격 서버 재현:** VM의 pelly가 호스트의 Ollama에 네트워크로 접속하므로, 폐쇄망의 "개발 PC ↔ 원격 서버" 구조와 같은 경로를 테스트할 수 있습니다.
* **한계:**
  * 성능 수치는 폐쇄망 GPU 서버와 다르므로 참고만 합니다.
  * VM은 ARM64 Windows이므로 운영 환경(x64)과 다릅니다. x64 패키지 동작은 CI의 `windows-latest`(x64)와 M4 폐쇄망 검증으로 확인합니다.
  * Windows 통합 인증은 외부에서 테스트하지 않고 M4에서 확인합니다.

---

## 9. 구현 로드맵 (Implementation Phases)

* **Milestone 0 (1주 차) — 환경 구성 및 기준선:**
  * 외부 개발 환경 구성(§8.5): Ollama, Windows ARM VM, Build Tools, SQL Server 컨테이너
  * 폐쇄망 정보 수집: Ollama 버전·모델 파일 해시·서버 설정·사양·동시 사용자 수, 개발 PC 도구 버전, 반입 절차 (Q9~Q13)
  * Ollama raw 모드 Prefill·접두사 캐시 재사용·think 태그 동작 확인 (Q1~Q3)
  * SQL 검증 계정의 최소 권한 확인 (Q4)
  * 샘플 코퍼스·외부 벤치마크 작성, 평가 하네스 골격 구축
* **Milestone 1 (2주 차) — C# 최소 E2E:**
  * `Spectre.Console.Cli` 스캐폴딩, 설정·로그, `pelly doctor` 기본 항목
  * Windows/macOS CI 매트릭스 구성 (§8.4)
  * `QwenStreamClient`(2단계 생성, 512 토큰 예산, 내장 토크나이저), `SearchReplacePatcher` + `FileGuard`
  * 기본 `RoslynSlicer`(대상 메서드 + 시그니처) → **C# 경로 E2E 최초 동작 (SDK 스타일)**
* **Milestone 2 (3주 차) — 검증 루프 및 SQL:**
  * `BuildToolResolver`(레거시 MSBuild.exe 포함), `BaselineSnapshot`, `BuildVerifier`, 자가 교정 루프·롤백·REVIEW 단계
  * `SymbolLocator`
  * `SqlSafetyGuard`, `SqlSyntaxVerifier`, 기본 `SqlSchemaSlicer` → **레거시 C#·SQL 경로 E2E 동작**
* **Milestone 3a (완료, 2026-10-18) — 제품 기능:** DTO 슬라이스, `--test`, `pelly report`·`--sanitize`, `pelly doctor` 완성(`--project`·`--sanitize`), `--verbose`, `--pause-on-exit`, 대기 시간 분리. 벤치마크 30건 회귀 30/30·불변 조건 위반 0건 (`docs/m3a-benchmark.md`, `docs/m3a-e2e.md`).
* **Milestone 3b (완료, 2026-10-19) — 튜닝 및 반입 패키지 (M3 중 남은 부분):**
  * 외부 벤치마크 50건과 3회 반복 기준선, 튜닝 후보 3개 측정(모두 거절, 기본값 유지), 동작 판정 연동 (`docs/m3b-baseline.md`, `docs/m3b-tuning.md`)
  * `win-x64` 단일 파일 publish(S6)·CI(실행 6회 = 잡 키 5개, unit은 macOS·Windows 매트릭스), self-contained 패키지·SBOM·라이선스 목록·체크섬·버전 기록 생성(첫 반입본 `pelly-0.9.0-win-x64.zip`, 버전 0.9.0. 현재 반입본은 0.9.1). **반입 신청은 사람이 진행합니다**(§11)
  * S7(Rider External Tools): 확인표는 만들었으나 실기 확인은 하지 않기로 함(2026-10-19 사람 결정). Rider 설정은 「실기 미확인」으로 남고, 현장 개발 PC의 Visual Studio는 M4 현장 확인표로 확인
* **Milestone 4 (5주 차 이후, 반입 승인 시점에 따라 조정) — 폐쇄망 이관 및 현장 검증:**
  * 설치, `pelly doctor`로 서버·빌드 도구·SQL·Windows 인증 점검
  * 실제 코드 기반 폐쇄망 벤치마크 작성 및 실행 → **KPI 최종 판정**
  * 설정 파일로 가능한 튜닝은 폐쇄망 안에서 진행. 코드 수정이 필요하면 비식별 리포트를 반출해 외부에서 수정 → 재반입
  * **현장 키트 절차 요약 (`deploy/field-guide.md`. 저장소의 `deploy/` 파일이 원본이고 현장 키트 zip 안에서는 `docs/field-guide.md`입니다. `field-benchmark.md`·`field-results.md`도 같습니다):** 반입 zip 둘의 SHA-256 확인 → 설치와 `pelly doctor` → 모델 GGUF SHA-256 계산과 `pelly-eval record --gguf-sha256`로 버전 기록 → 현장 스위트 작성과 `pelly-eval validate`(필요하면 `--deep`) → 기준선 `--repeat 3` 실행 → 후보 설정으로 같은 조건 재측정과 `compare` → `pelly-eval export`·`pelly report --sanitize`로 비식별 결과 반출 → `field-results.md` 기입. 명령은 Windows에서 실행해 보지 못했습니다(Windows 미확인).
  * **반입 전 사람 결정 (2026-10-19):** ① SNI 라이선스 예외 **승인** ② pelly `LICENSE` **Payletter 사내 전용** ③ S7 Rider **실기 미확인으로 종결** ④ 폐쇄망 정보 Q9~Q13 **질문지로 수집 중**(`docs/m4-field-questionnaire.md`) ⑤ 반입 신청은 사람이 진행

---

## 10. 미결 사항 (Open Questions)

| ID | 질문 | 확인 시점 | 담당 |
| :--- | :--- | :--- | :--- |
| Q1 | ~~think 태그·chat template~~ → **해소: ChatML + `<think>` (S1)**. 폐쇄망과 같은 모델로 재확인 | — | |
| Q2 | ~~권장 샘플링 값~~ → **해소: 모델 카드 "정밀 코딩" 값 0.6/0.95/20 (S3)** | — | |
| Q3 | ~~접두사 캐시 재사용~~ → **해소: 재사용됨, 2단계 TTFT 299ms (S2)** | — | |
| Q4 | ~~NOEXEC 최소 권한~~ → **해소: DB 수준 `VIEW DEFINITION` (S5)** | — | |
| Q5 | 외부 샘플 코퍼스가 모사할 실제 코드의 특징(프로젝트 구성, C# 버전, 인코딩, 데이터 접근 패턴) | M0 | |
| Q6 | 최종 작업 성공률 목표치 확정 (잠정 85%) | M4 | |
| Q7 | ~~기준 하드웨어 확정~~ → 폐쇄망 Ollama 서버로 확정 (사양은 Q9) | — | |
| Q8 | ~~레거시 프로젝트 지원 여부~~ → **해소: 레거시 .NET Framework 프로젝트 있음, v1에 포함 (FR-06)** | — | |
| Q9 | 폐쇄망 Ollama 서버의 GPU 사양·VRAM, 동시 사용자 수, `OLLAMA_NUM_PARALLEL` 등 현재 설정 | M0 → M4 반입 전 | 질문지 `docs/m4-field-questionnaire.md` |
| Q10 | ~~폐쇄망 서버의 Ollama 버전과 모델 태그·GGUF 파일 SHA-256~~ → **해소: Ollama 0.30.10, `qwen3.6:35b-a3b-q4_K_M`(ID `07d35212591f`, GGUF SHA-256 `f5ee307a…`), 공개 저장소에서 같은 파일 확보 (2026-10-28, `docs/m4-field-model-baseline.md`)** | — | |
| Q11 | 반입 절차: 바이너리만 vs 소스 포함, 보안 심사 제출 자료(SBOM, 라이선스, 백신 검사 결과 등), 소요 기간, 비식별 리포트 반출 허용 여부 | M0 → M4 반입 전 | 질문지 `docs/m4-field-questionnaire.md` |
| Q12 | 폐쇄망 개발 PC 환경: .NET SDK·Visual Studio/Build Tools 버전, .NET Framework 타겟 버전, 내부 NuGet 미러 유무, 형상 관리 도구(git/SVN/TFVC), PowerShell 실행 정책 | M0 → M4 반입 전 | 질문지 `docs/m4-field-questionnaire.md` |
| Q13 | 개발 PC → Ollama 서버 접근 방식: 주소·포트, 사내 프록시, 앞단 인증 여부 | M0 → M4 반입 전 | 질문지 `docs/m4-field-questionnaire.md` |

---

## 11. 폐쇄망 이관 (배포 패키지 및 절차)

### 11.1 반입 패키지 구성
| 항목 | 내용 |
| :--- | :--- |
| 실행 파일 | `dotnet publish -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true` 결과 `pelly.exe` (트리밍 사용 안 함, .NET 런타임 미포함 — 대상 PC에 .NET 10 런타임 필요). V2-0부터 그래픽 네이티브 DLL 셋(`av_libglesv2.dll`, `libHarfBuzzSharp.dll`, `libSkiaSharp.dll`)은 단일 파일에 묶지 않고 `pelly.exe` 옆에 둡니다. 단일 파일 모드에서 Roslyn·ScriptDom·토크나이저 리소스·SqlClient가 정상 동작함을 M3b에서 확인했다(S6 통과, M3b T7, CI `publish-check` 녹색) |
| 기본 설정 | `pelly.json` 템플릿 (Ollama 주소·모델명·SQL 화이트리스트 자리 표시) |
| 문서 | 설치 가이드, 사용 가이드, `server-checklist.md`(서버 관리자용) |
| 무결성 | 모든 파일의 SHA-256 체크섬 목록 |
| 보안 심사 자료 | SBOM(CycloneDX), 서드파티 라이선스 목록, "실행 중 외부 통신 없음" 명세(§5) |
| 버전 기록 | `version-record.json`(`pellyVersion`, `ollamaVersion`, `model`, `modelDigest`, `modelFormat`, `modelQuantization`, `modelGgufSha256`(현장에서 `record --gguf-sha256`으로 채움), `recordedAt`)과 벤치마크 결과 요약. 모델 GGUF SHA-256은 M4에서 현장 모델로 기록 |
| 현장 키트 | 둘째 zip `pelly-field-kit-<버전>-win-x64.zip`(아래 M4 준비 구성) |

**M3b 실제 구성 (M3b T9·T10, 첫 반입본 `0.9.0`, 현재 `0.9.1`, M4 현장 확인 뒤 `1.0.0`):** CI `package` 잡이 `pelly-0.9.1-win-x64.zip`을 만들고, 들어가는 파일은 `deploy/package-files.txt`의 허용 목록뿐입니다(`LICENSE` 포함). V2-0 기준 20줄이며, 고정 파일 17개(`SHA256SUMS.txt` 제외)에 `SHA256SUMS.txt`와 BuildHost 폴더 둘(줄 하나가 폴더 전체)입니다.

| 구분 | 파일 |
| :--- | :--- |
| 실행·설정 | `pelly.exe`(단일 파일, 트리밍·압축 없음), `av_libglesv2.dll`·`libHarfBuzzSharp.dll`·`libSkiaSharp.dll`(`pelly ui`용 네이티브, `pelly.exe` 옆, V2-0), `BuildHost-net472/`·`BuildHost-netcore/`(의미 분석 보조 프로세스, V2-1c), `LICENSE`, `pelly.json.template` |
| 문서 | `docs/install.md`, `docs/usage.md`, `docs/ide-external-tools.md`, `docs/server-checklist.md`, `docs/no-network.md`, `docs/field-checklist.md`, `docs/field-results.md` |
| 무결성·심사 | `SHA256SUMS.txt`, `sbom.cdx.json`(CycloneDX .NET 도구 6.3.1을 `.config/dotnet-tools.json`에 정확히 고정), `THIRD-PARTY-NOTICES.md`, `licenses/Microsoft.Data.SqlClient.SNI.runtime.LICENSE.txt`, `licenses/Avalonia.Angle.Windows.Natives.LICENSE.txt` |
| 버전·결과 | `version-record.json`, `benchmark-summary.md` |

* **라이선스 게이트:** MIT·Apache-2.0·BSD 계열이 아닌 패키지가 나오면 패키지 스크립트가 실패합니다. 예외는 `deploy/license-exceptions.json`에 패키지 id·버전·라이선스 파일 SHA-256·승인자를 적은 것만 허용합니다. **현재 예외는 둘**이며 모두 **사람이 승인했습니다.** ① `Microsoft.Data.SqlClient.SNI.runtime` 7.1.0(네이티브 SNI의 Microsoft 재배포 라이선스, 2026-10-19). ② `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419(`av_libglesv2.dll`, nuspec이 라이선스를 파일로만 적어 게이트에 걸리나 내용은 BSD-3-Clause, 2026-10-26 V2-0 반입 전 결정). 예외에 `"packages": [...]`를 적으면 그 패키지(zip) 만들 때만 적용되고 다른 zip에서는 일치도 미사용 보고도 하지 않습니다. ②는 `["pelly"]`라 현장 키트에는 적용되지 않습니다. `approvedBy`에 「잠정」이 든 예외로는 `1.0.0` 이상 패키지가 만들어지지 않으며, CI가 잠정으로 바꾼 사본으로 이 거부를 시험합니다.
* **버전 기록 (R8 대체):** Ollama 버전과 모델 태그, 모델 digest·형식·양자화(`modelDigest`·`modelFormat`·`modelQuantization`)를 담습니다. 개발 PC 모델은 GGUF가 아닌 safetensors `nvfp4`라 GGUF SHA-256을 읽을 수 없고, 벤치마크하지 않은 20GB 모델을 따로 받아 해시하는 것은 의미가 없어서, **현장 모델의 GGUF SHA-256은 M4에서 기록합니다.** 지금은 패키지가 현장 모델 파일의 동일성을 증명하지 못합니다.
**M4 준비 구성 (현장 키트, 버전 `0.9.1`):** 같은 `package` 잡이 둘째 zip `pelly-field-kit-0.9.1-win-x64.zip`을 만듭니다(`build-package.ps1 -Package field-kit`). 허용 목록은 `deploy/field-kit-files.txt`이며 `pelly-eval.exe`(단일 파일), 현장 절차 문서(`docs/field-guide.md`, `field-benchmark.md`, `field-results.md`), 스위트·작업·판정 프로젝트 템플릿(`templates/`), `sbom.cdx.json`, `THIRD-PARTY-NOTICES.md`, 예외 승인 라이선스 전문, `SHA256SUMS.txt`가 들어갑니다. `pelly.exe`, 그 옆 GUI DLL 셋과 설치 문서는 첫 zip에만 있습니다. `pelly-eval --offline-selftest`(숨은 옵션)는 `pelly doctor --offline-selftest`와 같은 여섯 행으로 단일 파일의 실행 경로를 확인하며, CI `publish-check`와 `package` 테스트가 실행합니다. `pelly-eval.exe`는 개발자 도구가 아니라 현장에서 기준선을 재는 반입 대상이며, 그래서 `NetworkPolicyTests`가 `eval/PellyCoder.Eval/`도 훑습니다.

* **GUI 크기 (V2-0):** 네이티브 DLL 셋은 `av_libglesv2.dll` 5,394,096바이트, `libHarfBuzzSharp.dll` 1,816,088바이트, `libSkiaSharp.dll` 11,628,896바이트(합 18,839,080바이트)입니다. 관리 어셈블리가 들어가 `pelly.exe`는 127,059,367 → 136,359,551바이트(+9.3 MB)가 됐습니다. 이 수치는 macOS에서 한 교차 publish 측정이며 Windows 값은 CI `publish-check`가 출력합니다(Windows 미확인). 네이티브를 단일 파일에 묶지 않은 이유는 `pelly cs`만 쓰는 사람도 첫 실행에 수십 MB를 임시 폴더에 풀지 않게 하고 AppLocker·WDAC 노출을 줄이기 위해서입니다. DLL이 없으면 `pelly ui`만 종료 코드 4이고 다른 명령은 영향이 없습니다.
* **JS 해석기 (V2-7):** `pelly js`가 쓰는 `Acornima` 1.9.0 버전(순수 .NET ECMAScript 해석기, `Directory.Packages.props`에 고정)이 단일 파일에 들어갑니다. 라이선스는 BSD-3-Clause이고 nuspec이 `license type="expression"`으로 적어 라이선스 게이트의 허용 목록(BSD 계열)에 그대로 맞으므로 **예외가 필요 없습니다**(`deploy/license-exceptions.json`은 바뀌지 않음). 게이트 통과는 CI `package` 잡이 확인합니다(`deploy/build-package.ps1`). NuGet 패키지의 고지는 SBOM에서 만들어지므로 저장소의 `THIRD-PARTY-NOTICES.md`(NuGet 밖 구성 요소만)도 바뀌지 않습니다. `pelly.exe`는 137,036,927 → 137,615,007바이트(+578,080바이트, 약 0.55 MB)입니다. Task 1 이전 커밋 `e2ab5e0` 빌드와 W1 구현 끝 커밋 `e01b233` 빌드를 같은 명령 `dotnet publish src/PellyCoder.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`로 비교한 값이며, macOS 교차 publish 측정, Windows 값은 CI입니다. 반입 zip의 파일 목록(`deploy/package-files.txt`)은 바뀌지 않습니다.
* **GUI 빌드 시점 수집:** Avalonia 빌드 도구의 익명 사용 정보 수집은 환경 변수 `AVALONIA_TELEMETRY_OPTOUT=1`로 끕니다(CI 전체, 개발자는 직접 설정). 실행 중에는 외부 통신이 없습니다(`deploy/no-network.md`).
* **`LICENSE`:** pelly 자체의 라이선스는 **Payletter 사내 전용**(All rights reserved, 2026-10-19 사람 결정)입니다. 저장소 맨 위 `LICENSE`가 두 zip의 맨 위에 그대로 들어갑니다.
* 개발자 PC의 설정 파일·경로·연결 문자열은 zip에 들어가면 안 되며 패키지 테스트가 검사합니다.

### 11.2 설치
* zip을 원하는 폴더에 풀고 `PATH`에 추가합니다. 관리자 권한은 필요 없습니다. .NET 10 런타임이 있어야 합니다(C# 빌드 검증에 필요한 .NET 10 SDK에 함께 들어 있음).
* `pelly doctor`로 Ollama 서버, 빌드 도구, SQL 접속을 확인합니다.
* 대상 PC에 .NET 10 SDK가 있으면 대안으로 로컬 폴더 피드에서 `dotnet tool install -g PellyCoder.Cli --add-source <폴더>`로 설치할 수도 있습니다.

### 11.3 업데이트 주기
* 반입 절차에 시간이 걸리므로, 자주 바뀌는 값(샘플링, 예산, 프롬프트 일부)은 **설정 파일로 빼서** 재반입 없이 바꿀 수 있게 합니다.
* 코드 수정이 필요한 개선은 모아서 정기 반입합니다. 반입본마다 버전 기록(§11.1)을 남겨 폐쇄망 결과와 외부 결과를 대조할 수 있게 합니다.

---

## 12. v2 로드맵

### 12.1 원칙
v2는 Superpowers, OpenSpec 같은 개발 하네스의 아이디어를 가져오되, **방식은 Pelly에 맞게 바꿉니다.**
* **프로세스는 프롬프트가 아니라 코드로 강제합니다.** 긴 스킬·지침 문서를 모델에 넣지 않습니다. 단계 진행, 검증, 상태 기록은 모두 FSM과 C# 코드가 맡습니다.
* **v1 FSM을 그대로 재사용합니다.** v2 기능은 v1의 단일 작업 실행(PREFLIGHT → … → REVIEW)을 여러 번 호출하거나, VERIFY 단계에 검사를 추가하는 방식으로만 확장합니다.
* **토큰 예산(§3.3)은 바꾸지 않습니다.** 새로 넣는 정보도 기존 예산 안에서 나눠 씁니다. 예외: `spec draft`의 계획 호출은 S6가 잰 대로 생각 2048 + 답 1024를 더하며(전체 `num_ctx` 8192 안), 편집용 요청문 상한(`budget.request`)은 적용하지 않습니다.
* **작업을 쪼개고 계획하는 일은 사람이 합니다.** 활성 3B 모델이 약한 부분이기 때문입니다. 초안은 모델이 쓸 수 있으나 채택은 사람이 합니다(S6).

### 12.2 진입 조건 및 순서
* **진입 조건:** M4 폐쇄망 검증에서 ① 최종 작업 성공률 목표(Q6) 달성, ② 검증 불변 조건 위반 0건.
* **순서:**

  | 단계 | 기능 | 예상 기간 | 의존성 |
  | :--- | :--- | :--- | :--- |
  | V2-0 | GUI 승인 창 (`pelly ui`, Avalonia): External Tools에서 띄우는 작은 창으로 요청 입력·진행 표시·diff 승인. **구현됨(2026-10-26)** | 1~2주 | v1 (진입 조건과 무관하게 v1 직후 착수 가능) |
  | V2-1 | 작업 명세 파일과 순차 실행 | 2주 | v1 |
  | V2-2 | 테스트 우선 검증 | 2주 | V2-1 (수용 기준 ↔ 테스트 연결) |
  | V2-3 | 프로젝트 규칙 파일 | 1주 | v1 (다른 단계와 독립적이라 앞당길 수 있음) |
  | V2-1d | 운영 정의 읽기·sql pull·SQL 영향 탐색 | 1~2주 | V2-1c |
  | V2-4 | 명세 초안 (`pelly spec draft`): 모델 초안 + 코드 검사 + 사람 승인. **구현됨(2026-10-27)** | 1~2주 | V2-1, V2-1c·V2-1d (영향 탐색) |
  | V2-5 | 현재 동작 고정 테스트 (`pelly test`): 모델이 특성 테스트를 쓰고 코드가 변이 검출까지 판정, 사람 승인. **구현됨(2026-10-29)** | 1~2주 | V2-2 (테스트 실행·비교 부품) |
  | V2-6 | Classic ASP 수정 (`pelly asp`): 모델이 `.asp`·`.inc`의 함수 하나나 줄 범위를 고치고 코드가 실행 없이 구조·안전·이름·컴파일·SP를 판정, 사람 승인. **구현됨(2026-11-04)** | 1~2주 | v1 (`sql`의 SP 부품). SP와 함께 바꾸기(A2)는 별도 계획 |
  | V2-7 | 클라이언트 JavaScript·CSS 수정 (`pelly js`·`pelly css`): 모델이 `.js`·`.css`의 함수·규칙 하나나 줄 범위를 고치고 코드가 브라우저 실행 없이 문법·문법 수준·대상 모양·안전을 판정(이름·API·속성은 경고), 사람 승인. **구현됨(2026-11-05, 1단계 W1)** | 1~2주 | V2-6 (인코딩·바이트 이어 붙이기 쓰기, `asp.siteRoot`·`asp.encoding`). 페이지 안 블록 W2 단계와 명세·영향 W3 단계는 별도 계획 |

* 각 단계가 끝날 때마다 폐쇄망으로 반입해 검증합니다(§11.3).

> **구현(V2-0): 2026-10-26.** `pelly ui cs`·`pelly ui sql` 승인 창이 구현됐습니다(FR-00 「승인 창」). 같은 FSM·롤백을 쓰고 `GuiUserInteraction`만 새로 만들었으며, 창은 요청 입력, 진행 줄, diff, 「적용」/「거부」, 다시 실행을 제공합니다. 네이티브 DLL 셋을 `pelly.exe` 옆에 두는 배치와 크기는 §11.1, 설계는 `docs/superpowers/specs/2026-10-26-v2-0-gui-design.md`입니다. CI와 헤드리스 테스트가 확인하지 못하는 네 가지(한글 IME 입력, Visual Studio External Tools 동작, 고해상도 표시, 폐쇄망 PC 적재)는 **Windows 미확인**이며 사람이 `deploy/field-results.md` 9절에 적습니다.

### 12.3 V2-1. 작업 명세 파일과 순차 실행 (`pelly apply`)
> **구현(V2-1d): 2026-10-26.** pelly가 운영 DB에 `VIEW DEFINITION`만으로 읽기 전용 접속합니다(`sql.production.*`, 환경 변수 `PELLY_SQL_PROD_CONNECTION`). `pelly sql pull`은 운영(기본)·개발의 프로시저·함수·뷰를 SSMS 모양의 ALTER 스크립트로 받아 `db/pulled/`(`.gitignore`로 제외)에 두고, `pelly sql check`는 운영에 실행하기 직전 운영과 대조합니다. 받은 뒤 운영이 바뀌었으면 `pelly sql`은 종료 코드 4(`SqlProdChanged`)로 멈춥니다. SQL 영향 탐색은 ① 그 객체를 참조하는 모듈(`EXEC`·참조), ② 이름을 문자열로 쓰는 C# 코드, ③ 같은 컬럼을 읽는 모듈(좁혀서 경고, 테이블마다 요약)을 `spec check --impact`와 `apply`에서 경고로 알립니다(운영 연결이 없으면 `apply`의 동작은 그대로). 한계는 동적 SQL·상수를 거친 이름·수정 전 정의 기준·다른 DB 참조입니다. SQL 통합 테스트(`[SqlServerFact]`)는 작성했으나 개발 중에는 실행하지 못했고 CI와 사람이 돌립니다. 현장에서 운영 주요 테이블로 개별 경고 수(상한 10)를 보는 것은 사람 몫입니다. 설계는 `docs/superpowers/specs/2026-10-26-v2-1d-sql-impact-design.md`, 사용법은 README `pelly sql pull`·`pelly sql check`·SQL 영향 탐색 절을 보십시오.
> **구현(V2-1c): 2026-10-25.** 영향 탐색(심볼 기준, 의미 분석이 안 되면 「이름 기준(부정확)」으로 대체하고 표시), BuildHost 배포, `pelly doctor` 의미 분석 행, `pelly report`의 명세 완료율·그룹 빌드 통과율, E2E 벤치마크 `pelly-eval apply`(승인된 S6 정답 명세 12건, `eval/benchmark/apply/`)가 구현됐습니다. 현장 모델 실측(`--repeat N`)과 레거시 사례의 Windows 실측은 사람이 합니다(`deploy/field-results.md`).
>
> **구현(V2-1b): 2026-10-25.** 작업 그룹(`[G<n>]`: 시그니처·이름 변경, 영향 범위 빌드, 오류 귀속·재실행, 그룹 되돌리기)이 구현됐습니다. 영향 탐색과 벤치마크는 V2-1c입니다. 그룹 동작은 README `pelly apply` 절을 보십시오.
>
> **구현(V2-1a): 2026-10-24.** 명세 파일, `pelly spec new`·`pelly spec check`, 순차 `pelly apply`(`--continue`·`--resume`·`--review-at-end`)가 구현됐습니다. **영향 탐색은 V2-1c이며 아직 없습니다.** 설계는 `docs/superpowers/specs/2026-10-24-v2-1-apply-design.md`, 사용법은 README `pelly apply` 절을 보십시오.

OpenSpec의 "변경 제안 → 작업 목록 → 적용 → 보관" 흐름에서 착안했습니다. v1은 한 번에 메서드 하나만 고칠 수 있습니다. 이 기능은 그 단위를 유지한 채, 여러 파일에 걸친 변경을 사람이 정한 순서대로 처리합니다.

* **명세 파일 형식** (`changes/<id>.md`, 사람이 작성):
  ```markdown
  ---
  id: order-cancel
  status: draft            # draft | in-progress | done
  ---
  # 주문 취소 시 배송 상태 확인

  ## 목표
  이미 배송된 주문은 취소할 수 없게 한다.

  ## 수용 기준
  - AC1: 배송 완료 주문을 취소하면 InvalidOperationException 발생
    test: OrderServiceTests.Cancel_WhenShipped_Throws
  - AC2: 취소 SP는 배송 완료 주문의 상태를 바꾸지 않는다

  ## 작업
  - [ ] T1 cs  src/Orders/OrderService.cs#Cancel     배송 완료 상태면 InvalidOperationException 던지기 (AC1)
  - [ ] T2 sql db/sp/usp_CancelOrder.sql             WHERE 조건에 배송 완료 제외 추가 (AC2)
  ```
* **작업 줄 문법:** `- [ ] <ID> <cs|sql> <파일>[#심볼] <요청> [(AC 참조)]`. 정해진 문법으로만 파싱하며, 모델이 명세를 해석하지 않습니다.
* **보조 명령:**
  * `pelly spec new <id>`: 빈 템플릿 생성
  * `pelly spec check <파일>`: 문법, 대상 파일·심볼 존재 여부, AC 참조 유효성, 작업 수 상한(20개) 검사
* **실행 (`pelly apply changes/<id>.md`):**
  1. 작업을 위에서부터 하나씩 v1 FSM으로 실행합니다. 앞 작업의 결과는 파일에 반영돼 있으므로, 다음 작업의 SLICE가 바뀐 코드를 기준으로 컨텍스트를 만듭니다.
  2. 성공하면 명세 파일의 체크박스를 `[x]`로 바꾸고 run-id를 붙입니다(`- [x] T1 … <!-- run: 20261017-0931-ab12 -->`).
  3. 실패하면 기본적으로 멈춥니다(`--continue`이면 다음 작업으로 진행). `pelly apply --resume`으로 실패한 작업부터 다시 시작합니다.
  4. 모든 작업이 끝나면 `status: done`으로 바꾸고 `changes/archive/<날짜>-<id>.md`로 옮깁니다.
* **작업 그룹 (시그니처 변경용):**
  * v1에서는 메서드 시그니처를 바꾸면 호출하는 쪽에서 빌드 에러가 나서 검증을 통과할 수 없었습니다.
  * 작업에 `[G1]`처럼 그룹을 붙이면(연속, `cs`만), 그룹 안의 작업은 빌드 없이 실행하고 **그룹의 마지막 작업 뒤에 한 번에 빌드 검증**합니다. 허용 변경은 대상 멤버의 시그니처·이름뿐이며 다른 멤버 변경과 public 추가는 거절합니다.
  * 빌드는 그룹 파일의 프로젝트와 그것을 (간접 포함) 참조하는 프로젝트(작업 공간 폴더와 `.sln`·`.slnx`에서 찾음)를 대상으로 하고, 그룹 시작 전 기준 빌드와 끝난 뒤 검증 빌드의 차이(새 오류)만 봅니다. 검증할 수 없으면 편집 전에 종료 코드 4입니다 — `.csproj` 없음, 영향 범위를 확정할 수 없음(폴더 2000개 초과, 목록이나 속성을 읽을 수 없는 폴더, 읽을 수 없는 솔루션·프로젝트 파일, 작업 공간 밖을 가리키거나 대상을 알 수 없는 링크·정션 폴더, `$(…)` 속성으로 만든 `ProjectReference`, 범위 안 프로젝트의 `ProjectReference`가 없는 `.csproj`를 가리킴), 레거시 프로젝트를 Windows 밖에서 빌드, 빌드 도구 없음, 기준 빌드의 비컴파일러 오류·시간 초과, 참조되는 범위 안 프로젝트에 기준 빌드부터 있는 컴파일 오류(참조하는 프로젝트가 컴파일되지 않음) 등. 편집 뒤 검증 빌드가 검증할 수 없게 되면 그룹을 롤백하고 종료 코드 4입니다.
  * 새 오류가 그룹 작업의 대상 멤버나 그 작업이 더한 private 도우미 안, 또는 그 대상이 속한 형식의 선언 줄·기본 형식 목록이면 그 작업에 빌드 피드백을 주고 다시 실행합니다(`retry.max` 안에서; 재실행을 다 쓰면 롤백, 종료 코드 1). 명세에 없는 곳의 오류는 `<파일>#<멤버>`(멤버 밖이면 `<파일>:<줄>`)를 알리고 그룹 전체를 롤백합니다(종료 코드 1). 승인은 빌드 통과 뒤 그룹 단위로 한 번 받고, 거부하면 롤백합니다(종료 코드 2).
  * `--resume`은 `[x]`가 아닌 그룹 작업만 다시 실행합니다(롤백 중 복원하지 못한 파일의 작업은 `[x]`를 유지하므로 다시 실행되지 않습니다). `--dry-run`은 첫 미완료 작업이 그룹 작업이면 그 작업 하나만 그룹 모드로, 영향 범위 탐색과 빌드 없이 보여 주므로 종료 코드 4 사유는 검사하지 않습니다.
  * 이로써 v1 Non-goal이던 "DTO/인터페이스 시그니처 변경"을 지원합니다.
* **REVIEW:** 기본은 작업마다 diff를 승인받고, `--review-at-end`이면 전체 변경을 마지막에 한 번 승인받습니다(거부하면 전체 롤백).
* **기대 효과:** 변경 이력이 리포지토리에 문서로 남아, 폐쇄망 환경의 감사 추적과 코드 리뷰 자료로 쓸 수 있습니다.
* **추가 구성 요소:** `Specs/ChangeSpecParser.cs`, `Specs/SpecChecker.cs`, `Specs/SpecDocument.cs`, `Specs/SpecRunner.cs`, `Specs/SpecArchiver.cs`, `Specs/FsmTaskExecutor.cs`, `Commands/ApplyCommand.cs`, `Commands/SpecNewCommand.cs`, `Commands/SpecCheckCommand.cs`
  * 작업 그룹(V2-1b): `Specs/ApplyTransaction.cs`(apply·그룹 백업과 되돌리기), `Specs/GroupVerifier.cs`(영향 범위 기준·검증 빌드), `Specs/GroupErrorAttribution.cs`(새 오류의 작업 귀속), `Builds/ProjectReferenceGraph.cs`(영향 범위 탐색), `Files/BoundedWalker.cs`(작업 공간 폴더 탐색), `CSharp/MemberShapeGuard.cs`의 그룹 허용(`ShapeAllowance.TargetSignatureAndName`)

### 12.4 V2-2. 테스트 우선 검증 (Test-First Verification)
> **구현(V2-2a): 2026-10-26.** 단독 실행 `pelly cs --verify-test`(RED 확인 → 패치 → GREEN·회귀 확인)와 `--expect-break`, 테스트 우선 `summary.json`(`testFirst`)·`pelly report`의 수용 기준 충족률과 RED 확인 경고율이 구현됐습니다. 아래 도구 항목과 달리 **`--filter` 실행 대신 테스트 프로젝트 전체를 한 번 돌려** 지정 테스트의 결과를 읽고, 레거시는 `vstest.console.exe` 탐색 대신 설정 `test.command`(`{trx}` 필요)로 돌립니다. 명세의 `test:`·`breaks:`와 `pelly apply` 연동은 아래 V2-2b 줄을 보십시오. 설계는 `docs/superpowers/specs/2026-10-26-v2-2-test-first-design.md`, 사용법은 README `pelly cs` 절을 보십시오.
> **구현(V2-2b): 2026-10-26.** 명세의 수용 기준에 `test:`·`breaks:`를 적고 `pelly apply`가 연결 작업(첫 작업 RED, 마지막 작업 GREEN, 모든 연결 작업 회귀)으로 판정합니다. 작업 그룹은 그룹 기준 테스트의 컴파일 RED·GREEN·엄격 회귀를 보고(`GroupTestGreenFailed`·`GroupTestRegression`·`GroupTestUnverifiable`), `spec check`는 테스트 이름 오류·경고를 내며, `apply.json`의 `criteria`와 `pelly report`의 미검증·breaks 줄이 더해졌습니다. 사용법은 README `pelly spec`·`pelly apply` 절을 보십시오.
> **구현(V2-2c): 2026-10-26.** 벤치마크 러너에 `pelly-eval run --test-first`(판정 테스트로 `--verify-test` 실행)와 `compare --test-first`(판정이 아닌 보고), 테스트 우선 apply 사례 `eval/benchmark/apply-test-first/t1`~`t8`이 더해졌고, 남은 가드(`GroupTestFileChanged`, 교차 작업 `TestFileChanged`)가 구현됐습니다. 예비 측정(외부 개발 PC, 사례 승인 전)은 `docs/v2-2c-test-first-benchmark.md`입니다.

Superpowers의 TDD(Red → Green) 원칙에서 착안했습니다. v1 검증은 "컴파일이 되는가"까지만 확인합니다. 이 기능은 "요청한 동작을 하는가"까지 결정론적으로 확인합니다.

* **연결:** 명세의 수용 기준에 `test:`로 테스트 이름을 지정합니다. 단독 실행에서는 `pelly cs … --verify-test <테스트 이름>`으로 지정합니다.
* **흐름:**
  1. **RED 확인 (패치 전):** 지정한 테스트를 실행해 **실패하는지** 확인합니다. 이미 통과하면 "이 테스트는 변경을 검증하지 못함"으로 경고하고 사용자 확인을 받습니다.
  2. **패치 및 빌드 검증:** v1과 같습니다.
  3. **GREEN 확인 (패치 후):** 지정한 테스트가 **통과**해야 합니다. 실패하면 자가 교정 루프로 돌아갑니다.
  4. **회귀 확인:** 같은 테스트 프로젝트의 다른 테스트는 패치 전 결과(Baseline)와 비교해 **새로 실패한 테스트가 없어야** 합니다.
* **테스트 실행 도구:**
  * SDK 스타일: `dotnet test <project> --no-build --filter "FullyQualifiedName~<이름>"`
  * 레거시: `vswhere`로 찾은 `vstest.console.exe <dll> /Tests:<이름>`
* **피드백:** 실패한 테스트 이름, assert 메시지, 대상 파일 안의 스택 프레임만 전달합니다(재시도 피드백 예산 296 토큰 이내).
* **테스트 작성:** 수정의 기대 동작을 정하는 테스트는 v2에서도 사람이 작성합니다. 단, 테스트가 없는 코드의 **현재 동작을 고정하는** 테스트는 `pelly test`(§12.9)가 모델로 쓰고 코드가 판정합니다. 이 둘은 다릅니다. 기대값을 사람이 정하는 테스트는 여전히 사람 몫입니다. 명세에 `test` 유형 작업을 두어 모델이 테스트를 먼저 쓰게 하는 방식은 V2-2 결과를 보고 v2.x에서 검토합니다. 이때 테스트 작업은 "빌드는 되고 테스트는 실패(RED)"를 통과 기준으로 삼습니다.
* **추가 구성 요소:** `Verifiers/TestRunner.cs`(dotnet test / vstest 선택), `Verifiers/TestBaseline.cs`

### 12.5 V2-3. 프로젝트 규칙 파일 (`pelly.rules.md`)
> **구현: 2026-10-24.** 설계 결정 1–6은 `docs/superpowers/plans/2026-10-24-pelly-v2-3-project-rules.md`에 있고 이 절과 다른 점이 있습니다. 파일은 `pelly.rules.md`가 아니라 작업 공간 루트 `AGENTS.md`의 `## Pelly rules` 절입니다. 규칙은 시스템 메시지가 아니라 사용자 메시지의 CONTEXT 뒤에 들어가며, `forbid`는 선택된 규칙만이 아니라 모든 규칙을 검사하고 정규식 시간 초과는 종료 코드 4입니다. 검사는 원본 대비 추가된 줄의 **코드 부분**에만 하며, 주석과 문자열 리터럴은 C#은 Roslyn, SQL은 ScriptDom 토큰으로 빼고 봅니다(2026-10-24 추가). `pelly rules init`은 초안을 사람이 검토하게 합니다. **"규칙 도입 효과(+5%p)" KPI는 폐쇄망에서 판정합니다.** 외부 사전 측정(2026-10-24, 같은 GGUF 모델, `pelly rules init` 초안 그대로)은 46.00 → 46.67건(+1.3%p, 규칙과 무관한 두 작업의 흔들림)이었고 불변 조건 위반은 0건이었습니다. 내장 벤치마크에는 규칙을 지켜야 통과하는 작업이 없어서, 대상 코드에 보이지 않는 관례(`SYSDATETIME()`)를 판정하는 규칙 스위트 `eval/benchmark-rules` 6건을 더해 쟀고 규칙 없음 0/6 → 있음 6/6이었습니다. 현장 판정에도 이런 관례 작업과 `pelly-eval --no-rules` 대조 실행이 필요합니다(`docs/v2-3-rules-premeasure.md`). 사용법은 README `pelly rules` 절을 보십시오.

Claude Code의 `CLAUDE.md`, OpenSpec의 프로젝트 문서에서 착안했습니다. 다만 긴 문서를 통째로 넣지 않고, **지금 작업과 관련된 짧은 규칙만** 골라 넣습니다.

* **형식:** 한 줄에 규칙 하나(100자 이내). 앞에 태그를 붙입니다.
  ```markdown
  - [*] 주석과 로그 메시지는 한국어로 작성한다
  - [dapper,sql] SQL은 Dapper 파라미터로만 전달하고 문자열 연결을 쓰지 않는다
  - [async] async 메서드 이름은 Async로 끝낸다
  - [async] .Result / .Wait() 를 쓰지 않는다 | forbid: \.Result\b|\.Wait\(\)
  ```
* **선택 규칙 (결정론적):**
  * `[*]` 규칙은 항상 넣습니다.
  * 나머지는 태그 키워드가 SLICE 결과 텍스트에 나타날 때만 넣습니다(대소문자 무시).
  * 규칙 예산은 **150 토큰**이며, 시스템 프롬프트 예산(600)에서 나눠 씁니다(시스템 450 + 규칙 150). 넘치면 파일에서 아래쪽 규칙부터 뺍니다. `--verbose`에서 들어간 규칙과 빠진 규칙을 보여줍니다.
* **금지 패턴 검사:** `| forbid: <정규식>`이 붙은 규칙은 VERIFY 단계에서 **REPLACE 블록에 새로 추가된 줄**을 검사합니다. 일치하면 빌드 에러와 똑같이 자가 교정 피드백으로 보냅니다. 프롬프트로 부탁만 하는 것이 아니라 코드로 강제하는 장치입니다.
* **보조 명령:** `pelly rules check`: 문법, 길이, 중복, 정규식 유효성, 전체 토큰 수 검사
* **추가 구성 요소:** `Rules/RulesLoader.cs`, `Rules/RuleSelector.cs`, `Verifiers/ForbiddenPatternVerifier.cs`

### 12.6 v2 KPI (추가분)
| 지표 | 목표 (잠정) | 측정 기준 |
| :--- | :--- | :--- |
| **명세 완료율** | ≥ 70% | 명세 하나의 모든 작업이 사람 개입 없이 성공한 비율 (작업 3~8개 규모 벤치마크 명세 기준) |
| **수용 기준 충족률** | ≥ 80% | 테스트가 연결된 작업 중 GREEN·회귀 없음까지 통과한 비율. 예비 측정: `docs/v2-2c-test-first-benchmark.md` |
| **RED 확인 경고율** | 기록만 함 | 이미 통과하는 테스트가 지정된 비율. 높으면 명세 작성 가이드 보완. 예비 측정: `docs/v2-2c-test-first-benchmark.md` |
| **규칙 위반 잔존율** | 0% | 최종 승인된 패치에 `forbid` 패턴이 남은 비율 (불변 조건) |
| **규칙 도입 효과** | 성공률 +5%p 이상 | 규칙 파일 유무에 따른 v1 최종 작업 성공률 차이 |

벤치마크(§8.1)에 다중 작업 명세 10건과 테스트가 연결된 작업 20건을 추가합니다. 외부 샘플 코퍼스와 폐쇄망 세트 모두에 추가합니다.

### 12.7 v2에서도 하지 않을 것
* **사람 승인 없이** 모델이 작업을 분해·계획하는 기능. `pelly spec draft`(§12.8)는 모델 초안을 코드 검사와 사람 승인을 거쳐 명세 파일로만 쓰고, 실행하지 않습니다. 호출부·구현체 전파는 모델이 아니라 영향 탐색이 맡습니다.
* **스킬·지침 문서를 프롬프트에 통째로 넣는 방식.** 필요한 정보는 규칙 파일처럼 짧게 골라 넣습니다.
* **종료 조건 없이 모델이 다음 행동을 고르는 자율 에이전트 루프.** 모든 반복은 FSM이 정한 횟수 안에서만 일어납니다.

### 12.8 V2-4. 명세 초안 (pelly spec draft)
> **구현: 2026-10-27.** 설계는 `docs/superpowers/specs/2026-10-27-spec-draft-design.md`, 사용법은 README `pelly spec draft` 절과 `deploy/usage.md`를 보십시오. Windows 실행은 미확인이고 현장 모델 확인은 사람이 `deploy/field-results.md` 12절에 적습니다.

`pelly apply`는 사람이 쓴 명세의 작업 줄을 실행합니다. 작업 줄을 쓰려면 바꿀 파일과 멤버를 하나하나 찾아 적어야 하는데, 이 일을 모델이 초안으로 돕습니다. 채택은 사람이 합니다.

* **근거(S6 작업 분해 프로브, 같은 대상 모델, 12사례 × 5회, 생각 켬):**

  | 범주 | 통과 | 판단 |
  | :--- | ---: | :--- |
  | C# + SQL 동시 변경 | 15/15 | 초안 후보 |
  | 한 곳만 바꾸면 되는 요청 | 15/15 | 초안 후보 (과분해하지 않음) |
  | 같은 파일 여러 메서드 | 10/15 | 조건부 |
  | 시그니처 변경과 호출부·구현체 전파 | 5/15 | 모델에 맡기지 않음 |

  결론: 전파는 모델이 아니라 의미 기반 영향 탐색(V2-1c, V2-1d)이 하고, 없는 이름은 `spec check`의 이름 검사가 잡고, 이미 있는 것을 다시 하라는 지시처럼 코드를 읽어야 보이는 실패는 사람 승인이 거릅니다. 자세한 수치는 `docs/s6-planning-probe.md`입니다.
* **흐름:** `--scope`로 사람이 준 범위의 색인(파일 경로, 형식, 멤버 시그니처, 본문 없음)과 요청으로 모델을 한 번 부릅니다. 받은 줄 중 작업 줄 문법에 맞는 줄만 쓰고, 맞지 않는 줄은 고치지 않고 버린 줄로 원문 그대로 보입니다. 이 초안을 기존 `SpecChecker`로 검사하고(문법, 대상 존재, 문구 안 이름, 작업 수 상한), 그룹 작업에서 영향 탐색이 찾은 C# 의미 기반 대상은 코드가 작업 줄로 더합니다(「(영향 탐색으로 추가)」 표시). SQL·이름 기준·문자열 이름 호출 대상은 더하지 않고 경고만 냅니다. 사람이 승인하면 `changes/<id>.md`(`status: draft`)를 쓰고, 승인 전에는 `changes/` 아래 아무것도 쓰거나 바꾸지 않습니다(`--dry-run`이면 보이기만 합니다).
* **S6 조건 고정:** 시스템 프롬프트는 S6의 고정 프롬프트 문구 그대로, 생각 단계(상한 2048 토큰) 뒤 답 단계(상한 1024 토큰)의 2단계 호출, `num_ctx` 8192입니다. 첫 입력(시스템 + 색인 + 요청)이 4096 토큰을 넘으면 색인을 자르지 않고 모델을 부르기 전에 종료 코드 4로 멈춥니다(`--scope`를 좁힘).
* **하지 않는 것:** 초안을 바로 실행(실행은 늘 `pelly apply`), 수용 기준·`test:` 줄 쓰기(템플릿 자리만 둠), 범위 고르기, 모델이 고쳐 다시 쓰는 대화 루프(한 번 부르고 끝).
* **기록:** 초안(작업 1개 이상)이 나온 실행은 `.pelly/runs/<run-id>/draft.json`에 프롬프트, 생각, 답, 버린 줄, 더한 줄, 검사 결과, 결과를 남깁니다. 작업이 하나도 없는 실행은 기록을 남기지 않고 버린 줄은 화면에만 나옵니다. `pelly report`에는 넣지 않았습니다.
* **알려진 한계:** 생성자는 색인에 없습니다(S6 한계를 비교를 위해 그대로 둠). 모델은 수용 기준을 쓰지 않습니다. 모델 한 번 실행에 수십 초 걸립니다.

### 12.9 V2-5. 현재 동작 고정 테스트 (pelly test)
> **구현: 2026-10-29.** 설계는 `docs/superpowers/specs/2026-10-29-pelly-test-characterization-design.md`, 사용법은 README `pelly test` 절과 `deploy/usage.md`를 보십시오. 실측은 `docs/chartest-premeasure.md`(예비, Mac, Modern만)이고, Windows 실행과 현장 모델 확인은 사람이 `deploy/field-results.md` 13절에 적습니다.

레거시에는 테스트가 없는 코드가 많아 `pelly cs --test`는 기댈 기준이 없고 검증이 「빌드가 되는가」에서 멈춥니다. 현장 모델은 빌드는 되지만 실행하면 예외가 나는 수정을 `--test` 없이 6/6 냈습니다(`docs/m4-field-model-baseline.md` 3절). 그래서 먼저 **지금 동작을 고정하는 테스트**를 더합니다.

* **원칙(모델이 쓰고, 코드가 판정):** 특성 테스트는 판정이 기계적으로 됩니다. 지금 코드에서 통과하고, 두 번 돌려도 같고, 대상을 망가뜨리면 실패해야 합니다. 모델의 판단을 믿지 않습니다.
* **판정 7단계:** 형태, 단언·호출, 환경 의존 금지, 빌드, 현재 코드 통과(기존 테스트는 원본 상태와 비교), 재실행 일치, 변이 검출(대상 본문 → `throw`). 하나라도 어긋나면 피드백으로 재시도하고(`retry.max`), 판정을 건너뛴 테스트는 성공으로 치지 않습니다. 테스트별 결과(TRX)를 읽을 수 없는 실행 방식은 종료 4입니다.
* **운영 코드 보호:** 대상 파일은 실행 전후로 바이트가 같아야 합니다. 변이로 잠깐 바꾸고 `finally`에서 되돌리며 해시로 확인합니다. 강제 종료에 대비해 `.pelly/runs/<run-id>/chartest.pending`을 두고, 있으면 `test`·`cs`·`sql`·`apply`가 종료 4로 멈춥니다. 자동 복원은 하지 않습니다.
* **범위:** 메서드 하나, 기존 테스트 프로젝트(만들지 않음), 테스트 파일 하나(레거시 새 파일이면 csproj 하나)만 쓰기. SQL 테스트, 의존성 주입 도입, 여러 변이, `pelly ui`·`spec`·`apply` 연결은 하지 않습니다.
* **한계:** 현재 코드의 버그도 정답으로 굳힙니다. 사람이 diff의 기대값을 읽고 승인합니다(그래서 `--yes`는 자동화 용도). 변이는 하나뿐입니다. 개발 Mac 예비 측정은 Modern 사례만이고 레거시 net48은 Windows에서 현장 확인합니다.

### 12.10 V2-6. Classic ASP 수정 (pelly asp)
> **구현(1단계 A1): 2026-11-04.** 설계는 `docs/superpowers/specs/2026-11-04-pelly-asp-design.md`, 사용법은 README `pelly asp` 절과 `deploy/usage.md`를 보십시오. 예비 측정은 `docs/asp-premeasure.md`(Mac, 컴파일 검사 건너뜀)이고, Windows `cscript`와 현장 확인은 사람이 `deploy/field-results.md` 16절에 적습니다. 2단계 A2(명세에서 SP와 함께 바꾸기, SP → ASP 영향)는 아래 「A2」 단락입니다.

현장 백오피스는 화면·업무 로직이 Classic ASP(VBScript)이고 데이터는 SP라 요청은 대개 「SP를 고치고 그 SP를 부르는 ASP도 고친다」입니다. ASP에는 컴파일러도 테스트도 없으므로 **실행 없이 코드로 할 수 있는 판정을 모으고**, 실행 검증이 없다는 것을 분명히 알립니다.

* **원칙(모델이 쓰고, 코드가 판정):** 대상은 `.asp`·`.inc` 파일 하나의 `Function`·`Sub`·`Property` 하나(`--proc`, `--line`) 또는 줄 범위(`--lines A-B`, HTML 섞여도 됨)입니다. 모델은 프로시저를 고르지 않습니다. 흐름·재시도·되돌리기·종료 코드는 `cs`·`sql`과 같습니다.
* **판정(순서대로, 모두 원본 기준선 대비 새로 생긴 것만, 위치가 아닌 내용의 개수로 셈):** 패치 적용, 인코딩(파일 인코딩으로 쓸 수 없는 문자), 구조(`<% %>`·VBScript 블록·문자열·HTML 태그 짝, 지시문·include 줄 불변, 프로시저 이름·매개변수 불변), 안전(`ShellObject`, `DynamicCode`, `OnErrorResumeNext`, `SqlConcatRequest` — 끝내 남으면 종료 5), 이름(없는 함수·`Option Explicit`에서 선언 없는 변수), 프로젝트 규칙, VBScript 컴파일(Windows `cscript`로 컴파일만, 실행 문장 전에 `WScript.Quit 0`), SP 존재(개발 DB 연결이 있을 때만).
* **SQL 문자열 연결 규칙:** SQL 단어(`SELECT`·`INSERT`·`UPDATE`·`DELETE`·`EXEC`·`EXECUTE`·`MERGE`)를 담은 문자열에 `Request` 값이나 그 값을 받은 변수를 붙이면 거부합니다. `CLng`·`CInt`·`CDbl`·`CBool`·`CDate`, `Replace(x, "'", "''")`, `asp.sqlSanitizers`의 현장 함수로 감싸면 허용하고, `Trim(Request(…))`처럼 그 밖의 함수로 감싼 값은 여전히 위험합니다. 변수 상태는 문장 순서대로 정하되(`id = CLng(id)` 뒤는 안전), 조건 안의 대입은 위험을 더할 수만 있고 지우지 못합니다. `sql = sql & …`처럼 이어 붙이는 것도 셉니다. 원본의 같은 문장은 막지 않지만 **이미 위험한 줄을 고치면 새 문장**으로 봅니다.
* **바이트 보존:** 편집 영역 밖은 원본 바이트 그대로이고(줄 끝이 섞인 파일, 끝 줄바꿈 포함), 인코딩은 BOM → `CodePage` 지시문 → 바이트(UTF-8/CP949) → `asp.encoding`(기본 `cp949`) 순서로 정합니다. 공용 `TextDecoding`·`TextFileCodec`은 바꾸지 않았습니다. 해석기는 IIS처럼 문자열 안의 `%>`에서도 코드 블록을 끝냅니다.
* **「실행 검증 없음」:** 검토 화면과 실행 기록에 판정 줄과 고정 문구 「ASP는 실행해서 확인하지 않았습니다. 화면에서 직접 확인하십시오.」를 남기고(`--yes`도 같음), `summary.json`의 `asp.executed`는 늘 `false`입니다. 판정할 수 없던 검사(Mac의 컴파일, DB 연결 없는 SP 확인)는 「건너뜀」으로 보입니다.
* **기록·점검:** `summary.json`의 `asp` 블록, 실패 유형 `AspStructureErrors`·`AspUnknownNames`·`AspCompileErrors`·`AspSafetyRejected`·`AspEncoding`·`AspSpNotFound`·`AspUnsupportedLanguage`·`AspUnsupportedFile`, `pelly report`의 「ASP:」 줄, `pelly doctor`의 ASP 행 셋(설정에 `asp` 절이 있을 때만), PowerShell 자동완성.
* **하지 않는 것:** 실행 검증(IIS·브라우저, 로그인·세션이 필요), `global.asa`, 서버 JScript, 여러 파일·새 파일·include·지시문 변경, XSS 검사, COM 구성 요소, ASP.NET 이전, `pelly ui asp`. 운영 연결(`sql.production`)은 ASP 문맥·검사에 쓰지 않습니다.
* **예비 측정(Mac, 12사례 × 3회):** 종료 0 36/36, 모두 첫 시도, 편집 영역 밖 바이트 36/36 같음. 다만 판정을 통과한 결과 중 4회가 사람이 읽으면 틀렸습니다(VBScript `Or`가 단락 평가를 하지 않아 실행 시 멈추는 코드 3회, 범위 안의 기존 입력 줄을 지운 1회). 둘 다 컴파일 검사로도 잡히지 않아, 사람이 diff를 읽고 화면을 띄워 확인하는 것이 이 기능의 전제입니다.
* **한계:** 컴파일 검사는 Windows에서만 되고 현장 개발 PC의 `cscript`는 미확인입니다(별도 Windows 11 23H2 PC에서 확인). include는 합치지 않고 대상 파일만 컴파일하므로 파일 간 이름 중복은 못 잡습니다. 못 찾은 include·`Execute`가 있는 파일은 이름 검사가 경고로 낮아집니다.

* **A2: 명세에서 SP와 ASP를 함께 (구현 2026-11-09, 설계 `docs/superpowers/specs/2026-11-09-pelly-asp-a2-design.md`).** 명세 작업 종류에 `asp`가 더해졌습니다: `asp <파일>#Proc`·`#Class.Proc`·`#L<시작>-<끝>`(줄 범위는 asp에서만, 대상 필수). `spec check`는 `AspTargetRequired`·`LinesOutOfFile`·`LinesNotSupported`·`AspLinesAfterEdit`를 오류로, 파일 종류는 `asp.extensions`로 `KindMismatch`, 인코딩은 `AspEncoding`으로 검사하며 asp 작업은 요청문 식별자 검사를 하지 않습니다. 그룹은 cs·sql·asp를 섞을 수 있고 하나라도 실패하면 그룹 전체를 되돌립니다. C# 빌드·테스트 검증은 그룹의 cs 작업에만 하고 cs가 없는 그룹은 검증을 건너뜁니다(V2-1의 「그룹은 cs만」 규칙이 바뀜). 그룹 없는 sql이 적용된 뒤 asp가 실패하면 sql은 남기고 「함께 되돌리려면 [G1]로 묶으십시오」 경고를 냅니다. SP → ASP 영향은 사이트 SP 호출 색인(`asp.siteRoot`·`asp.extensions`·`asp.spCallFunctions`)으로 명세에 없는 호출을 `ImpactMissing`으로 경고합니다(SP당 최대 10개 + 「외 n곳」, include 표시). `spec check`는 `--impact`일 때, `apply`는 미완료 sql 작업이 있으면 운영 연결 설정과 상관없이 돌고 「계속할까요?」 확인에 합쳐지며, 완료한 asp 작업은 덮은 것으로 셉니다. 목록은 `.pelly/impact/<id>-sql.json`·`impact-sql.json`의 `asp` 배열입니다. asp 작업은 이번 apply에서 적용했거나 이미 완료한 sql 작업 SP의 서명을 SP 파일에서 읽어 모델 입력과 SP 검사에 쓰고(개발 DB 없이도, 해석 실패는 경고 후 DB 값), 적용 뒤 매개변수가 바뀐 SP를 부르는데 고치지 않은 호출은 경고하고 `aspCallersNotUpdated`·`pelly report`에 셉니다(기본값 있는 매개변수만 더해지면 기존 호출은 그대로 동작할 수 있다는 말을 덧붙임). `spec draft`는 asp 작업을 제안하지 않고 경고만 보이며, 예비 측정에서 sql 작업의 `[G1]` 때문에 `SqlInGroup` 오류로 실행되지 않던 초안이 이제 실행될 수 있습니다. 한계: 동적으로 만든 SP 이름, `rs.Open "EXEC x @a=1"`처럼 이름 붙은 인수를 문자열에 넣은 호출, SP 결과 열 변경의 영향은 찾지 못합니다. Windows 실행과 현장 확인(`deploy/field-results.md` 16절 A5·A6)은 미확인입니다.

### 12.11 V2-7. 클라이언트 JavaScript·CSS 수정 (pelly js·pelly css)
> **구현(1단계 W1): 2026-11-05, 2단계 W2: 2026-11-06.** 설계는 `docs/superpowers/specs/2026-11-04-pelly-js-css-design.md`, 사용법은 README `pelly js`·`pelly css` 절과 `deploy/usage.md`를 보십시오. 예비 측정은 `docs/web-premeasure.md`(Mac)이고, 현장 확인은 사람이 `deploy/field-results.md` 17절에 적습니다. 2단계 W2(페이지 안 `<script>`·`<style>`, `pelly asp --lines`가 클라이언트 블록에 걸칠 때 JS·CSS 판정 더하기)는 아래 W2 항목에 적었고, 3단계 W3(명세의 `js`·`css` 작업, 이름·선택자 변경과 부르는 곳 영향)은 별도 계획입니다.

백오피스 화면은 ASP·SP만이 아니라 클라이언트 JavaScript(대개 jQuery)와 CSS로도 되어 있어, 요청이 SP·ASP·JS를 함께 고치는 경우가 많습니다. JS·CSS에는 컴파일러가 없고 폐쇄망에는 Node·ESLint가 없을 가능성이 크므로, ASP처럼 **실행 없이, 외부 도구 없이 pelly 안에서 할 수 있는 판정을 모으고**, 브라우저에서 확인하지 않았다는 것을 분명히 알립니다.

* **원칙(모델이 쓰고, 코드가 판정):** 대상은 따로 있는 `.js` 파일의 이름 있는 함수 하나(`--function`, `객체.이름` 포함, `--line`은 가장 안쪽의 이름 있는 함수 — jQuery의 이름 없는 함수는 건너뛰고, 없으면 `--lines`를 안내) 또는 `.css` 파일의 규칙 하나(`--selector`, `@media` 안 포함, `--line`), 또는 줄 범위(`--lines A-B`, JS는 문장·CSS는 규칙 중간을 자르면 종료 3. CSS는 한 규칙 안의 선언 줄만 주는 것은 허용)입니다. 모델은 대상을 고르지 않습니다. 압축 파일과 라이브러리 파일(`web.vendorPaths`, 배포본 머리 주석)은 고치지 않습니다(종료 3). 흐름·재시도·되돌리기·종료 코드는 `cs`·`sql`·`asp`와 같습니다.
* **판정(순서대로, 모두 원본 기준선 대비 새로 생긴 것만, 위치가 아닌 내용의 개수로 셈):** 막는 것 — 패치 적용, 인코딩, 문법(JS는 `Acornima`로 고친 파일 전체를 해석, CSS는 괄호·문자열·주석 짝과 `;` 빠짐·규칙 밖 선언), JS 문법 수준, 대상 모양(함수 이름·매개변수, 선택자 불변), 안전(`JsEval`·`JsFunctionConstructor`·`JsStringTimer`·`JsDocumentWrite`·`JsExecScript`·`JsActiveX`, `CssExpression`·`CssBehavior`·`CssMozBinding`·`CssJavascriptUrl` — 끝내 남으면 종료 5), 프로젝트 규칙. **경고만** 하는 것 — JS 이름(내장·브라우저 전역·파일 선언·사이트 색인·`web.globals` 어디에도 없음), API(목표 수준에 없는 ES API, jQuery 기준 버전에 없거나 없어진 메서드), CSS 속성 이름, 같은 파일 뒤쪽 규칙에 덮이는 선언. 경고만 하는 이유는 페이지 안 `<script>`나 ASP가 출력한 코드에 있는 이름을 pelly가 볼 수 없어 「없다」를 확신할 수 없기 때문입니다.
* **문법 수준(가장 흔하고 조용한 고장):** 원본의 수준은 원본이 해석되는 가장 낮은 판(ES5, ES2015, …, ES2026)입니다. 목표 수준은 `web.jsTarget`이 `auto`(기본)면 원본 수준, 고정 값이면 그 값과 원본 수준 중 높은 쪽입니다(원본이 더 높으면 알림만 보이고 원본을 막지 않음). ES5 파일에 `let`·`const`·화살표 함수·템플릿 문자열·`class`가 들어가면 거절하고 영어 기능 이름으로 피드백해 재시도합니다. 객체 펼치기·나머지(`{...a}`)는 ES2018 문법으로 셉니다. 원본이 해석되지 않으면(깨진 파일, IE 전용 구문) 멈추지 않고 괄호·문자열·주석 짝 비교로 낮추며 문법·문법 수준·이름·API는 「건너뜀」으로 보입니다.
* **jQuery 버전:** 사이트 루트 아래 jQuery 본체의 머리 주석이나 파일 이름에서 버전을 찾고, 여럿이면 가장 낮은 버전을 기준으로 합니다(`web.jqueryVersion`으로 지정 가능). 모델 입력과 API 경고에 씁니다. 3.0 버전에서 경고가 붙었을 뿐 없어지지 않은 `.bind`·`.unbind`는 「없어진 것」 표에 넣지 않았습니다.
* **설정:** 새 설정은 `web.jsTarget`·`web.jqueryVersion`·`web.globals`·`web.vendorPaths` 넷입니다. 사이트 루트와 순수 ASCII 파일의 인코딩은 **`asp.siteRoot`·`asp.encoding`을 그대로 읽고** `web.siteRoot`·`web.encoding`이나 공용 `site.*`는 만들지 않았습니다(사람 결정 2026-11-04). 이름 규칙의 라이브러리 판정(`jquery*`, `bootstrap*`)은 `web.vendorPaths` 기본값에 들어 있어 설정으로 뺄 수 있습니다.
* **바이트 보존과 인코딩:** 편집 영역 밖은 원본 바이트 그대로입니다(ASP의 바이트 이어 붙이기 쓰기를 공용 `SplicingTextCodec`으로 꺼내 씀, `cs`·`sql`·`asp` 동작은 바뀌지 않음). 인코딩은 BOM → CSS `@charset` → 바이트(UTF-8/CP949) → `asp.encoding` 순서이고, 순수 ASCII 파일에 한글이 들어가면 저장한 인코딩을 검토에 알립니다.
* **「브라우저 실행 검증 없음」:** 검토 화면과 실행 기록에 판정 줄과 고정 문구 「JS·CSS는 브라우저에서 실행해 확인하지 않았습니다. 화면에서 직접 확인하십시오. 브라우저 캐시 때문에 바뀐 파일이 바로 반영되지 않을 수 있습니다.」를 남깁니다(`--yes`도 같음).
* **기록·점검:** `summary.json`의 `web` 블록(대상 단위, 인코딩, 원본·목표 수준, jQuery 기준 버전, 검사별 결과, 문법 수준 거절 수, 경고 수), 실패 유형 `JsSyntaxErrors`·`JsLevelExceeded`·`JsSafetyRejected`·`JsTargetChanged`·`CssStructureErrors`·`CssSafetyRejected`·`CssTargetChanged`·`WebVendorFile`·`WebMinifiedFile`·`WebUnsupportedFile`·`WebEncoding`, `pelly report`의 「JS:」·「CSS:」 줄, `pelly doctor`의 「JS·CSS」 행(설정에 `web`이나 `asp` 절이 있을 때, jQuery 버전 목록·기준 버전·`web.jsTarget`), PowerShell 자동완성. 사이트 색인은 `.pelly/cache/`에 파일 해시와 함께 저장합니다.
* **하지 않는 것:** 브라우저 실행·화면 비교, 외부 도구(Node·npm·ESLint·Prettier), 압축·라이브러리 파일 수정, TypeScript·JSX·ES 모듈 번들러·SCSS·LESS, 서버 JScript, 코드 모양 정리, XSS 검사, 여러 파일·새 파일, `pelly ui js`·`pelly ui css`.
* **예비 측정(Mac, 12사례 × 3회):** 36/36 실행이 종료 코드 0(성공), 모두 첫 시도, 편집 영역 밖 바이트 36/36 같음, 검사 실패·경고 0. 사례마다 원본에 없는 판정 문자열을 두어 요청한 변경이 들어갔는지 보며, 실행기가 이를 실행 전에 검사합니다. ES5 수준 함정 사례 둘(요청이 최신 문법을 허락하거나 시킴)에서도 모델이 [READ-ONLY: env]의 수준 힌트만으로 ES5 문법을 지켜 **문법 수준 거절은 0회**였고, 그래서 거절 → 재시도 경로는 실제 모델로 재지 못했습니다(가짜 모델 테스트로만 확인). 사람이 읽고 틀렸다고 본 결과는 없었으나, 요청 일부(`const`·템플릿 문자열)를 말없이 따르지 않은 결과와, jQuery 없이 쓰인 공용 `common.js`에 `$` 호출을 새로 넣은 결과(경고 없음)가 있어 diff를 읽는 것이 여전히 전제입니다.
* **2단계 W2(2026-11-06, 페이지 안 블록):** 설계는 `docs/superpowers/specs/2026-11-06-pelly-web-w2-design.md`입니다. 현장 수정의 대부분이 페이지 안 `<script>`에서 일어난다는 사람 답(2026-11-06)에 따라 W1 판정을 그대로 페이지 안 블록에 씁니다.
  * **대상:** 페이지(`asp.extensions` + `.htm`·`.html`)의 클라이언트 `<script>`·`<style>` 블록 안 함수·규칙·줄 범위. 대상이 블록 밖이거나 걸치면 종료 3(`WebOutsideBlock`)이고 `pelly asp`를 안내합니다. 인라인 이벤트 속성·`style=`·`href="javascript:"`, VBScript로 출력한 스크립트, 클라이언트 VBScript는 블록이 아닙니다.
  * **판정:** 블록마다 W1 판정을 따로 하고 원본 기준선 대비 새로 생긴 것만 셉니다. 해석기 입력은 페이지 크기의 보기 텍스트(블록 밖은 공백, 줄 번호 유지)이고, `<%= %>`는 자리 표시 이름으로 바꾸며, `<% 문장 %>`이 섞인 블록은 문법·문법 수준 해석을 건너뛰고 괄호·문자열·주석 짝 비교로 낮춥니다. 블록 밖 바이트, 블록 경계(`WebBlockChanged`), 블록 안 서버 코드(`WebServerCodeChanged`)가 바뀌면 거절하고, 서버 값이 필요하면 `pelly asp --lines`를 안내합니다. 규칙 검사는 블록 텍스트에 적용됩니다. `.htm`·`.html` 인코딩은 BOM 다음에 `<meta charset>`을 읽습니다.
  * **`pelly asp`와 `pelly edit`:** `pelly asp`는 편집 영역(`--lines`·`--proc`)과 겹친 블록마다 `JsSyntax`·`JsLevel`·`JsSafety`·`CssSyntax`·`CssSafety`(막음)와 `JsNames`·`JsApi`(경고) 행을 더하고 프롬프트를 `asp-v2`로 올립니다. 규칙 검사(`CodeMask.Asp`)는 바꾸지 않아 **`pelly asp`로 고친 페이지 안 JS에는 프로젝트 규칙이 걸리지 않습니다**(프로젝트 규칙의 금지 패턴에 언어 구분이 없기 때문, 나중에 정함). `pelly edit`은 `--function`→js, `--selector`→css, `--proc`→asp, `--line`·`--lines`는 위치(블록 안 js·css, 그 밖 asp), 지정 없음은 asp(`.htm`·`.html`은 js)로 보내고, `.htm`·`.html`은 asp로 가야 하면 종료 4입니다.
  * **이름·jQuery:** 페이지 대상은 같은 페이지의 다른 블록, include한 파일의 블록, `<script src>`로 부르는 `.js`의 선언을 아는 이름으로 치고 다른 페이지에만 있는 함수는 경고합니다. `.js` 대상은 모든 페이지 블록 선언을 알며, 공용 `.inc` 대상은 그것을 include하는 페이지들의 이름 합집합과 가장 낮은 jQuery를 씁니다. jQuery 기준은 `web.jqueryVersion` → 페이지가 부르는 jQuery → 사이트 순이고 출처(`setting`·`page`·`site`)를 기록합니다. 사이트 색인은 페이지 블록 선언과 include 목록도 담습니다(`.js`와 페이지의 파일 수 한도는 따로).
  * **기록:** 프롬프트 `js-page-v1`·`css-page-v1`(모델 입력에 [READ-ONLY: page]·[READ-ONLY: html] 추가), `summary.json`의 `web` 블록에 `container`(`file`·`page`)·`block`(`kind`, `startLine`, `endLine`, `serverCode`)·`jquerySource`, `pelly report`의 JS·CSS 줄에 「페이지 안 K건」.
  * **요소 검사(2026-11-09, v1.27):** 편집 때문에 새로 생긴 「페이지에 없는 id·name을 찾는 JS」를 경고 행 「요소」로 알립니다(막지 않음). W2 예비 측정 page-004에서 모델이 `name`만 있는 입력 칸을 `$("#custId")`로 찾은 틀린 수정이 계기입니다. 설계 `docs/superpowers/specs/2026-11-09-pelly-web-element-check-design.md`.
  * **W3a(2026-11-10, v1.28):** 명세의 `js`·`css` 작업, 명세 안 JS 함수 서명 변경과 부르는 곳 영향(`spec check --impact` 경고, apply 뒤 남은 호출 경고, 뒤 작업에 바뀐 서명 전달). 부르는 곳은 자동으로 고치지 않고 명세 작업으로 고칩니다. 설계 `docs/superpowers/specs/2026-11-10-pelly-web-w3-design.md`.
  * **W3b(2026-11-10, v1.29):** 폼 필드 이름 짝 검사(`pelly asp`의 `FormFields`, `pelly js`의 `Fields`, apply 끝 다시 계산과 `formFieldMismatches`). 확실한 짝만 보고 편집이 새로 만든 어긋남만 경고합니다.
  * **하지 않는 것·남은 것:** CSS 선택자 변경, 브라우저 실행 검증, `Request.QueryString` 짝 검사, 이벤트 속성(`onclick="frm.action=…"`) 안의 action 대입. `<% 문장 %>`이 섞인 블록은 분기별로 펼쳐 해석하지 않습니다(현장에서 얼마나 흔한지 `deploy/field-results.md` 17절에서 확인). Windows 실행과 현장 확인은 미확인입니다.
* **한계:** 실행하지 않으므로 화면 동작·런타임 오류·브라우저별 차이는 확인하지 못합니다. 따로 있는 JS 파일 하나로는 그 파일을 부르는 페이지를 모르므로 jQuery 기준은 사이트 전체의 가장 낮은 버전입니다(페이지 안 블록과 페이지가 부르는 `.js`는 W2에서 페이지 기준). 현장 브라우저(IE 모드 여부)와 jQuery 버전은 미확인입니다.
