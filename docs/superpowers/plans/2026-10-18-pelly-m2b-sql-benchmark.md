# Pelly-Coder M2b (SQL 경로·벤치마크 실행기) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `pelly sql`이 SSMS 스크립트 형태의 `.sql` 파일에서 저장 프로시저·함수·뷰 하나를 고치고, 안전 가드와 DB(객체 존재 + `SET NOEXEC` 컴파일)로 검증한다. 또 벤치마크 실행기(`PellyCoder.Eval`)가 작업 30건을 실행·판정해 KPI 표를 만든다.

**Architecture:**
* SQL 파일은 ScriptDom 토큰 스트림으로 `GO` 배치로 나누고, 대상 객체 배치 하나만 편집 영역으로 삼는다(D15).
* FSM은 C# 경로와 같은 오케스트레이터·전이표를 쓰되, `FsmOrchestratorFactory.CreateSql`이 SQL 전용 단계(`SqlPreflightPhase`, `SqlLocatePhase`, `SqlSlicePhase`, `SqlApplyPhase`)와 SQL 검증 단계(`SqlObjectStep`, `SqlCompileStep`)를 끼운다. EDIT·REVIEW·VERIFY 틀은 공유한다.
* DB 접근은 새 `ISqlExecutor` 뒤로 숨긴다(D6). 벤치마크 실행기는 코퍼스 복사본에서 작업마다 오케스트레이터를 실행하고, 결과를 독립적으로 다시 검증해 판정한다.

**Tech Stack:** .NET 10, Microsoft.SqlServer.TransactSql.ScriptDom(신규), Microsoft.Data.SqlClient 7.1.1, Dapper 2.1.89, YamlDotNet 18.1.0, SQL Server 2022(Docker / CI 서비스 컨테이너), Ollama 0.40.0

**Spec:** `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md`
* 결정 사항: D3, D6, D15, D16, D17
* 절: §5(ISqlExecutor), §7.1, §8.1(sql 설정), §8.4 SQL 줄, §8.6, §8.10, §8.12, §8.13(SQL 권한), §8.14(SQL 외부 도구 줄), §10.4, §10.5, §10.6, §11 M2 행
* PRD v1.7: FR-00(`pelly sql`), FR-01, FR-02 SQL, FR-06 SQL 파이프라인, FR-07, FR-08 SQL, §2.2 KPI, §8.1
* S5 결과: `docs/spikes/S5-sql-noexec.md`

**범위 밖 (이 계획에서 하지 않음):**
* M3로 넘김:
  * 벤치마크 50건으로 확장, 프롬프트·샘플링 튜닝
  * `report --sanitize`, `--pause-on-exit`, `--test`, DTO 슬라이스
  * publish 관련(S6·S7)
* M4로 넘김: 레거시 벤치마크를 실제 모델과 Windows 빌드로 전체 판정
* 이번에 하지 않음: SQL의 `dotnet user-secrets` 연결(아래 결정 R3)

## Global Constraints

* **이어받는 제약:** M1·M2a 계획의 Global Constraints를 그대로 따른다.
  * 빌드 환경: SDK `10.0.302`, `net10.0`, Nullable·ImplicitUsings·TreatWarningsAsErrors
  * 패키지: CPM(간접 고정 켜짐), MIT/Apache-2.0 라이선스만
  * Core 규칙: `Console.*` 금지(D20), `Process`는 `SystemProcessRunner` 안에서만
  * 종료 코드는 0~5만 쓴다.
  * 환경 테스트 속성: `[WindowsFact]`, `[SqlServerFact]`, `[OllamaFact]`
  * 언어: 식별자·커밋은 영어, 문서는 한국어
  * 커밋 끝에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
* **네임스페이스 규칙:** 네임스페이스 이름을 그 트리 안의 타입 이름과 같게 짓지 않는다. 새 네임스페이스는 둘이다.
  * `PellyCoder.Core.Sql` — 폴더 `src/PellyCoder.Core/Sql/`
  * `PellyCoder.Eval` — 폴더 `eval/PellyCoder.Eval/`
* **DB 접근:**
  * `Microsoft.Data.SqlClient`와 Dapper는 `PellyCoder.Core.Sql.SqlServerExecutor` 안에서만 쓰고, 나머지 코드는 `ISqlExecutor`로만 접근한다.
  * 문장은 항상 매개변수로 넘긴다. 사용자·모델 텍스트를 SQL 문자열에 이어 붙이는 것은 `CompileWithNoExecAsync`의 대상 배치 하나뿐이다.
* **NOEXEC 컴파일 순서(S5, 그대로):** 같은 연결에서 다섯 배치를 따로 보낸다.
  1. `BEGIN TRAN;`
  2. `SET NOEXEC ON;`
  3. `<대상 배치>`
  4. `SET NOEXEC OFF;`
  5. `IF @@TRANCOUNT > 0 ROLLBACK;`

  * 4·5번은 3번이 실패해도 `finally`에서 반드시 보낸다.
  * 연결에는 먼저 `SET ANSI_NULLS {ON|OFF}; SET QUOTED_IDENTIFIER {ON|OFF};`를 보낸다. 기본은 ON이고, 파일에서 대상 배치보다 앞에 둔 마지막 설정을 따른다(§8.10 0번).
  * 진단 줄 번호는 `SqlError.LineNumber + 대상 배치 시작 줄 − 1`로 계산한다.
* **명령 시간 제한:** SQL 명령은 30초(`SqlServerExecutor.CommandTimeoutSeconds = 30`)로 둔다. 연결 시간 제한은 연결 문자열의 값을 따른다.
* **새 설정 기본값(§8.1):**
  * 키: `sql.allowedHosts = []`, `sql.allowedDatabases = []`, `sql.compatLevel = 160`, `sql.connectionString = null`
  * `compatLevel` 허용값: `90, 100, 110, 120, 130, 140, 150, 160, 170`
  * 환경 변수 `PELLY_SQL_CONNECTION`(접두사 `PELLY__`와 별개)가 있으면 `sql.connectionString`보다 우선한다.
* **연결 문자열 비밀번호(D17):** 설정에서 온 `sql.connectionString`에 `Password` 또는 `Pwd` 키가 있으면 설정 오류(종료 4)로 처리한다. 메시지는 `sql:connectionString에 비밀번호가 있습니다 — 비밀번호가 있는 연결은 환경 변수 PELLY_SQL_CONNECTION으로만 지정하세요`.
  * 키는 `System.Data.Common.DbConnectionStringBuilder`로 해석하고 대소문자를 구분하지 않는다.
  * 로그·메시지·doctor 출력에서는 연결 문자열의 비밀번호 값을 `***`로 가린다.
* **SQL 파서:**
  * `compatLevel` N에 맞춰 `TSql{N}Parser`를 만든다(`SqlParsers.Create`).
  * `QUOTED_IDENTIFIER`는 파서 생성자 인자로도 넘긴다.
* **코퍼스:** `eval/corpus`는 읽기 전용이다. 쓰는 테스트·벤치마크는 임시 복사본에서만 돈다. DB의 객체 정의는 어떤 테스트에서도 바뀌면 안 된다(`OBJECT_DEFINITION` 전후 비교로 확인).
* **CI:** 각 작업을 마치기 전에 브랜치를 push해 CI 4잡이 녹색인지 확인한다.
  * SQL 통합 테스트는 `sql-integration` 잡에서 Skip 없이 돌아야 한다. `--fail-skips on`은 이미 설정돼 있다.

## Review Focus

1. **검증 계정에 `VIEW DEFINITION`이 없음**
   * 이 권한이 없으면 원본·수정본 모두 "모든 테이블 없음"이 되어, 새 오류만 세는 방식이 아무것도 걸러내지 못하고 통과시킨다(S5).
   * 기대 동작: PREFLIGHT에서 종료 4와 권한 안내.
   * 테스트: Task 7 `NoViewDefinition_IsEnvError_BeforeAnyLlmCall`
2. **같은 파일의 다른 배치(`SET` 줄, 다른 프로시저, `GRANT`)가 바뀌거나, 모델이 `GO`를 끼워 넣음**
   * 기대 동작: 파일에 쓰지 않고 SAFETY 피드백으로 재시도하고, 다른 배치의 바이트는 그대로 남는다.
   * 테스트: Task 5 `GoInsertedInTarget_IsRejected`, `OtherBatchChanged_IsRejected`; Task 12 `SettlementAll_EditsOnlyTargetBatch_GrantsUntouched`
3. **연결 대상이 허용 목록 밖이거나 연결 문자열이 없음, 또는 설정 파일에 비밀번호가 있음**
   * 기대 동작: 모델 호출 전에 각각 종료 5 / 4 / 4로 끝나고, 비밀번호는 어디에도 출력되지 않는다.
   * 테스트: Task 2 `ConfigConnectionWithPassword_IsConfigError`, `Mask_HidesPassword`; Task 7 `HostNotAllowed_Exit5_NoLlmCall`, `NoConnection_Exit4`
4. **원본 객체에 이미 컴파일 오류나 없는 객체 참조가 있음**
   * 원래 있던 문제는 세지 않고, 새로 생긴 것만 실패로 본다.
   * 기대 동작: 수정은 가능하고, 알림은 한 번 띄운다.
   * 테스트: Task 10 `OriginalAlreadyMissingTable_IsNotCounted`, `OriginalCompileError_IsNotCounted`
5. **문자열·주석 안의 `GO`·금지어, `#temp`·CTE·테이블 변수 이름**
   * 문자열·주석 안의 것으로 배치를 나누거나 거부하지 않는다.
   * `#temp`·CTE·테이블 변수 이름은 객체 존재 검사 대상에서 뺀다.
   * 테스트: Task 1 `GoInsideStringOrComment_DoesNotSplit`; Task 4 `TempCteAndTableVariable_AreNotObjects`; Task 5 `ForbiddenWordInStringOrComment_IsAllowed`

## 결정 (계획 단계 판단, 틀렸을 때 비용 포함)

* **R1. `ISqlExecutor`는 설계 §5의 범용 `QueryAsync<T>` 대신 목적별 메서드로 정한다.**
  * 이유: 가짜 구현으로 단위 테스트하기 쉽다(D6의 목적).
  * 틀렸을 때 비용: v2에서 새 질의가 필요하면 메서드를 하나 더 추가해야 한다.
  * 처리: §5는 Task 15에서 갱신한다.
* **R2. 원본 대상 배치에 금지 구문이 이미 있으면 LOCATE에서 종료 5로 끝낸다.**
  * 이유: PRD FR-07은 "위치 무관"이라고 하고, 이 경우 재시도해도 매번 거부될 뿐이다.
  * 틀렸을 때 비용: 본문에 `GRANT`·`xp_cmdshell` 등을 가진 프로시저는 pelly로 고칠 수 없다.
  * 처리: 사용자 안내 문구에 명시한다.
* **R3. 비밀번호가 있는 연결은 `PELLY_SQL_CONNECTION` 환경 변수로만 받는다.**
  * `dotnet user-secrets`는 전역 도구에서 의미가 없어서 빼고, PRD는 Task 15에서 갱신한다.
  * 틀렸을 때 비용: 외부 개발자는 환경 변수를 써야 한다.
* **R4. 연결 대상 허용 목록이 비어 있으면 아무 데도 접속하지 않는다.**
  * 결과는 종료 5와 "sql.allowedHosts(또는 allowedDatabases)에 추가하세요" 안내.
  * 틀렸을 때 비용: 처음 쓸 때 설정이 한 줄 더 필요하다.
* **R5. SQL 파싱 오류는 APPLY에서 `MatchFailed`로 처리한다.**
  * 종료 코드는 재시도를 다 쓰면 1, FailureKind는 `SqlParseErrors`, 피드백은 `SQL PARSE FAILED`.
  * 이유: 파싱 오류는 안전 위반(종료 5)이 아니다. 또 파싱되지 않은 텍스트는 안전 가드로 검사할 수 없으므로 쓰기 전에 걸러야 한다.
  * 틀렸을 때 비용: 실패 라벨이 패치 실패와 같은 전이를 공유한다(전이표 변경 없음).
* **R6. SQL 진단 코드는 두 종류로 쓴다.**
  * DB 오류는 `Msg{번호}`(예: `Msg207`, `Msg208`).
  * ScriptDom 파싱 오류는 `SQL{번호}`(예: `SQL46010`).
  * 비식별 리포트 정규식에 `SQL`을 더하는 것은 Task 15의 문서 갱신에 포함한다(구현은 M3).
* **R7. 3부·4부 이름(다른 DB·연결된 서버)은 객체 존재 검사에서 빼고 알림만 한다.**
  * 틀렸을 때 비용: 그런 참조의 오타는 잡지 못한다(코퍼스에는 없음).
* **R8. 모델 선택(LOCATE 5단계)은 SQL에 넣지 않는다(설계 §8.4 SQL 줄).**
  * 객체가 여럿인데 `--object`·`--line`이 없으면 대화형에서는 선택 UI, 비대화형에서는 종료 3으로 끝낸다.
* **R9. 벤치마크는 30건으로 정한다(§11 M2 완료 기준 "30건 이상 실행 가능").**
  * 구성은 C# 18건(Legacy 10, Modern 8) + SQL 12건이다.
  * 50건 확장은 M3이 맡는다.
  * async 전환처럼 시그니처를 바꾸는 요청은 넣지 않는다. MemberShapeGuard가 막는 의도된 동작이기 때문이다.

---

## 파일 구조 (신규·변경)

```text
Directory.Packages.props                     # 변경: ScriptDom 추가
src/PellyCoder.Core/
├── Abstractions/ISqlExecutor.cs            # 신규
├── Sql/
│   ├── SqlParsers.cs                       # 신규: compatLevel → TSqlNNNParser
│   ├── SqlObjectName.cs                    # 신규
│   ├── SqlBatchSplitter.cs                 # 신규: SqlScript, SqlBatch, 세션 설정 감지
│   ├── SqlConnectionText.cs                # 신규: 비밀번호 검사·가리기, 허용 목록 검사(SqlTargetPolicy)
│   ├── SqlServerExecutor.cs                # 신규 (SqlClient·Dapper는 여기만)
│   ├── SqlReferenceCollector.cs            # 신규: 참조 객체·컬럼·동적 SQL·#temp
│   ├── SqlSafetyGuard.cs                   # 신규
│   └── SqlSchemaSlicer.cs                  # 신규
├── Config/PellyOptions.cs, PellyOptionsLoader.cs   # 변경: SqlOptions
├── Fsm/
│   ├── RunRequest.cs                       # 변경: Kind, ObjectName, RunContext.Sql
│   ├── RunPhase.cs / Transitions.cs        # 변경: SafetyRejected 행 2개
│   ├── FsmOrchestrator.cs                  # 변경: SQL 상태 문구
│   ├── FsmOrchestratorFactory.cs           # 변경: CreateSql
│   └── Phases/
│       ├── SqlPreflightPhase.cs, SqlLocatePhase.cs, SqlSlicePhase.cs, SqlApplyPhase.cs   # 신규
│       ├── PatchWriter.cs                  # 신규: ApplyPhase의 백업·쓰기 부분을 추출
│       ├── ApplyPhase.cs                   # 변경: PatchWriter 사용
│       └── EditPhase.cs                    # 변경: Kind에 따라 프롬프트 선택
├── Verification/SqlObjectStep.cs, SqlCompileStep.cs   # 신규
├── Feedback/FeedbackBuilder.cs             # 변경: ForSqlSafety
├── Llm/PromptBuilder.cs, PromptResources.cs           # 변경: BuildSql, SQL 리소스
├── Llm/Resources/prompts/system.sql.txt, fewshot.sql.txt   # 신규 (version 1)
├── Logging/RunLogger.cs                    # 변경: 종류별 프롬프트 버전
└── Doctor/DoctorService.cs                 # 변경: SQL 점검 행
src/PellyCoder.Cli/                          # 변경: SqlCommand(신규), CliApp, Composition, Runners
eval/PellyCoder.Eval/                        # 신규: 벤치마크 실행기 (exe)
eval/benchmark/tasks/*.yaml, README.md       # 변경: 30건
tests/PellyCoder.Core.Tests/Sql/*, Fsm/Sql*Tests.cs, Fakes/FakeSqlExecutor.cs
tests/PellyCoder.Integration.Tests/Sql/*, E2E/Sql*Tests.cs
tests/PellyCoder.Eval.Tests/                 # 신규
docs/m2b-e2e.md, docs/m2b-benchmark.md       # 신규
```

---

### Task 1: ScriptDom·배치 분리·객체 이름

**Files:**
- Modify: `Directory.Packages.props`, `src/PellyCoder.Core/PellyCoder.Core.csproj`
- Create: `src/PellyCoder.Core/Sql/SqlParsers.cs`, `SqlObjectName.cs`, `SqlBatchSplitter.cs`
- Test: `tests/PellyCoder.Core.Tests/Sql/SqlBatchSplitterTests.cs`, `SqlObjectNameTests.cs`

**Interfaces:**
- Produces:
  * `static class SqlParsers { TSqlParser Create(int compatLevel, bool quotedIdentifier); IReadOnlyList<int> SupportedLevels; }`
    * 지원 수준: 90~170. 그 밖의 값은 `ArgumentOutOfRangeException`.
  * `sealed record SqlObjectName(string? Schema, string Name)`
    * `string Display` — 스키마가 있으면 `dbo.X`, 없으면 `X`. 대괄호는 벗긴다.
    * `static SqlObjectName Parse(string text)` — `X`, `dbo.X`, `[dbo].[X]`를 받는다. 점이 셋 이상이면 `FormatException`.
    * `bool SameAs(SqlObjectName other)` — 대소문자를 무시하고, 스키마가 없으면 `dbo`로 본다.
  * `enum SqlObjectKind { Procedure, Function, View }`
  * `sealed record SqlBatch(int Index, int StartLine, int EndLine, string Text, SqlObjectKind? ObjectKind, SqlObjectName? ObjectName, string? StatementType, IReadOnlyList<SqlParseError> ParseErrors)`
    * 줄은 1부터 세고 범위는 양 끝을 포함한다. `GO` 줄은 배치에 넣지 않는다.
    * `StatementType`은 ScriptDom 문장 타입 이름이다(예: `CreateProcedureStatement`).
  * `sealed record SqlParseError(int Line, int Column, int Number, string Message)` — `Line`은 파일 기준 줄이다.
  * `sealed record SqlSessionSettings(bool AnsiNulls, bool QuotedIdentifier)`; `static SqlSessionSettings Default`는 둘 다 ON.
  * `sealed record SqlScript(IReadOnlyList<SqlBatch> Batches, IReadOnlyList<string> LexErrors)`
    * `IReadOnlyList<SqlBatch> ObjectBatches`
    * `SqlSessionSettings SettingsBefore(int batchIndex)` — 그 배치보다 앞에서 혼자 있는 `SET ANSI_NULLS|QUOTED_IDENTIFIER ON|OFF` 문장 중 마지막 값을 쓴다.
  * `static class SqlBatchSplitter { SqlScript Split(string textLf, int compatLevel); }`

- [ ] **Step 1: 패키지 추가**
  * `Microsoft.SqlServer.TransactSql.ScriptDom`의 최신 안정판(170.x, MIT)을 `dotnet package search Microsoft.SqlServer.TransactSql.ScriptDom --exact-match`로 확인해 정확한 버전으로 고정한다. `TSql170Parser`가 있어야 한다.
  * Core csproj에 PackageReference를 추가한다.
- [ ] **Step 2: 실패하는 테스트 작성**
  ```csharp
  // SqlBatchSplitterTests — 실제 코퍼스 파일을 읽는다 (BOM 제거, \n 정규화)
  [Fact] SsmsScript_SplitsIntoSetSetObject()            // usp_CancelOrder.sql → 배치 3개, [2]는 Procedure dbo.usp_CancelOrder, StartLine 5, EndLine 52
  [Fact] SettlementAll_HasTwoObjectsAndTwoGrants()      // ObjectBatches 2개(usp_SettleDaily, usp_SettleMonthly), GRANT 배치는 ObjectKind null
  [Fact] View_And_Function_AreClassified()              // vw_ShippedOrders → View, fn_OrderTotal → Function
  [Fact] GoInsideStringOrComment_DoesNotSplit()         // "SELECT N'\nGO\n'" 와 "/*\nGO\n*/" 는 배치 1개
  [Fact] GoWithCountAndCase_Splits()                    // "go", "  GO  ", "GO 2" 각각 경계
  [Fact] CreateOrAlter_IsObjectBatch()                  // CREATE OR ALTER PROCEDURE → StatementType "CreateOrAlterProcedureStatement"
  [Fact] ParseErrorInBatch_IsKept_WithFileLine()        // 3번째 배치 구문 오류 → ParseErrors[0].Line이 파일 줄
  [Fact] UnclosedComment_IsLexError()                   // "/* 끝나지 않음" → LexErrors 비어 있지 않음
  [Fact] SettingsBefore_FollowsLastSet()                // SET QUOTED_IDENTIFIER OFF / GO / CREATE PROC → QuotedIdentifier false, AnsiNulls true
  // SqlObjectNameTests
  [Theory] Parse_Forms("usp_X", null, "usp_X") ("dbo.usp_X", "dbo", "usp_X") ("[dbo].[usp X]", "dbo", "usp X")
  [Fact] SameAs_TreatsMissingSchemaAsDbo_IgnoresCase()
  [Fact] Parse_FourPart_Throws()
  ```
- [ ] **Step 3: 실패 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Sql*"`
  * Expected: 컴파일 실패
- [ ] **Step 4: 구현**
  * 경계 찾기: `parser.GetTokenStream`의 `TSqlTokenType.Go` 토큰을 경계로 쓰고, 그 토큰이 있는 줄 전체를 경계 줄로 본다.
  * 토큰화는 `QUOTED_IDENTIFIER ON`으로 한다(설정은 나눈 뒤에야 알 수 있다). 객체 배치의 파싱은 `SettingsBefore(그 배치)`의 값으로 한다.
  * 배치 분류: 배치마다 따로 `Parse`해서 첫 문장이 Create/Alter/CreateOrAlter의 Procedure/Function/View 문장이면 객체 배치로 분류한다.
  * 줄 번호 보정: ScriptDom 오류 줄은 배치 기준이므로 `StartLine − 1`을 더한다.
- [ ] **Step 5: 통과 확인** — 같은 명령. Expected: 전부 통과.
- [ ] **Step 6: Commit** — `git commit -m "feat(sql): split scripts into GO batches and classify object batches"`

---

### Task 2: SQL 설정·연결 문자열 정책

**Files:**
- Create: `src/PellyCoder.Core/Sql/SqlConnectionText.cs`
- Modify: `src/PellyCoder.Core/Config/PellyOptions.cs`, `PellyOptionsLoader.cs`
- Test: `tests/PellyCoder.Core.Tests/Config/PellyOptionsLoaderTests.cs`, `PellyOptionsValidationTests.cs`, `tests/PellyCoder.Core.Tests/Sql/SqlConnectionTextTests.cs`

**Interfaces:**
- Consumes: `SqlParsers.SupportedLevels` (Task 1)
- Produces:
  * `sealed class SqlOptions { List<string> AllowedHosts; List<string> AllowedDatabases; int CompatLevel = 160; string? ConnectionString; }`
    * `PellyOptions.Sql` 속성으로 둔다.
    * 설정 키 `sql:*`는 `FutureKeys`에서 빼고 실제로 바인딩한다.
  * `PellyOptionsLoader.Load`의 처리 순서:
    1. 설정에서 온 `ConnectionString`의 비밀번호를 검사한다(Global Constraints의 문구).
    2. 그 뒤 `environment["PELLY_SQL_CONNECTION"]`이 비어 있지 않으면 그 값으로 덮어쓴다.
  * `compatLevel` 검증 오류 문구: `sql:compatLevel 값은 90, 100, …, 170 중 하나여야 합니다: {값}`
  * `static class SqlConnectionText`
    * `bool HasPassword(string cs)`
    * `string Mask(string cs)` — 비밀번호 값만 `***`로 바꾼다. 해석에 실패하면 문자열 전체를 `***`로 바꾼다.
    * `SqlEndpoint Endpoint(string cs)`
  * `sealed record SqlEndpoint(string DataSource, string Host, string? Database)`
    * `Host` 계산: `tcp:`/`np:`/`lpc:` 접두사와 `,포트`를 뗀 값.
    * `DataSource`는 인스턴스(`\INST`)를 포함한다.
  * `static class SqlTargetPolicy { SqlTargetCheck Check(SqlEndpoint endpoint, SqlOptions options); }`, `sealed record SqlTargetCheck(bool Allowed, string? Message)`
    * 호스트 일치: 허용 항목이 `DataSource` 또는 `Host`와 같으면 된다(대소문자 무시).
    * 데이터베이스 일치: 대소문자를 무시한다.
    * 거부 메시지(R4):
      * `SQL 서버 '{DataSource}'이(가) sql.allowedHosts에 없습니다 — 접속을 허용하려면 sql.allowedHosts에 추가하세요`
      * `데이터베이스 '{Database}'이(가) sql.allowedDatabases에 없습니다 — 접속을 허용하려면 sql.allowedDatabases에 추가하세요`

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] Defaults_MatchDesign()                          // 기존 테스트에 Sql 기본값 단언 추가: 빈 목록 2개, 160, null
  [Fact] ConfigConnectionWithPassword_IsConfigError()    // pelly.json에 "Server=x;Database=d;User Id=sa;Pwd=p" → Options null, 오류 문구 일치
  [Fact] EnvConnectionWithPassword_IsAccepted_AndWins()  // PELLY_SQL_CONNECTION이 설정 파일 값보다 우선, 오류 없음
  [Fact] GenericEnvPrefixConnection_IsTreatedAsConfig()  // PELLY__SQL__CONNECTIONSTRING에 Password → 오류
  [Theory] CompatLevel_Validation(80 → 오류, 160 → 통과, 175 → 오류)
  [Fact] Mask_HidesPassword()                            // "...;Password=a;b" 형식 포함, 출력에 원래 비밀번호 문자열 없음
  [Theory] Endpoint_NormalizesHost("tcp:db1,1433" → Host db1) ("localhost,14333" → localhost) (@"db1\SQLEXPRESS" → Host db1, DataSource db1\SQLEXPRESS)
  [Fact] Policy_EmptyLists_RejectWithHint()
  [Fact] Policy_HostMatchesByHostOrDataSource_IgnoresCase()
  [Fact] Policy_DatabaseNotAllowed_Rejects()
  ```
- [ ] **Step 2: 실패 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Config*" --filter-namespace "*Sql*"`
- [ ] **Step 3: 구현** — `sql:allowedHosts`·`sql:allowedDatabases`는 배열로 바인딩한다(기존 `IsOpenEnded` 규칙 유지).
- [ ] **Step 4: 통과 확인** — 같은 명령 + 전체 `dotnet test tests/PellyCoder.Core.Tests`
- [ ] **Step 5: Commit** — `git commit -m "feat(config): bind sql options, reject passwords in config, add target allowlist"`

---

### Task 3: SQL 실행기

**Files:**
- Create: `src/PellyCoder.Core/Abstractions/ISqlExecutor.cs`, `src/PellyCoder.Core/Sql/SqlServerExecutor.cs`, `tests/PellyCoder.Core.Tests/Fakes/FakeSqlExecutor.cs`
- Modify: `src/PellyCoder.Core/PellyCoder.Core.csproj` (Microsoft.Data.SqlClient, Dapper)
- Test: `tests/PellyCoder.Integration.Tests/Sql/SqlServerExecutorTests.cs`

**Interfaces:**
- Consumes: `SqlObjectName`, `SqlSessionSettings` (Task 1)
- Produces (R1):
  ```csharp
  public interface ISqlExecutor
  {
      Task<SqlServerProbe> ProbeAsync(CancellationToken ct);
      Task<IReadOnlyList<SqlObjectName>> FindMissingAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct);
      Task<IReadOnlyList<SqlTableSchema>> GetSchemasAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct);
      Task<IReadOnlyList<SqlObjectName>> GetReferencedEntitiesAsync(SqlObjectName obj, CancellationToken ct);
      Task<IReadOnlyList<SqlCompileError>> CompileWithNoExecAsync(string batch, SqlSessionSettings settings, CancellationToken ct);
  }
  public sealed record SqlServerProbe(string Version, string Database, bool CanViewDefinition);
  public sealed record SqlColumnInfo(string Name, string TypeDisplay, bool Nullable, bool IsPrimaryKey);
  public sealed record SqlIndexInfo(string Name, IReadOnlyList<string> KeyColumns, bool IsUnique);
  public sealed record SqlTableSchema(SqlObjectName Name, string ObjectType, IReadOnlyList<SqlColumnInfo> Columns, IReadOnlyList<SqlIndexInfo> Indexes);
  public sealed record SqlCompileError(int Number, int Line, string Message);   // Line: 배치 기준
  public sealed class SqlUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
  ```
  * `ProbeAsync`가 돌려주는 값: `@@VERSION` 첫 줄, `DB_NAME()`, `HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') = 1`.
  * `FindMissingAsync`: `OBJECT_ID(@name)`가 NULL인 이름을 입력 순서대로 돌려준다.
  * `GetSchemasAsync`
    * 대상: 사용자 테이블(`U`)과 뷰(`V`)만. 그 밖의 종류는 조용히 뺀다.
    * `ObjectType`: `TABLE` 또는 `VIEW`
    * 컬럼: `column_id` 순서
    * `TypeDisplay` 형식: `int`, `nvarchar(200)`(nchar·nvarchar는 `max_length/2`), `varchar(max)`, `decimal(18,2)`, `datetime2(0)`
    * 인덱스: PK 인덱스를 뺀 나머지
  * `GetReferencedEntitiesAsync`: `sys.dm_sql_referenced_entities(@name, 'OBJECT')`의 스키마·이름. 대상 객체가 DB에 없으면 빈 목록이다.
  * `CompileWithNoExecAsync`: Global Constraints 순서를 따르고, 대상 배치에서 나온 심각도 11 이상의 오류를 모두 돌려준다.
  * 연결 수준 실패는 모두 `SqlUnavailableException`으로 바꾸고, 메시지의 연결 문자열은 가린다. 해당하는 경우:
    * 로그인
    * DB 열기
    * 네트워크
    * 시간 초과(`-2`)
    * 연결 문자열이 null
  * `SqlServerExecutor(string? connectionString)`, `const int CommandTimeoutSeconds = 30`
  * `FakeSqlExecutor`(테스트용)
    * 설정할 수 있는 상태: `Probe`, `Missing`(이름 집합), `Schemas`, `Referenced`, `Compile`(배치 텍스트 → 오류 목록 함수)
    * 기록: `Compiled`(받은 배치 목록), `Calls`
    * `ThrowOnProbe(Exception)`
    * 이후 작업이 재사용한다.

- [ ] **Step 1: 실패하는 테스트 작성** (`[SqlServerFact]`, `TestEnv.SqlConnection`, 배포된 `PellyCorpus`)
  ```csharp
  [SqlServerFact] Probe_ReportsDatabaseAndViewDefinition()      // Database "PellyCorpus", CanViewDefinition true (sa)
  [SqlServerFact] FindMissing_ReturnsOnlyUnknown()              // [dbo.Orders, dbo.NoSuchTable, Customers] → [dbo.NoSuchTable]
  [SqlServerFact] GetSchemas_OrdersHasPkAndTypes()             // Orders: OrderId int NOT NULL PK, CancelReason nvarchar(200) NULL; 프로시저 이름은 결과에서 빠짐
  [SqlServerFact] ReferencedEntities_OfCancelOrder_IncludeOrders()
  [SqlServerFact] Compile_ValidAlter_NoErrors_DefinitionUnchanged()
  [SqlServerFact] Compile_MissingColumn_Is207_WithBatchLine()   // S5 사례 ⓑ, Line은 배치 기준
  [SqlServerFact] Compile_SyntaxError_Is102()
  [SqlServerFact] Compile_CreateOfExistingObject_HasNoErrors()  // 코퍼스 파일 그대로의 CREATE PROCEDURE(이미 배포됨) → 오류 0 (NOEXEC가 이름 충돌을 실행 시점으로 미룸을 고정)
  [SqlServerFact] Compile_LeavesNoOpenTransaction()             // 컴파일 실패 후에도 같은 실행기로 다음 Compile이 정상
  [SqlServerFact] Compile_QuotedIdentifierOff_IsApplied()       // QuotedIdentifier false에서 "SELECT \"x\"" 가 문자열로 해석돼 오류 없음
  [Fact] BadServer_ThrowsUnavailable_WithMaskedText()           // 127.0.0.1,1 + Password=secret, Connect Timeout=1 → SqlUnavailableException, 메시지에 "secret" 없음
  [Fact] NullConnection_ThrowsUnavailable()
  ```
  모든 `[SqlServerFact]`는 끝에 대상 객체의 `OBJECT_DEFINITION`이 시작 전과 같은지 단언한다.
- [ ] **Step 2: 실패 확인**
  * 로컬 SQL 컨테이너: `docker compose -f eval/corpus/Sql/docker-compose.yml up -d` → `deploy.sh`
  * 환경 변수: `PELLY_TEST_SQL="Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=Pelly_Local_2026!;TrustServerCertificate=True"`
  * Run: `dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*Sql*"`
- [ ] **Step 3: 구현**
  * 연결: 호출마다 `SqlConnection`을 새로 연다(풀 사용).
  * 쿼리: Dapper로 매개변수를 넘긴다.
  * 컴파일: 열린 연결 하나에서 순서대로 보낸다.
- [ ] **Step 4: 통과 확인**
  * 로컬에서 같은 명령으로 통과를 확인한다.
  * push 후 `sql-integration`에서 Skip 없이 통과해야 한다.
- [ ] **Step 5: Commit** — `git commit -m "feat(sql): add SQL executor with NOEXEC compile and metadata queries"`

---

### Task 4: 참조 수집기

**Files:**
- Create: `src/PellyCoder.Core/Sql/SqlReferenceCollector.cs`
- Test: `tests/PellyCoder.Core.Tests/Sql/SqlReferenceCollectorTests.cs`

**Interfaces:**
- Consumes: `SqlParsers`, `SqlObjectName`, `SqlBatch` (Task 1)
- Produces:
  * `sealed record SqlObjectReference(SqlObjectName Name, int Line, int Column)` — 처음 나온 위치이고 줄은 배치 기준이다.
  * `sealed record SqlReferences(IReadOnlyList<SqlObjectReference> Objects, IReadOnlySet<string> Columns, bool HasDynamicSql, bool UsesTempTables, IReadOnlyList<string> SkippedMultiPart)`
  * `static class SqlReferenceCollector { SqlReferences Collect(string batchText, SqlObjectName target, int compatLevel, bool quotedIdentifier); }`
  * 넣는 것(`Objects`):
    * `NamedTableReference`
    * 스키마가 붙은 함수 호출(`FunctionCall` + `MultiPartIdentifierCallTarget`)
    * `SchemaObjectFunctionTableReference`
    * `ExecutableProcedureReference`의 프로시저 이름
  * 빼는 것:
    * `#`/`##`로 시작하는 이름
    * 배치 안 CTE 이름(스키마 없는 경우)
    * `inserted`/`deleted`
    * 대상 자신
    * 스키마 없이 `sp_`/`xp_`로 시작하는 프로시저
    * 변수로 지정한 EXEC
  * 3부·4부 이름은 `SkippedMultiPart`에 원문으로 남긴다(R7).
  * `Columns`: `ColumnReferenceExpression`의 마지막 식별자. 대소문자를 무시하는 집합이다.
  * `HasDynamicSql`: `EXEC(@x)`/`EXEC('…')`(`ExecutableStringList`) 또는 `sp_executesql` 호출
  * `UsesTempTables`: `#` 테이블을 참조하거나 생성함
  * 대상 배치가 파싱되지 않으면 `ArgumentException`을 던진다. 호출자는 파싱된 배치에만 쓴다.

- [ ] **Step 1: 실패하는 테스트 작성** (코퍼스 객체 배치 + 인라인 문자열)
  ```csharp
  [Fact] CancelOrder_ReferencesOrdersAndHistories()     // Objects = [dbo.Orders, dbo.OrderHistories] 순서, 대상 자신 없음
  [Fact] TempCteAndTableVariable_AreNotObjects()        // #Sales, WITH x AS (...), @t table → Objects에 없음, UsesTempTables true
  [Fact] DynamicSql_IsFlagged()                         // usp_SearchOrdersDynamic → HasDynamicSql true, sp_executesql는 Objects에 없음
  [Fact] SchemaQualifiedFunction_IsObject()             // SELECT dbo.fn_OrderTotal(o.OrderId) → dbo.fn_OrderTotal
  [Fact] ExecProcedure_IsObject_SystemProcIsNot()       // EXEC dbo.usp_X; EXEC sp_help → dbo.usp_X만
  [Fact] MultiPartNames_AreSkippedWithText()            // OtherDb.dbo.T, Srv.Db.dbo.T → SkippedMultiPart 2개
  [Fact] Columns_AreCollected_CaseInsensitive()         // "o.CancelReason" → Columns.Contains("cancelreason")
  [Fact] FirstOccurrence_LineIsBatchRelative()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현(`TSqlFragmentVisitor` 하나로) → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Sql*"`
- [ ] **Step 5: Commit** — `git commit -m "feat(sql): collect referenced objects, columns, dynamic SQL and temp tables"`

---

### Task 5: SQL 안전 가드

**Files:**
- Create: `src/PellyCoder.Core/Sql/SqlSafetyGuard.cs`
- Test: `tests/PellyCoder.Core.Tests/Sql/SqlSafetyGuardTests.cs`

**Interfaces:**
- Consumes: `SqlBatchSplitter`, `SqlScript`, `SqlBatch`, `SqlParseError` (Task 1)
- Produces:
  * `sealed record SqlSafetyResult(bool Ok, string? Reason, IReadOnlyList<SqlParseError> ParseErrors)`
    * `Reason`은 영어이고 모델 피드백에 그대로 실린다.
    * 파싱 오류일 때는 `Ok=false`, `Reason=null`, `ParseErrors`가 비어 있지 않다.
  * `static class SqlSafetyGuard`
    * `SqlSafetyResult Check(SqlScript original, int targetIndex, string patchedLf, int compatLevel)`
    * `IReadOnlyList<string> ForbiddenIn(SqlBatch batch, int compatLevel)` — 금지 구문의 표시 이름 목록이다. LOCATE가 원본에 쓴다(R2).
  * 검사 순서와 `Reason` 문구(그대로 쓴다):
    1. 배치 수가 다르면: `the edit added or removed a GO batch separator. Keep the whole object in one batch and do not add GO.`
    2. 대상이 아닌 배치의 텍스트가 하나라도 다르면: `text outside the target object batch changed. Only the target object may change.`
    3. 대상 배치 파싱 오류 → `ParseErrors` (줄은 파일 기준)
    4. 최상위 문장이 1개가 아니거나, 문장 타입이 원본과 다르거나, 객체 이름이 원본과 `SameAs`가 아니면: `the target batch must contain exactly one {원본 문장 표시} statement for {원본 이름 Display}.`
       * 원본 문장 표시 예: `CREATE PROCEDURE`, `ALTER VIEW`, `CREATE OR ALTER FUNCTION`
    5. 금지 구문이 있으면: `forbidden construct '{표시 이름}' in the target object. Remove it.`
  * 금지 구문 표시 이름(PRD FR-07):
    * `SET NOEXEC`, `SET PARSEONLY`, `SET FMTONLY`
    * `SHUTDOWN`, `RECONFIGURE`, `xp_cmdshell`
    * `OPENROWSET`, `OPENDATASOURCE`, `ALTER DATABASE`
    * `CREATE LOGIN`, `ALTER LOGIN`, `DROP LOGIN`
    * `GRANT`, `REVOKE`, `DENY`

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Theory] AllowedForms_Pass(CREATE PROCEDURE / ALTER FUNCTION / CREATE OR ALTER VIEW 각각, 본문만 바꾼 수정)
  [Fact] SsmsFile_BodyEdit_Passes()                    // usp_CancelOrder.sql의 IF 조건 수정
  [Fact] GoInsertedInTarget_IsRejected()               // Reason 1번 문구
  [Fact] OtherBatchChanged_IsRejected()                // SET ANSI_NULLS OFF로 바꿈 → Reason 2번
  [Fact] SecondStatementAfterObject_IsRejected()        // 뷰 뒤에 "; DROP TABLE x" 추가 → Reason 4번 (뷰는 한 문장만 담음)
  [Fact] RenamedObject_IsRejected()                     // dbo.usp_CancelOrder2 → Reason 4번
  [Fact] CreateChangedToAlter_IsRejected()
  [Theory] Forbidden_EachIsRejected(15개 구문 각각을 프로시저 본문에 넣음 → Reason 5번, 표시 이름 일치)
  [Fact] ForbiddenWordInStringOrComment_IsAllowed()    // N'xp_cmdshell', -- SHUTDOWN, /* GRANT */
  [Fact] ParseError_ReturnsFileLines()                  // Ok false, Reason null, ParseErrors[0].Line == 파일 줄
  [Fact] ForbiddenIn_ListsOriginalConstructs()          // GRANT가 본문에 있는 원본 → ["GRANT"]
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인** (같은 명령)
- [ ] **Step 5: Commit** — `git commit -m "feat(sql): add SQL safety guard for target-batch edits"`

---

### Task 6: 스키마 슬라이서

**Files:**
- Create: `src/PellyCoder.Core/Sql/SqlSchemaSlicer.cs`
- Test: `tests/PellyCoder.Core.Tests/Sql/SqlSchemaSlicerTests.cs`, `tests/PellyCoder.Integration.Tests/Sql/SqlSchemaSlicerDbTests.cs`

**Interfaces:**
- Consumes: `ISqlExecutor`, `FakeSqlExecutor` (Task 3), `SqlReferences` (Task 4), `SliceResult`·`SlicePart`·`EditableRegion` (기존, `RoslynSlicer.cs`), `ITokenCounter`
- Produces: `static class SqlSchemaSlicer`
  * 시그니처: `Task<SliceResult> BuildAsync(SqlBatch target, SqlReferences refs, ISqlExecutor sql, ITokenCounter tokens, int contextBudget, int compatLevel, CancellationToken ct)`
  * 조회할 이름: `refs.Objects` 이름에 `GetReferencedEntitiesAsync(target.ObjectName)` 결과를 더해 순서대로 중복을 뺀다.
  * 파트:
    * `target` — 배치 원문. 우선순위 `int.MaxValue`이고 제거할 수 없다.
    * `schema` — 우선순위 1
  * 스키마 텍스트(§8.6 형식):
    ```text
    TABLE dbo.Orders (OrderId int NOT NULL PK, Status tinyint NOT NULL, ShippedAt datetime2 NULL)
    INDEX IX_Orders_Status (Status)
    UNIQUE INDEX UX_Customers_Email (Email)
    VIEW dbo.vw_ShippedOrders (OrderId int NOT NULL, ...)
    ```
  * 예산을 넘으면 다음 순서로 줄인다.
    1. 인덱스 줄 전부(뒤 테이블부터)
    2. `refs.Columns`에 없는 PK 아닌 컬럼(뒤 테이블부터)
    3. 테이블 줄 전체(뒤에서부터)
  * 줄인 내용은 `Omitted`에 `schema-indexes` / `schema-columns` / `schema-tables` 종류로 남긴다.
  * `target` 단독으로 예산을 넘으면 `TargetTooLarge = true`가 된다.
  * `Regions`: `[EditableRegion(new(target.StartLine, target.EndLine), "target")]`
  * `LanguageHint`: `T-SQL, SQL Server compatibility level {compatLevel}`

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // 단위 (FakeSqlExecutor)
  [Fact] RendersTablesIndexesAndViews_InReferenceOrder()
  [Fact] OverBudget_DropsIndexesFirst_ThenUnusedColumns_ThenTables()   // 예산을 단계별로 줄여 각 단계의 결과 텍스트 단언, PK·사용 컬럼은 2단계까지 유지
  [Fact] TargetAloneOverBudget_IsTooLarge()
  [Fact] Region_IsTargetBatchLines()
  [Fact] ReferencedEntities_AreMergedWithoutDuplicates()
  // DB ([SqlServerFact])
  [SqlServerFact] CancelOrder_SchemaHasOrdersAndHistories()            // "TABLE dbo.Orders (" 와 "CancelReason nvarchar(200) NULL" 포함
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Sql*"` 후 Integration `*Sql*`
- [ ] **Step 5: Commit** — `git commit -m "feat(sql): build schema context slice within the context budget"`

---

### Task 7: SQL FSM — 요청·사전 점검·대상 찾기

**Files:**
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs`, `RunPhase.cs` (변경 없음 확인), `Transitions.cs`, `FsmOrchestrator.cs` (`Describe`)
- Create: `src/PellyCoder.Core/Fsm/Phases/SqlPreflightPhase.cs`, `SqlLocatePhase.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/TransitionsTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/SqlLocateFlowTests.cs`

**Interfaces:**
- Consumes: Task 1~5, `PreflightPhase`(기존)
- Produces:
  * `RunRequest`에 선택 매개변수 두 개를 맨 끝에 추가한다: `string? ObjectName = null, EditKind Kind = EditKind.CSharp`. 기존 호출부는 바뀌지 않는다.
  * `RunContext.Sql`(SQL 실행에서만 설정)
    * 타입: `sealed class SqlRunState(SqlScript originalScript, int targetIndex, SqlObjectName target, SqlObjectKind kind, string statementType, SqlSessionSettings settings, int compatLevel)`
    * 읽기 전용 속성: 생성자 인자 그대로 — `OriginalScript`, `Target`, `Kind`, `StatementType`, `Settings`, `CompatLevel`
    * 가변 속성:
      * `int TargetIndex`
      * `SqlBatch CurrentTarget`
      * `IReadOnlyList<SqlObjectName>? OriginalMissing`
      * `IReadOnlyList<Diagnostic>? OriginalCompileErrors`
      * `bool NoticesShown`
  * `Transitions` 추가 행:
    * `(Preflight, SafetyRejected) → Failed(5)`
    * `(Locate, SafetyRejected) → Failed(5)`
  * `SqlPreflightPhase(PreflightPhase inner, ISqlExecutor sql, SqlOptions options) : IPhase`
    * `inner`가 Ok일 때만 이어서 아래를 순서대로 확인한다. 모두 모델 호출 전이다.
    * 연결 문자열 없음 → EnvError. 메시지: `SQL 연결 문자열이 없습니다. 비밀번호 없는 연결은 sql.connectionString, 비밀번호가 있는 연결은 환경 변수 PELLY_SQL_CONNECTION으로 지정하세요`
    * Database 없음 → EnvError. 메시지: `연결 문자열에 Database(Initial Catalog)가 없습니다`
    * 허용 목록 거부 → SafetyRejected. 메시지는 `SqlTargetPolicy.Message`, FailureKind는 `SqlTargetNotAllowed`.
    * `ProbeAsync` 실패 → EnvError. 메시지: `SQL 서버에 연결할 수 없습니다 ({DataSource}/{Database}): {예외 메시지}. pelly doctor로 확인하세요`
    * `CanViewDefinition == false` → EnvError, FailureKind `SqlPermission`. 메시지: `검증 계정에 VIEW DEFINITION 권한이 없어 객체 존재 검사를 할 수 없습니다. DBA에게 GRANT VIEW DEFINITION을 요청하세요 (설계 §8.10)`
    * 참고: 기존 `PreflightPhase`가 모델 서버 상태를 이미 확인하지만, 그것은 모델 생성 요청이 아니다.
  * `SqlLocatePhase(IUserInteraction ui, SqlOptions options) : IPhase`
    * 입력 검사: `--line`과 `--object`를 함께 쓰면 EnvError `--line은 --object와 함께 쓸 수 없습니다`.
    * 파일 해석: `SqlBatchSplitter.Split`. LexErrors가 있으면 EnvError `SQL 파일을 해석할 수 없습니다: {첫 오류}`.
    * 대상 선택(종료 3 메시지는 그대로 쓴다):
      * `--object`: `SameAs`로 찾는다.
        * 0개 → NotFound `대상 객체 '{x}'을(를) 찾을 수 없습니다: {파일}`
      * `--line`: 그 줄을 포함한 배치가 객체 배치여야 한다.
        * 아니면 NotFound `{n}번 줄은 프로시저·함수·뷰 정의 안이 아닙니다. 정의 안에 커서를 두고 실행하세요`
      * 지정 없음:
        * 객체 배치 1개 → 그것
        * 0개 → NotFound `파일에 프로시저·함수·뷰 정의가 없습니다`. 파싱 오류 배치가 있으면 ` (구문 오류가 있는 배치: 줄 a-b)`를 붙인다.
        * 2개 이상 → 대화형이면 선택 UI, 아니면 Ambiguous(R8) `객체가 {n}개입니다. --object로 지정하세요: dbo.a, dbo.b`
    * 원본 금지 구문(R2): `ForbiddenIn`이 비어 있지 않으면 SafetyRejected, FailureKind `SqlForbiddenInOriginal`. 메시지: `대상 객체에 이미 금지 구문({목록})이 있어 pelly로 편집할 수 없습니다 (PRD FR-07)`
    * 성공하면 다음을 설정한다.
      * `ctx.Target = new EditTarget(EditKind.Sql, path, name.Display, new(StartLine, EndLine), null)`
      * `ctx.ProjectStyle = "Sql"`
      * `ctx.Sql = new SqlRunState(…)`
  * `FsmOrchestrator.Describe` 문구:
    * SQL LOCATE: `대상 찾기: {ObjectName}` / `{line}번 줄` / `파일에서`
    * SQL VERIFY: `객체 존재·컴파일 검사`

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // TransitionsTests
  [Fact] PreflightSafetyRejected_Exits5()  [Fact] LocateSafetyRejected_Exits5()
  // SqlLocateFlowTests — 오케스트레이터 전체를 CreateSql로 만들 수 없으므로(Task 10) 단계 단위로 ExecuteAsync 호출,
  //   MockFileSystem + FakeSqlExecutor + ScriptedLlmBackend(호출 수 확인)
  [Fact] NoConnection_Exit4()                                  // 단계 결과 EnvError, 메시지 일치
  [Fact] HostNotAllowed_Exit5_NoLlmCall()                      // SafetyRejected, 백엔드 생성 호출 0
  [Fact] NoViewDefinition_IsEnvError_BeforeAnyLlmCall()
  [Fact] ProbeThrows_IsEnvError_WithMaskedConnection()
  [Fact] SingleObject_IsTarget_SpanMatchesBatch()
  [Fact] Object_SelectsAmongMany()                             // Settlement_All, "usp_SettleMonthly"
  [Fact] Line_InsideSecondObject_SelectsIt()
  [Fact] Line_OnGrantBatch_IsNotFound_WithCursorHint()
  [Fact] ManyObjects_NonInteractive_IsAmbiguous_ListsNames()
  [Fact] ManyObjects_Interactive_UsesSelection()
  [Fact] ForbiddenInOriginal_Exits5_WithList()
  [Fact] LineWithObject_IsEnvError()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Fsm*"`
- [ ] **Step 5: Commit** — `git commit -m "feat(fsm): add SQL preflight and locate phases"`

---

### Task 8: SQL 문맥·프롬프트

**Files:**
- Create: `src/PellyCoder.Core/Fsm/Phases/SqlSlicePhase.cs`, `src/PellyCoder.Core/Llm/Resources/prompts/system.sql.txt`, `fewshot.sql.txt`
- Modify: `src/PellyCoder.Core/Llm/PromptBuilder.cs`, `PromptResources.cs`, `src/PellyCoder.Core/Fsm/Phases/EditPhase.cs`, `src/PellyCoder.Core/Logging/RunLogger.cs`
- Test: `tests/PellyCoder.Core.Tests/Llm/PromptBuilderSqlTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/SqlSliceFlowTests.cs`, `tests/PellyCoder.Core.Tests/Logging/RunLoggerTests.cs`

**Interfaces:**
- Consumes: `SqlSchemaSlicer` (Task 6), `SqlReferenceCollector` (Task 4), `RunContext.Sql` (Task 7)
- Produces:
  * `PromptResources`에 `SystemSql`, `FewShotSql`, `SqlVersion`(`"sql-v1"`)을 추가하고, `string VersionFor(EditKind kind)`를 둔다.
    * `system.sql.txt`와 `fewshot.sql.txt`의 버전이 다르면 `LoadDefault`가 예외를 던진다(C#과 같은 규칙).
  * `PromptBuilder.BuildSql(EditTarget target, SqlObjectKind kind, Slice slice, string request, Feedback? feedback) → PromptBuildResult`
    * TARGET 줄: `{Display} ({PROCEDURE|FUNCTION|VIEW}) — file: {파일 이름}`
    * CONTEXT 순서: `[READ-ONLY: schema]` → `[EDITABLE: target]`
    * 예산 검사는 `BuildCSharp`와 같다.
  * `EditPhase`는 `ctx.Target.Kind == EditKind.Sql`이면 `BuildSql`을 쓴다.
  * `RunLogger.Summarize`의 `PromptVersion`은 `VersionFor(ctx.Target?.Kind ?? CSharp)`로 바꾼다.
  * `SqlSlicePhase(FileGuard fileGuard, ITokenCounter tokens, BudgetOptions budget, ISqlExecutor sql) : IPhase`
    1. 파일을 다시 읽어 외부 변경을 검사한다(`SlicePhase`와 같은 메시지·FailureKind).
    2. `Split`한 뒤 `TargetIndex` 배치를 `CurrentTarget`으로 둔다. 배치 수가 원본과 다르면 NotFound(`TargetNotFound`)이다. 정상 흐름에서는 일어나지 않고, 방어용이다.
    3. 그 배치가 파싱되지 않으면, 직전 시도가 쓴 텍스트가 깨진 것이다. APPLY가 막으므로 정상 흐름에서는 일어나지 않는다. 이때 NotFound로 처리한다.
    4. 참조를 수집하고, `SqlSchemaSlicer.BuildAsync`로 문맥을 만든다. 실행기 예외 `SqlUnavailableException`이 나면 EnvError이다.
    5. `TargetTooLarge`면 BudgetExceeded이다. 메시지: `대상 객체가 문맥 예산보다 큽니다 ({n} > budget.context {m} 토큰). 객체를 나누거나 예산을 늘리세요.`
  * `system.sql.txt`(`# version: 1`) 규칙 7개
    * 1·2·4·7번: `system.cs.txt`와 같은 뜻
    * 3번: SEARCH는 `[EDITABLE: target]`에서만 복사한다.
    * 5번: CREATE/ALTER 머리와 이름을 유지한다. `GO`·다른 문장·다른 객체를 더하지 않는다. FR-07 금지 구문을 열거하고 쓰지 않게 한다.
    * 6번: `Target: {LanguageHint}.` 그리고 `[READ-ONLY: schema]` 또는 대상에 이미 있는 테이블·컬럼만 쓴다.
  * `fewshot.sql.txt`: 작은 프로시저에 WHERE 조건을 하나 추가하는 예 하나(약 200토큰)

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] BuildSql_SectionsInOrder_NoLineNumbers()
  [Fact] SqlSystemAndFewShot_FitSystemBudget()          // tokens(SystemSql + "\n\n" + FewShotSql) <= 600
  [Fact] BuildSql_SystemPartIsStableAcrossRequests()    // 같은 compat이면 system 부분 문자열 동일 (S2 캐시)
  [Fact] Resources_SqlVersionsMustMatch()
  [Fact] Summary_PromptVersion_FollowsKind()            // Sql 실행 → "sql-v1", C# → "cs-v2"
  [Fact] SqlSlice_ExternalChange_IsEnvError()
  [Fact] SqlSlice_SecondAttempt_UsesEditedTargetBatch()  // 첫 시도로 쓴 배치 텍스트가 다음 프롬프트의 [EDITABLE: target]
  [Fact] SqlSlice_ExecutorUnavailable_IsEnvError()
  [Fact] EditPhase_SqlTarget_UsesSqlPrompt()            // ScriptedLlmBackend가 받은 프롬프트에 "[READ-ONLY: schema]"
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Llm*" --filter-namespace "*Fsm*" --filter-namespace "*Logging*"`
- [ ] **Step 5: Commit** — `git commit -m "feat(sql): add SQL slice phase and SQL prompt resources (sql-v1)"`

---

### Task 9: SQL 적용 — 가드·쓰기

**Files:**
- Create: `src/PellyCoder.Core/Fsm/Phases/SqlApplyPhase.cs`, `PatchWriter.cs`
- Modify: `src/PellyCoder.Core/Fsm/Phases/ApplyPhase.cs`, `src/PellyCoder.Core/Feedback/FeedbackBuilder.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/SqlApplyFlowTests.cs`, `tests/PellyCoder.Core.Tests/Feedback/FeedbackBuilderTests.cs`; 기존 `ShapeGuardFlowTests`·`BuildVerificationFlowTests`·`FsmOrchestratorTests`는 그대로 통과해야 한다.

**Interfaces:**
- Consumes: `SqlSafetyGuard` (Task 5), `RunContext.Sql` (Task 7)
- Produces:
  * `static class PatchWriter { Task<PhaseOutcome> WriteAsync(RunContext ctx, FileGuard fileGuard, FeedbackBuilder feedback, PatchOutcome patch, CancellationToken ct); }`
    * 하는 일: 첫 쓰기 전 백업, 쓰기, 쓰기 상태 처리, `MatchStages` 갱신. 지금 `ApplyPhase`의 해당 부분을 그대로 옮긴다.
    * 메시지·FailureKind는 바꾸지 않는다.
  * `FeedbackBuilder.ForSqlSafety(string reason)` — 앞에 `SQL SAFETY REJECTED: `를 붙이고 예산에 맞게 자른다.
  * `SqlApplyPhase(FileGuard fileGuard, FeedbackBuilder feedback, IUserInteraction ui) : IPhase`
    1. 패치를 적용한다. 실패하면 기존 `MatchFailed` 처리.
    2. `SqlSafetyGuard.Check(ctx.Sql.OriginalScript, ctx.Sql.TargetIndex, patch.NewText, ctx.Sql.CompatLevel)`
       * 파싱 오류: 진단 `Diagnostic(파일 이름, Line, Column, "error", $"SQL{Number}", Message)`. 피드백 `ForDiagnostics("SQL PARSE FAILED", …, patch.NewText)`. 오류 코드를 `ctx.ErrorCodes`에 더하고, FailureKind `SqlParseErrors`로 `MatchFailed`를 돌려준다(R5).
       * `!Ok`: 피드백 `ForSqlSafety(Reason)`, FailureKind `SqlSafetyRejected`, 결과 `SafetyRejected`.
    3. 드라이런이면 diff를 보이고 `DryRunCompleted`.
    4. 그 밖에는 `PatchWriter.WriteAsync`.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] ValidEdit_IsWritten_OtherBatchesByteIdentical()      // BOM·CRLF 원본에서 대상 외 배치 바이트 동일
  [Fact] GoInserted_IsSafetyRejected_NeverWritten()          // 파일 바이트 그대로, Feedback이 "SQL SAFETY REJECTED: the edit added"로 시작
  [Fact] ParseError_IsMatchFailed_WithSqlParseFeedback()     // FailureKind SqlParseErrors, ErrorCodes에 "SQL" 코드
  [Fact] EditOutsideTargetBatch_IsOutsideRegion()            // SEARCH가 SET 배치 → MatchFailed, OutsideRegion
  [Fact] DryRun_ShowsDiff_WritesNothing()
  [Fact] ForbiddenAdded_ThreeTimes_Exit5_Restored()          // 재시도 소진 → 종료 5, 원본 복원 (오케스트레이터를 단계 목록으로 직접 구성)
  [Fact] ForSqlSafety_FitsBudget()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests`
  * Expected: 기존 테스트 포함 전부 통과(`PatchWriter` 추출이 C# 동작을 바꾸지 않음)
- [ ] **Step 5: Commit** — `git commit -m "feat(sql): apply SQL patches through the safety guard; extract PatchWriter"`

---

### Task 10: SQL 검증 단계·팩토리

**Files:**
- Create: `src/PellyCoder.Core/Verification/SqlObjectStep.cs`, `SqlCompileStep.cs`
- Modify: `src/PellyCoder.Core/Fsm/FsmOrchestratorFactory.cs`
- Test: `tests/PellyCoder.Core.Tests/Verification/SqlStepsTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/SqlFlowTests.cs`

**Interfaces:**
- Consumes: `ISqlExecutor` (Task 3), `SqlReferenceCollector` (Task 4), Task 7~9의 단계, `BaselineDiff` (기존)
- Produces:
  * `SqlObjectStep(ISqlExecutor sql, FeedbackBuilder feedback) : IVerifierStep` (`Name = "SqlObjects"`)
    * 현재 대상 배치에서 참조를 모은다. `OriginalMissing`은 원본 대상 배치 기준으로 한 번만 조회해 캐시한다.
    * 새로 없는 이름은 `FindMissing(현재) − OriginalMissing`이다(이름 기준 `SameAs`).
    * 진단: `Diagnostic(파일 이름, 배치 시작 줄 + Line − 1, Column, "error", "Msg208", $"Invalid object name '{Display}'.")`
    * 피드백: `ForDiagnostics("SQL CHECK FAILED", …)`
    * `OriginalMissing`이 비어 있지 않으면 첫 VERIFY에서 한 번 알린다: `원본 객체가 이미 없는 객체({목록})를 참조합니다 — 새로 생긴 것만 검사합니다`
  * `SqlCompileStep(ISqlExecutor sql, FeedbackBuilder feedback) : IVerifierStep` (`Name = "SqlCompile"`)
    * 현재 대상 배치를 컴파일한다. `OriginalCompileErrors`는 원본 대상 배치를 한 번만 컴파일해 캐시한다.
    * 진단: `Diagnostic(파일 이름, 배치 시작 줄 + Line − 1, 0, "error", $"Msg{Number}", Message)`
    * 차감: `BaselineDiff.New`. 키는 파일·코드·메시지이고 줄은 무시한다.
    * 원본 오류가 있으면 한 번 알린다: `원본 객체에 이미 컴파일 오류가 {n}개 있습니다 — 새로 생긴 오류만 검사합니다`
    * 통과하면 한 번(`NoticesShown`) 아래 알림을 띄운다.
      * `HasDynamicSql`이면 `동적 SQL 안의 문장은 검증하지 않습니다 (설계 §8.10)`
      * `UsesTempTables`이면 `임시 테이블(#)의 컬럼은 검증하지 않습니다 (설계 §8.10)`
      * `SkippedMultiPart`가 있으면 `다른 데이터베이스·연결된 서버 객체({목록})는 존재를 검사하지 않습니다`
  * 두 단계 공통: `SqlUnavailableException`이 나면 `StepResult.Environment(메시지)`.
  * 실패 라벨은 `{Name}Errors`이다(`SqlObjectsErrors`, `SqlCompileErrors`).
  * `FsmOrchestratorFactory.CreateSql(IFileSystem fs, ILlmBackend backend, IUserInteraction ui, PellyOptions options, TimeProvider time, ISqlExecutor sql) → FsmOrchestrator`
    * 단계 구성:
      1. `SqlPreflightPhase(new PreflightPhase(...), sql, options.Sql)`
      2. `SqlLocatePhase`
      3. `SqlSlicePhase`
      4. `EditPhase`
      5. `SqlApplyPhase`
      6. `VerifyPhase([SqlObjectStep, SqlCompileStep])`
      7. `ReviewPhase`

- [ ] **Step 1: 실패하는 테스트 작성** (`FakeSqlExecutor` + `ScriptedLlmBackend` + `MockFileSystem`, 코퍼스 파일 내용)
  ```csharp
  [Fact] Success_FirstAttempt_Exit0_FileChanged()
  [Fact] MissingColumn_FeedbackHasMsg207_RetrySucceeds()     // Fake Compile: 1번째 수정본 텍스트 → 207, 2번째 → 없음; 2번째 프롬프트에 "SQL CHECK FAILED"·"Msg207"
  [Fact] NewMissingTable_Is208()                             // 수정본이 dbo.NoSuch를 참조 → Msg208, 줄 번호 = 파일 줄
  [Fact] OriginalAlreadyMissingTable_IsNotCounted()          // 원본이 참조하는 dbo.Gone이 없음 → 통과 + 알림 1회
  [Fact] OriginalCompileError_IsNotCounted()
  [Fact] RetriesExhausted_Exit1_Restored()
  [Fact] ExecutorDiesDuringVerify_Exit4_Restored()
  [Fact] DynamicSqlNotice_ShownOnce_AcrossRetries()
  [Fact] Cancel_DuringEdit_Exit2_FileUntouched()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Verification*" --filter-namespace "*Fsm*"`
- [ ] **Step 5: Commit** — `git commit -m "feat(sql): verify object existence and NOEXEC compile against the baseline; wire CreateSql"`

---

### Task 11: CLI `pelly sql`·doctor SQL 점검

**Files:**
- Create: `src/PellyCoder.Cli/Commands/SqlCommand.cs`
- Modify: `src/PellyCoder.Cli/CliApp.cs`, `Composition.cs`, `Runners.cs`, `src/PellyCoder.Core/Doctor/DoctorService.cs`
- Test: `tests/PellyCoder.Cli.Tests/SqlCommandTests.cs`, `tests/PellyCoder.Core.Tests/Doctor/DoctorServiceTests.cs`

**Interfaces:**
- Consumes: `FsmOrchestratorFactory.CreateSql` (Task 10), `SqlServerExecutor` (Task 3), `SqlConnectionText`·`SqlTargetPolicy` (Task 2)
- Produces:
  * `pelly sql <FILE> [REQUEST]` 옵션
    * 대상 지정: `--object <NAME>`, `--line <N>`
    * 실행 방식: `--yes`, `--dry-run`, `--max-retries <N>`
    * 설정 덮어쓰기: `--config <PATH>`, `--think <MODE>`, `--model <TAG>`, `--endpoint <URL>`
  * 검증 문구(종료 4)
    * `--line`·`--max-retries` 범위 검사는 `CsCommand`와 같다.
    * `--line`과 `--object`를 함께 쓰면 `--line은 --object와 함께 쓸 수 없습니다`.
    * 요청문 대화형 입력·비대화형 오류는 `CsCommand`와 같다.
  * `RunRequest(file, null, null, line, request, yes, dryRun, maxRetries, ObjectName: obj, Kind: EditKind.Sql)`
  * `CliApp`: `config.AddCommand<SqlCommand>("sql").WithDescription("SQL 저장 프로시저·함수·뷰 하나를 요청대로 수정합니다.")`
  * `IRunner.RunAsync`는 `request.Kind`에 따라 나눈다. SQL이면 실행마다 `new SqlServerExecutor(options.Sql.ConnectionString)`로 `CreateSql`을 부른다.
  * `DoctorService` 생성자에 마지막 선택 인자 `ISqlExecutor? sql = null`을 추가한다. SQL 점검 행 `SQL 연결` 하나:
    * 연결 문자열 없음 → `Warn` `pelly sql을 쓰려면 sql.connectionString 또는 PELLY_SQL_CONNECTION을 설정하세요`
    * 허용 목록 거부 → `Fail`. 메시지는 정책 메시지.
    * Probe 실패 → `Fail`. 연결 문자열은 가린다.
    * `CanViewDefinition` false → `Fail` `VIEW DEFINITION 권한 필요`
    * 정상 → `Ok` `{DataSource}/{Database}, {Version 첫 줄}`
  * `DoctorRunner`는 연결 문자열이 있을 때 `SqlServerExecutor`를 넘긴다.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // SqlCommandTests (기존 CsCommandTests 방식: 가짜 IRunner가 받은 RunRequest 단언)
  [Fact] Sql_PassesKindObjectAndLine()  [Fact] LineWithObject_Exit4()  [Fact] MissingRequest_NonInteractive_Exit4()
  [Fact] UnknownFile_Exit4()  [Fact] Help_ListsSqlCommand()
  // DoctorServiceTests (FakeSqlExecutor)
  [Fact] Sql_NoConnection_Warn()  [Fact] Sql_NotAllowed_Fail()  [Fact] Sql_ProbeFails_Fail_Masked()
  [Fact] Sql_NoViewDefinition_Fail()  [Fact] Sql_Ok_ShowsTarget()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Cli.Tests && dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Doctor*"`
- [ ] **Step 5: Commit** — `git commit -m "feat(cli): add pelly sql command and SQL doctor check"`

---

### Task 12: SQL 통합·실제 모델 E2E

**Files:**
- Create: `tests/PellyCoder.Integration.Tests/E2E/CannedLlmBackend.cs`, `SqlOfflineEndToEndTests.cs`, `SqlEndToEndTests.cs`, `tests/PellyCoder.Integration.Tests/Infrastructure/OllamaSqlFactAttribute.cs`, `docs/m2b-e2e.md`
- Modify: `tests/PellyCoder.Integration.Tests/Infrastructure/EnvFactAttributes.cs` (필요하면)

**Interfaces:**
- Consumes: `FsmOrchestratorFactory.CreateSql`, `SqlServerExecutor`, `CorruptingBackend`(M2a), `RecordingUserInteraction`(M2a), `CorpusCopy`(M2a)
- Produces:
  * `CannedLlmBackend(IReadOnlyList<string> responses)` — n번째 생성 요청에 n번째 텍스트를 NDJSON 청크로 흘려보낸다. 상태 조회는 항상 "도달 가능·모델 설치됨"이다.
  * `[OllamaSqlFact]` — Ollama와 SQL 환경이 둘 다 있어야 실행된다. Trait은 `Env=Ollama`이므로 CI에서 제외된다.

- [ ] **Step 1: 오프라인 통합 테스트 작성** (`[SqlServerFact]` → CI `sql-integration`에서 실행)
  * 공통 조건: 코퍼스 `Sql` 폴더 임시 복사, 실제 DB, 미리 정한 응답 사용
  * 모든 테스트 끝에 DB 객체 정의가 그대로인지 단언한다.
  ```csharp
  [SqlServerFact] CancelOrder_ValidEdit_Exit0()                       // sql-001 요청에 맞는 정답 블록 → 종료 0, 파일에 "@Status = 2"
  [SqlServerFact] MissingColumn_ThenFix_Exit0_ErrorCodesHas207()
  [SqlServerFact] MissingTable_IsCaughtBy208_NotByCompile()           // S5 ⓒ: NOEXEC가 못 잡는 것을 존재 검사가 잡음
  [SqlServerFact] SettlementAll_EditsOnlyTargetBatch_GrantsUntouched() // --object usp_SettleMonthly, 다른 배치 바이트 동일
  [SqlServerFact] ForbiddenEveryAttempt_Exit5_Restored()
  [SqlServerFact] HostNotAllowed_Exit5_NoGenerateCall()
  [SqlServerFact] QuotedIdentifierOffFile_CompilesWithOff()          // 파일 앞 SET QUOTED_IDENTIFIER OFF 반영
  ```
- [ ] **Step 2: 실제 모델 E2E 작성** (`[OllamaSqlFact]`, 로컬 전용)
  ```csharp
  [OllamaSqlFact] Sql001_RealModel_Exit0()                            // usp_CancelOrder, sql-001 요청
  [OllamaSqlFact] FirstAttemptMissingColumn_FeedbackFixes()           // CorruptingBackend: 1번째 응답 REPLACE에 존재하지 않는 컬럼 "o.NoSuchColumn" 삽입 → Msg207 → 2번째 프롬프트 "SQL CHECK FAILED" → 종료 0
  [OllamaSqlFact] MultiObjectFile_ObjectOption_Exit0()                // sql-004
  [OllamaSqlFact] CliRun_DryRun_ShowsDiff()                           // 실제 pelly sql 프로세스 실행 1건(--dry-run), 종료 0
  ```
- [ ] **Step 3: 실행**
  * 오프라인 테스트: 로컬 SQL 컨테이너에서 실행하고, push 후 `sql-integration`에서 Skip 없이 통과해야 한다.
  * 실제 모델 E2E: `PELLY_TEST_OLLAMA=http://localhost:11434 PELLY_TEST_MODEL=qwen3.6:35b-a3b-coding-nvfp4 PELLY_TEST_SQL=… dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*E2E*" --fail-skips on`을 3회 돌린다.
  * 실패 시 규칙은 M2a Task 14와 같다.
    * 프롬프트 문제면 리소스를 고치고 버전을 올린다(`sql-v2`).
    * Core 버그면 BLOCKED로 보고한다.
    * 기대값을 완화하지 않는다.
- [ ] **Step 4: 기록** — `docs/m2b-e2e.md`에 적는다.
  * 테스트별 3회 통과 수, 시도 수, think 사용
  * 단계별 시간(VERIFY의 컴파일 시간 포함)
  * CLI 실행 1건의 출력과 diff
  * 강제 재시도가 일어나지 않았으면 실패로 본다.
- [ ] **Step 5: Commit** — `git commit -m "test(sql): offline SQL integration against SQL Server and real-model SQL E2E"`

---

### Task 13: 벤치마크 실행기

**Files:**
- Create: `eval/PellyCoder.Eval/PellyCoder.Eval.csproj`, `Program.cs`, `BenchmarkTask.cs`, `TaskJudge.cs`, `BenchmarkRunner.cs`, `KpiReport.cs`, `EvalUserInteraction.cs`
- Create: `tests/PellyCoder.Eval.Tests/` (프로젝트 + 테스트)
- Modify: `PellyCoder.sln`(두 프로젝트 추가), `tests/PellyCoder.Integration.Tests/Benchmark/*`(정의 클래스를 `PellyCoder.Eval.BenchmarkTask`로 교체하고 테스트 쪽 사본 삭제), `.gitignore`(`eval/results/`)

**Interfaces:**
- Consumes: `FsmOrchestratorFactory.Create`/`CreateSql`, `PellyOptionsLoader`, `OllamaBackend`, `SystemProcessRunner`, `SqlServerExecutor`, `BuildToolResolver`·`BuildVerifier`, `MemberLocator`, `SqlBatchSplitter`, `RunSummary`
- Produces:
  * `sealed class BenchmarkTask`
    * 필드: README 스키마 그대로(`Id, Kind, File, Method, Line, Object, Request, Judge`)
    * `static BenchmarkTask Load(string path)` — YamlDotNet camelCase
  * `sealed record JudgeOutcome(bool Passed, IReadOnlyList<string> Reasons, string BuildCheck /* Passed|Failed|Skipped|NotRequired */, bool InvariantViolated)`
  * `static class TaskJudge { JudgeOutcome Judge(BenchmarkTask task, string originalTarget, string finalTarget, int exitCode, VerificationResult independent); }`
    * 판정 규칙: README 공통 규칙과 같다.
      * 종료 0이 아니면 실패.
      * 대상이 원본과 같으면 실패(`대상이 바뀌지 않음`).
      * `mustContain`·`mustAdd`·`mustNotContain`을 확인한다.
      * 독립 검증이 `Failed`면 실패.
    * `InvariantViolated`: 종료 0인데 독립 검증이 `Failed`인 경우. 이것이 KPI "검증 불변 조건"이다.
  * `sealed record VerificationResult(string Status /* Passed|Failed|Skipped */, string? Detail)`
  * 대상 텍스트 추출
    * cs: 원본과 결과 각각에서 `method` 이름 또는 `line`으로 `MemberLocator`가 찾은 멤버의 원문
    * sql: `object` 또는 유일한 객체 배치의 텍스트
  * 독립 검증(오케스트레이터와 별개로 다시 실행)
    * cs SDK: `BuildVerifier.BuildAsync`로 오류 0개이면 Passed.
    * cs Legacy: Windows가 아니면 `Skipped`("빌드 판정 생략", 설계 §10.6). Windows에서는 MSBuild로 판정한다(「Windows 미확인」 주석).
    * sql: `CompileWithNoExecAsync` 오류 0개이고 `FindMissingAsync`가 빈 목록이면 Passed.
    * `judge.tests`: SDK만 `dotnet test <테스트 csproj> --no-restore --filter "FullyQualifiedName=<이름>"`로 확인하고, 레거시는 `Skipped`.
  * `BenchmarkRunner`가 작업마다 하는 일:
    1. 작업 파일이 속한 코퍼스 최상위 폴더(`Modern`/`Legacy`/`Sql`)를 `<out>/work/<id>/`에 복사한다(`bin`, `obj`, `.pelly` 제외).
    2. Modern은 `dotnet restore`를 한다(시간 측정에서 제외).
    3. `RunRequest(--yes, 비대화형 EvalUserInteraction)`로 오케스트레이터를 실행하고 벽시계 시간을 잰다.
    4. `.pelly/runs/*/summary.json`과 `events.jsonl`을 읽는다.
    5. 독립 검증 후 판정한다.
  * 실행 명령: `dotnet run --project eval/PellyCoder.Eval -- [--tasks <id 접두사·글롭>] [--kind cs|sql] [--out <dir>] [--config <pelly.json>]`
    * 설정: `PellyOptionsLoader`(사용자 설정 + `--config` + 환경 변수)
    * SQL 연결: `PELLY_SQL_CONNECTION`. 없으면 sql 작업은 `Skipped`이고 보고서에 사유를 적는다.
    * 기본 출력 폴더: `eval/results/<yyyyMMdd-HHmmss>/`
  * 산출물
    * `results.json`: 작업별 id, kind, 종료 코드, 시도 수, 판정, 사유, 독립 검증, 실패 유형, 벽시계 초, TTFT, think 토큰
    * `report.md`: KPI 표(PRD §2.2 순서)
      * 검증 불변 조건: 종료 0 중 독립 검증 Failed 0건이면 100%
      * 패치 포맷 파싱 성공률: `events.jsonl`의 Edit 단계 결과 중 `FormatError`가 아닌 비율
      * 최종 작업 성공률: 전체·cs·sql·legacy 각각
      * 단일 작업 완료 시간: 중위값, P90(최근접 순위법)
      * TTFT: 중위값, P90
      * think 최대 토큰, 잘림 수
      * 실패 유형 분포
      * Legacy 빌드 판정 생략 수
  * 프로그램 종료 코드
    * 0: 실행 완료
    * 1: 검증 불변 조건 위반이 1건 이상
    * 4: 설정 오류 또는 LLM 서버에 연결 불가

- [ ] **Step 1: 실패하는 테스트 작성** (`tests/PellyCoder.Eval.Tests`, 프로세스·LLM 없이)
  ```csharp
  [Fact] Judge_UnchangedTarget_Fails()  [Fact] Judge_MustContainMissing_Fails()  [Fact] Judge_MustAdd_CountsOccurrences()
  [Fact] Judge_MustNotContainPresent_Fails()  [Fact] Judge_NonZeroExit_Fails()
  [Fact] Judge_Exit0ButIndependentFailed_IsInvariantViolation()
  [Fact] Judge_LegacySkippedBuild_StillPassesOtherwise()   // BuildCheck "Skipped", Passed true
  [Fact] Kpi_Percentiles_NearestRank()                     // [1..10] → 중위 5, P90 9
  [Fact] Kpi_ParseSuccess_FromEvents()                     // Edit Ok 9, FormatError 1 → 90%
  [Fact] Kpi_Report_ListsAllPrdRows()                      // report.md에 6개 KPI 행 이름
  [Fact] TargetText_CsByMethodAndLine_SqlByObject()        // 코퍼스 파일로
  [Fact] Copy_ExcludesBinObjPelly()
  [Fact] TaskFilter_PrefixAndKind()
  ```
  `Integration.Tests`에는 실행기 스모크 테스트를 추가한다.
  * `[Fact] Runner_CannedBackend_CsModern001_Passes()` — `CannedLlmBackend`로 정답 블록을 주고 실제 restore·빌드까지 한다.
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Eval.Tests && dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*Benchmark*"`
  * push 후 `unit` 잡 두 OS 모두 녹색이어야 한다.
- [ ] **Step 5: Commit** — `git commit -m "feat(eval): add benchmark runner with independent re-verification and KPI report"`

---

### Task 14: 벤치마크 정의 30건·첫 측정

**Files:**
- Create: `eval/benchmark/tasks/` 신규 20건, `docs/m2b-benchmark.md`
- Modify: `eval/benchmark/README.md`, `tests/PellyCoder.Integration.Tests/Benchmark/BenchmarkDefinitionTests.cs`

**Interfaces:**
- Consumes: `BenchmarkTask` (Task 13)

- [ ] **Step 1: 구성 테스트 갱신**
  * `Composition_Is5Cs5Sql_With3Legacy`를 `Composition_Is18Cs12Sql_With10Legacy`로 바꾼다.
  * 단언: cs 18, sql 12, legacy 10, `line` 지정 2건 이상, `object` 지정 2건 이상
  * 새 테스트 `[Fact] SqlTasks_TargetIsAnObjectBatch()` — `SqlBatchSplitter`로 대상 객체가 정확히 하나인지 확인한다.
- [ ] **Step 2: 작업 20건 작성** (R9). 구성은 설계 §10.6과 PRD §8.1을 따른다.

  | 범위 | 새 건수 | 내용 |
  | :--- | ---: | :--- |
  | cs-legacy-004~010 | 7 | MVC 컨트롤러·서비스 3(`OrderController`, Legacy `OrderService`), ADO.NET 배치 2(`SettlementBatch.Execute`·`BuildReport`), Dapper 리포지토리 2(`OrderDao`) |
  | cs-modern-003~008 | 6 | `OrderRepository` 2, `OrdersController` 2, `OrderService.SearchAsync` 1, `DailySettlementJob.Run` 1 |
  | sql-006~012 | 7 | SP 조건 추가 2, 컬럼 추가 조회 2(`usp_GetOrderDetail` 등), JOIN 변경 1, 뷰 1(`vw_ShippedOrders`), 여러 객체 파일의 `usp_SettleDaily` 1 |

  * 요청 종류: 로직 수정, null 처리, Dapper 쿼리 변경, 조건·컬럼·JOIN.
  * 시그니처를 바꾸는 요청은 넣지 않는다(R9). 단, SQL 매개변수 추가는 허용한다(sql-002처럼).
  * 요청문은 사용자가 입력하는 한국어 문장으로, 한 멤버·한 객체 안에서 끝나야 한다.
  * 판정 문자열은 대상 원문을 읽고 `Judges_RejectUnmodifiedTarget`를 만족하게 고른다.
- [ ] **Step 3: README 갱신**
  * 현재 구성 표를 30건으로 바꾼다.
  * 실행 방법(Task 13 명령)과 결과 폴더를 적는다.
  * `sql` 작업에는 `PELLY_SQL_CONNECTION`과 허용 목록 설정이 필요하다고 적는다.
- [ ] **Step 4: 정의 테스트 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*Benchmark*"`
- [ ] **Step 5: 첫 측정** — 로컬 Mac에서 실제 모델과 SQL 컨테이너로 30건을 1회 실행한다.
  * `docs/m2b-benchmark.md`에 적을 것:
    * KPI 표
    * 실패 작업별 원인 분류: 모델 / 정의 / pelly 결함
    * Legacy 빌드 판정 생략 사실
    * 환경: 모델 태그, Ollama 버전, 날짜
  * 검증 불변 조건 위반이 1건이라도 있으면 BLOCKED로 보고한다(제품 결함).
  * 성공률은 기준선 기록일 뿐 합격 조건이 아니다.
  * pelly 결함으로 보이는 실패는 고치지 않고 문서에 적어 보고한다(이 작업의 범위 밖).
- [ ] **Step 6: Commit** — `git commit -m "feat(eval): extend benchmark to 30 tasks and record first baseline run"`

---

### Task 15: 문서 갱신

**Files:**
- Modify: `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md`, `docs/PRD.md`, `README.md`, `deploy/`(없으면 만들지 않음)

- [ ] **Step 1: 설계 문서 반영** — 각 변경 옆에 근거(작업 번호·R 번호)를 적는다.
  * §5: `ISqlExecutor` 목적별 메서드(R1)
  * §7.1 전이표: `SafetyRejected` 두 행
  * §8.1: `sql` 설정 검증 문구, `PELLY_SQL_CONNECTION` 우선(R3), 허용 목록 빈 값(R4)
  * §8.4 SQL 줄: 메시지와 R8
  * §8.6: 줄이는 순서와 `Omitted` 종류
  * §8.10:
    * 가드 문구, 원본 금지 구문(R2), 파싱 오류 처리(R5)
    * 원본 대비 새 오류만 세는 규칙(누락 객체·컴파일), 3·4부 이름(R7), `VIEW DEFINITION` 없으면 종료 4
  * §8.12: FailureKind 추가 — `SqlTargetNotAllowed`, `SqlPermission`, `SqlForbiddenInOriginal`, `SqlParseErrors`, `SqlSafetyRejected`, `SqlObjectsErrors`, `SqlCompileErrors`
  * §8.12 비식별 정규식: `^(CS|MSB|NU|Msg|SQL)\d+$` (R6)
  * §8.13: SQL 행
  * §10.6: 실행기 사용법과 독립 재검증
- [ ] **Step 2: PRD 반영**
  * FR-00: `pelly sql` 옵션
  * FR-07: user-secrets 제외(R3), 허용 목록 빈 값(R4), 원본 금지 구문(R2)
  * §8.1: 30건(M2b)·50건(M3)
  * 변경 이력: v1.8
- [ ] **Step 3: README** — `pelly sql` 사용 예와 SQL 설정(허용 목록·`PELLY_SQL_CONNECTION`), 벤치마크 실행 방법
- [ ] **Step 4: 검증** — 문서만 바뀌었는지 `git diff --stat`으로 확인한다.
- [ ] **Step 5: Commit** — `git commit -m "docs: reflect M2b SQL path and benchmark runner"`

---

## M2b 완료 기준

* `pelly sql`이 다음 작업을 실제 모델로 끝낸다(Task 12, 강제 재시도 경로 포함).
  * SSMS 스크립트 파일의 프로시저 수정
  * 여러 객체 파일의 `--object` 수정
* 안전 가드·허용 목록·비밀번호 정책이 모델 호출 전, 또는 쓰기 전에 막는다(Review Focus 1~3).
* SQL 통합 테스트가 CI `sql-integration`에서 Skip 없이 통과한다. 레거시 빌드 테스트도 `windows-integration`에서 계속 통과한다.
* 벤치마크 30건이 실행기로 실행되고 KPI 표가 나온다(§11 M2 완료 기준). 검증 불변 조건 위반은 0건이다.
* CI 4잡 녹색, `dotnet test` 실패 0.

## M3로 넘기는 것

* 벤치마크 50건과 그 결과로 하는 프롬프트·샘플링 튜닝
* `report --sanitize`(SQL 코드 정규식 포함), `--pause-on-exit`, `--test`, DTO 슬라이스
* doctor 완성, publish·SBOM·체크섬, S6·S7, `deploy/` 문서
