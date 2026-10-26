# V2-1d 운영 정의 읽기·`pelly sql pull`·SQL 영향 탐색 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** pelly가 운영 DB를 읽기 전용으로 읽어 SP를 파일로 받고(`pelly sql pull`), 받은 뒤 운영이 바뀌었으면 수정을 멈추며(`pelly sql`·`pelly sql check`), 명세의 `sql` 작업에서 빠진 SQL 영향 대상을 경고한다.

**Architecture:**
- 운영 연결은 읽기 전용 인터페이스 `ISqlDefinitionReader`(정의·의존성·컬럼 사용 조회만)로만 쓴다. 컴파일 검증용 `ISqlExecutor`에는 운영 연결 문자열이 들어가지 않는다. 개발 DB도 같은 리더로 읽는다(pull `--from dev`, 개발 비교, 개발 기준 영향 탐색).
- 정의 텍스트 처리(정규화·해시·`CREATE`→`ALTER`·받은 기록 주석)는 순수 함수 `SqlDefinitionText`/`PullRecord`에 모은다. pull·운영 비교·check가 모두 이것을 쓴다.
- 운영 비교는 `SqlProdComparer`가 판정하고, `pelly sql` 파이프라인에는 LOCATE 뒤·SLICE 앞에 `SqlProdCheckPhase`로 넣는다. `pelly sql check`는 같은 판정을 종료 코드로 바꾼다.
- SQL 영향 탐색은 `SqlImpactFinder`(서버 의존성 ①·③ + C# 문자열 ②)이고, 결과는 V2-1c의 `ImpactTarget`·`ImpactCheck.Issues`·`Covers`로 경고가 된다.

**Tech Stack:** .NET 10, Microsoft.Data.SqlClient 7.1.1, Dapper, Microsoft.SqlServer.TransactSql.ScriptDom 170.191.0, Roslyn(`Microsoft.CodeAnalysis.CSharp`), Spectre.Console.Cli 0.57.2, System.IO.Abstractions, xunit v3. 새 패키지 없음.

**Spec:** `docs/superpowers/specs/2026-10-26-v2-1d-sql-impact-design.md` (이하 「설계」). 계획과 설계가 다르면 설계가 맞고, 계획을 고친다.

## Global Constraints

- **운영에는 읽기 조회만:** 운영 연결로 보내는 문장은 `SELECT`(카탈로그 뷰, `OBJECT_DEFINITION`, `OBJECT_ID`, `HAS_PERMS_BY_NAME`, `sys.dm_sql_referenced_entities`)뿐이다. `SET NOEXEC`, `BEGIN TRAN`, DDL·DML을 운영 연결로 보내는 코드 경로가 없어야 한다(설계 §2 실패 조건).
- **운영 연결 없는 사용자의 동작 불변:** `sql.production`이 없으면 `pelly sql`·`spec check`(`--impact` 없음)·`apply`의 출력·질문·종료 코드·DB 조회가 지금과 같다(설계 §2, §7.1).
- **보존:** v1·V2-1a~c·V2-2·V2-3의 기존 테스트는 바뀌지 않고 통과한다. 경고 체계는 V2-1c 것을 쓴다(오류·종료 4를 영향 탐색이 새로 만들지 않는다).
- **설정 키(설계 §4·§5):** `sql.production.allowedHosts`, `sql.production.allowedDatabases`, `sql.production.connectionString`, `sql.pullDirectory`(기본 `db/pulled`). 비밀번호가 있는 운영 연결은 환경 변수 `PELLY_SQL_PROD_CONNECTION`으로만.
- **사용자 문구는 한국어**이며 설계 §5·§6·§7.5의 문구를 그대로 쓴다. 객체 이름 표시는 `SqlObjectName.Display`에 스키마가 없으면 `dbo.`를 붙인 꼴(`dbo.usp_X`)이다.
- **종료 코드:** v1 그대로(0 성공, 1 검증 실패, 2 취소·거부, 3 대상 특정 실패, 4 환경·입력 오류, 5 안전장치 거부). 새 실패 종류 `SqlProdChanged`(종료 4).
- **게이트:** `dotnet build -warnaserror` 경고 0, `dotnet test` 실패 0(건너뜀은 SQL 서버·로컬 모델·Windows 전용만).
- **작업 위치:** 워크트리 `/Users/payletter/git-root/Pelly-Code-v21d`(브랜치 `feat/v2-1d-sql-impact`). 메인 체크아웃에서 빌드·테스트하지 않는다. push·병합은 사람이 고른 뒤에만.
- **SQL 통합 테스트:** `[SqlServerFact]`(트레이트 `Env=SqlServer`), 연결은 `PELLY_TEST_SQL`. 운영 역할 DB는 같은 서버의 `PellyCorpusProd`(Task 3에서 CI 배포에 추가). 로컬에서 돌리려면 `eval/corpus/Sql/deploy.sh <컨테이너> <sa 비밀번호> PellyCorpusProd`를 먼저 실행한다.

## Review Focus

1. **SSMS가 다시 저장한 파일:** 받은 파일을 SSMS에서 열고 저장해 줄바꿈이 CRLF로 바뀌고 줄 끝 공백이 생겨도 운영과 "같음"이어야 한다 → Task 2 `Normalize_ignores_crlf_and_trailing_spaces`, Task 6 `Crlf_resaved_file_is_same_as_prod`.
2. **주석 안의 `CREATE`:** 정의 앞 주석(`/* CREATE: 2020-01 홍길동 */`)이 있는 SP를 받으면 주석은 그대로, 실제 `CREATE PROCEDURE`만 `ALTER`로 바뀌어야 한다 → Task 2 `ToAlter_skips_create_inside_comment`.
3. **받은 기록이 다른 객체의 것:** 여러 객체가 든 파일에서 받은 기록의 `object=`가 대상과 다르면 그 기록을 믿지 않아야 한다 → Task 6 `Record_for_another_object_is_invalid`.
4. **명세의 이름 표기 차이:** 작업 `#[dbo].[USP_Cancel]`이 서버의 `dbo.usp_Cancel` 대상을 덮어야 한다(대소문자·대괄호·스키마 생략) → Task 12 `Covers_sql_object_ignores_case_brackets_and_default_schema`.
5. **암호화 객체와 권한 없음:** `definition`이 NULL인 객체를 받을 때, `VIEW DEFINITION`이 있으면 "암호화된 객체", 없으면 권한 안내로 갈라야 한다 → Task 5 `Encrypted_object_fails_with_encrypted_message`, `Null_definition_without_permission_says_permission`.

---

### Task 1: 운영 연결 설정과 허용 목록

**Files:**
- Modify: `src/PellyCoder.Core/Config/PellyOptions.cs`(`SqlOptions`에 `Production`, `PullDirectory`), `src/PellyCoder.Core/Config/PellyOptionsLoader.cs`, `src/PellyCoder.Core/Sql/SqlConnectionText.cs`(`SqlTargetPolicy`)
- Test: `tests/PellyCoder.Core.Tests/Config/PellyOptionsLoaderTests.cs`, `tests/PellyCoder.Core.Tests/Sql/SqlConnectionTextTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class SqlProductionOptions { List<string> AllowedHosts; List<string> AllowedDatabases; string? ConnectionString; }
  // SqlOptions: public SqlProductionOptions Production { get; set; } = new(); public string PullDirectory { get; set; } = "db/pulled";
  public static SqlTargetCheck SqlTargetPolicy.CheckProduction(SqlEndpoint endpoint, SqlProductionOptions options);
  public static bool SqlTargetPolicy.SameTarget(SqlEndpoint a, SqlEndpoint b); // DataSource/Host와 Database가 모두 같으면 true (대소문자 무시)
  ```

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `PellyOptionsLoaderTests.Production_section_binds`: `{"sql":{"production":{"allowedHosts":["p1"],"allowedDatabases":["Shop"],"connectionString":"Server=p1;Database=Shop;Integrated Security=true"}}}` → `options.Sql.Production.AllowedHosts == ["p1"]`, 오류 없음, "unknown config key" 없음.
  - `Production_password_in_file_is_error`: 운영 `connectionString`에 `Password=x` → 오류 문구에 `PELLY_SQL_PROD_CONNECTION` 포함.
  - `Production_env_overrides_file`: 환경 `PELLY_SQL_PROD_CONNECTION=Server=p2;Database=Shop;User Id=u;Password=x` → `Production.ConnectionString`이 그 값, 오류 없음.
  - `Production_same_as_dev_is_error`: 개발·운영 연결이 같은 서버·DB → 오류 `sql.production이 개발 연결(sql.connectionString)과 같은 서버·DB를 가리킵니다`.
  - `PullDirectory_defaults_to_db_pulled`: 설정 없음 → `"db/pulled"`.
  - `SqlConnectionTextTests.CheckProduction_names_production_keys`: 허용 목록 밖 호스트 → 메시지에 `sql.production.allowedHosts`.
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~PellyOptionsLoaderTests|FullyQualifiedName~SqlConnectionTextTests"` → 새 테스트 FAIL(컴파일 오류).
- [ ] **Step 3: 구현한다**
  - `BuildKnownKeys`는 `SqlOptions`의 속성만 키로 만든다. `sql:Production:AllowedHosts`·`sql:Production:AllowedDatabases`·`sql:Production:ConnectionString`을 직접 더하고, 앞의 둘을 `IsOpenEnded`에 더한다.
  - `Bind`에서 `options.Sql.Production.AllowedHosts/AllowedDatabases`도 `Clear()` 후 바인딩한다(기존 목록과 같은 이유).
  - 운영 비밀번호 검사·환경 변수 덮어쓰기는 개발과 같은 자리에 같은 방식으로 둔다. 같은 대상 검사는 둘 다 설정되어 있을 때만 하고 `SqlConnectionText.Endpoint`로 비교한다.
  - `CheckProduction`은 기존 `Check`와 같은 규칙에 메시지 키만 `sql.production.allowedHosts`/`sql.production.allowedDatabases`로 바꾼다. 공통부는 private 메서드로 뺀다.
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS, 기존 테스트도 PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(sql): production connection settings, allow list and pull directory"`

---

### Task 2: 정의 텍스트 — 정규화·해시·ALTER 변환·받은 기록

**Files:**
- Create: `src/PellyCoder.Core/Sql/SqlDefinitionText.cs`, `src/PellyCoder.Core/Sql/PullRecord.cs`
- Test: `tests/PellyCoder.Core.Tests/Sql/SqlDefinitionTextTests.cs`, `tests/PellyCoder.Core.Tests/Sql/PullRecordTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class SqlDefinitionText
  {
      public static string Normalize(string definition, int compatLevel);   // 설계 §6.1
      public static string Hash(string definition, int compatLevel);        // SHA-256(Normalize(..)) 소문자 16진수
      public static string ToAlter(string definition, int compatLevel);     // 첫 CREATE 토큰만 ALTER, CREATE OR ALTER는 그대로
      public static string RenderPullFile(SqlModuleDefinition module, PullRecord record, string? devDiffersHash, int compatLevel); // 설계 §5 파일 모양, 줄바꿈은 정의 원문 따름
  }
  public sealed record PullRecord(string Source, string Server, string Database, SqlObjectName Object, string Sha256, DateTimeOffset At)
  {
      public string Format();                                   // "-- pelly pull: source=prod server=… database=… object=dbo.usp_X sha256=… at=2026-10-26T09:12:33Z"
      public static PullRecord? TryParse(string fileTextLf);    // 파일 첫 줄만 본다; 형식이 틀리면 null
  }
  ```
  `SqlModuleDefinition`은 Task 3에서 정의한다(이 작업에서 먼저 만들어도 된다: `public sealed record SqlModuleDefinition(SqlObjectName Name, string TypeCode, string? Definition, bool UsesAnsiNulls, bool UsesQuotedIdentifier);` in `src/PellyCoder.Core/Abstractions/ISqlDefinitionReader.cs`).

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Normalize_ignores_crlf_and_trailing_spaces`: `"CREATE PROCEDURE p AS\r\nSELECT 1   \r\n"`과 `"\n\nALTER PROCEDURE p AS\nSELECT 1\n"`의 `Hash`가 같다.
  - `Normalize_keeps_comment_and_case_changes`: 주석 한 글자만 다르면 해시가 다르다. `select`/`SELECT`도 다르다.
  - `Normalize_unifies_create_or_alter`: `CREATE OR ALTER PROCEDURE p AS SELECT 1`과 `CREATE PROCEDURE p AS SELECT 1`의 해시가 같다.
  - `ToAlter_replaces_first_create_only`: `"CREATE PROCEDURE p AS\nBEGIN\n  CREATE TABLE #t(a int)\nEND"` → 첫 줄만 `ALTER PROCEDURE p AS`, `#t`의 CREATE는 그대로.
  - `ToAlter_skips_create_inside_comment`: `"/* CREATE: 2020-01 */\n-- CREATE note\nCREATE PROCEDURE p AS SELECT 1"` → 주석 두 줄 그대로, 셋째 줄만 `ALTER`.
  - `ToAlter_keeps_create_or_alter`: 입력 그대로.
  - `RenderPullFile_matches_ssms_shape`: ANSI_NULLS off·QUOTED_IDENTIFIER on인 모듈 → 줄 순서가 `-- pelly pull: …`, `SET ANSI_NULLS OFF`, `GO`, `SET QUOTED_IDENTIFIER ON`, `GO`, `ALTER …`, `GO`. 원문이 CRLF면 모든 줄이 CRLF.
  - `RenderPullFile_adds_dev_differs_line`: `devDiffersHash="abc"` → 둘째 줄이 `-- pelly pull: dev differs (sha256=abc)`.
  - `PullRecordTests.Format_then_TryParse_roundtrips`, `TryParse_returns_null_for_ssms_header`(첫 줄이 `/****** Object: …`), `TryParse_reads_bracketed_object`(`object=[dbo].[usp X]`).
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~SqlDefinitionTextTests|FullyQualifiedName~PullRecordTests"` → FAIL.
- [ ] **Step 3: 구현한다** — 키워드 위치는 `SqlParsers.Create(compat, true).GetTokenStream`의 토큰으로 찾는다(`TSqlTokenType.Create`, 그 뒤 공백·주석을 건너뛴 `Or`+`Alter`면 CREATE OR ALTER). 정규화의 키워드 통일은 그 토큰 범위를 `CREATE`로 바꾼다. 받은 기록의 `object=`는 공백이 있는 이름을 위해 항상 `[스키마].[이름]` 꼴로 쓴다(`]`는 `]]`).
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(sql): definition normalization, hashing, CREATE→ALTER and the pull record"`

---

### Task 3: 읽기 전용 정의 리더와 운영 역할 테스트 DB

**Files:**
- Create: `src/PellyCoder.Core/Abstractions/ISqlDefinitionReader.cs`, `src/PellyCoder.Core/Sql/SqlDefinitionReader.cs`, `src/PellyCoder.Core/Sql/SqlReaders.cs`, `tests/PellyCoder.Integration.Tests/Sql/SqlDefinitionReaderTests.cs`, `tests/PellyCoder.Integration.Tests/Infrastructure/ProdDb.cs`
- Modify: `src/PellyCoder.Core/Sql/SqlServerExecutor.cs`(연결 실행·오류 변환을 공유 헬퍼로 뺌), `.github/workflows/ci.yml`(sql-integration에 `PellyCorpusProd` 배포 단계)
- Test: `tests/PellyCoder.Core.Tests/Sql/SqlReadersTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface ISqlDefinitionReader
  {
      Task<SqlServerProbe> ProbeAsync(CancellationToken ct);
      Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct);              // 객체 없으면 null; TypeCode는 sys.objects.type을 RTRIM한 값
      Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct); // sys.sql_expression_dependencies, 같은 DB, 자신 제외
      Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct);     // DMF가 실패(컴파일 안 됨)하면 null
      Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct);                // 컬럼 이름, column_id 순
  }
  public sealed record SqlModuleRef(SqlObjectName Name, string TypeCode);
  public sealed record SqlColumnUse(SqlObjectName Table, string? Column, bool Selected, bool Updated, bool SelectAll);
  public sealed class SqlDefinitionReader(string connectionString) : ISqlDefinitionReader;
  public sealed record SqlReaderOpen(ISqlDefinitionReader? Reader, SqlEndpoint? Endpoint, string? Problem); // Problem: 사용자 문구
  public static class SqlReaders
  {
      public static SqlReaderOpen Production(SqlOptions o, Func<string, ISqlDefinitionReader> factory); // 미설정이면 (null, null, null)
      public static SqlReaderOpen Development(SqlOptions o, Func<string, ISqlDefinitionReader> factory);
  }
  ```
  연결 실패는 기존과 같이 `SqlUnavailableException`, 취소는 `OperationCanceledException`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `SqlReadersTests.Production_not_configured_is_all_null`, `Production_outside_allow_list_has_problem`(`sql.production.allowedHosts` 문구), `Production_without_database_has_problem`.
  - 통합(`[SqlServerFact]`, `ProdDb.ConnectionString` = `PELLY_TEST_SQL`의 Database를 `PellyCorpusProd`로 바꾼 것):
    - `GetDefinition_returns_text_and_settings`: `dbo.usp_CancelOrder` → `TypeCode=="P"`, `Definition`에 `CREATE PROCEDURE`, `UsesAnsiNulls`·`UsesQuotedIdentifier`가 카탈로그 값과 같다.
    - `GetDefinition_missing_object_is_null`.
    - `GetReferencingModules_finds_callers`: 코퍼스에서 `dbo.Orders`를 참조하는 모듈에 `dbo.usp_CancelOrder`가 있다.
    - `GetColumnUses_reports_selected_columns`: `dbo.usp_CancelOrder` → `dbo.Orders.Status`의 `Selected==true`.
    - `GetColumnUses_of_broken_module_is_null`: 테스트에서 운영 역할 DB에 존재하지 않는 컬럼을 참조하는 프로시저를 만든 뒤(지연 이름 확인이 안 되는 경우는 열을 지운 테이블로 만든다) → `null`. 테스트 끝에 지운다.
    - `Reader_works_with_view_definition_only_login`: 테스트가 sa로 `VIEW DEFINITION`만 가진 로그인·사용자를 운영 역할 DB에 만들고, 그 연결로 리더의 다섯 메서드를 모두 호출해 성공한다(쓰기 권한이 필요한 문장이 섞이면 실패). 끝에 로그인을 지운다.
- [ ] **Step 2: 실행해 실패를 확인한다** — 단위: `dotnet test tests/PellyCoder.Core.Tests --filter FullyQualifiedName~SqlReadersTests` → FAIL. 통합: `PELLY_TEST_SQL=… dotnet test tests/PellyCoder.Integration.Tests --filter FullyQualifiedName~SqlDefinitionReaderTests` → FAIL.
- [ ] **Step 3: 구현한다**
  - `SqlServerExecutor.RunAsync`의 연결 열기·`SqlException`→`SqlUnavailableException`·취소 변환을 `internal static class SqlCalls { Task<T> RunAsync<T>(string? cs, Func<SqlConnection, Task<T>> body, CancellationToken ct) }`로 옮기고 두 클래스가 같이 쓴다. 기존 `SqlServerExecutor` 테스트가 그대로 통과해야 한다.
  - `GetColumnUsesAsync`는 `sys.dm_sql_referenced_entities(@name,'OBJECT')`의 `referenced_minor_name`·`is_selected`·`is_updated`·`is_select_all`을 읽고, 기존 `GetReferencedEntitiesAsync`처럼 연결이 살아 있는 `SqlException`이면 `null`을 돌려준다.
  - `SqlReaders.*`는 `SqlConnectionText.Endpoint` + `SqlTargetPolicy.Check`/`CheckProduction`을 거친다. 리더를 만들기만 하고 접속하지 않는다.
  - CI: `Deploy SQL corpus` 다음에 `./eval/corpus/Sql/deploy.sh "${{ job.services.mssql.id }}" 'Pelly_Test_2026!' PellyCorpusProd` 단계를 더한다.
- [ ] **Step 4: 통과를 확인한다** — 위 두 명령 → PASS. `dotnet test tests/PellyCoder.Integration.Tests --filter FullyQualifiedName~SqlServerExecutorTests`도 PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(sql): read-only definition reader, shared SQL call helper, production-role test DB"`

---

### Task 4: `sql`을 명령 묶음으로 — 기존 `pelly sql <파일>` 유지

**Files:**
- Modify: `src/PellyCoder.Cli/CliApp.cs`(`sql` 브랜치, 기본 명령 `SqlCommand`, `CommandSettings` 키, `FindOptionWithoutValue`의 브랜치 판정), `src/PellyCoder.Cli/Program.cs`(`--pause-on-exit` 원시 인자 판정이 `sql pull`·`sql check`를 알게)
- Test: `tests/PellyCoder.Cli.Tests/SqlCommandTests.cs`, `tests/PellyCoder.Cli.Tests/ExitPauseTests.cs`

**Interfaces:**
- Produces: `config.AddBranch("sql", b => { b.SetDefaultCommand<SqlCommand>(); b.AddCommand<SqlPullCommand>("pull"); b.AddCommand<SqlCheckCommand>("check"); })`. `SqlPullCommand`·`SqlCheckCommand`는 이 작업에서 `ExitCodes.EnvError`만 돌려주는 빈 껍데기로 등록하고 Task 5·8이 채운다. `CommandSettings`에 `"sql pull"`·`"sql check"` 키.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `SqlCommandTests.Bare_sql_with_file_still_runs_edit`: `pelly sql x.sql "요청" --dry-run` → 기존과 같이 `IRunner`가 `EditKind.Sql` 요청을 받는다.
  - `Sql_pull_routes_to_pull_command`, `Sql_check_routes_to_check_command`.
  - `Sql_pull_out_without_value_is_rejected`: `pelly sql pull dbo.X --out` → 종료 4, `--out 옵션에 값이 없습니다.`
  - `ExitPauseTests`: `sql pull … --pause-on-exit`도 기다린다.
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Cli.Tests --filter "FullyQualifiedName~SqlCommandTests|FullyQualifiedName~ExitPauseTests"` → FAIL.
- [ ] **Step 3: 구현한다** — 브랜치 기본 명령이 인자를 받는지 Step 1 첫 테스트로 확인한다. **Spectre 0.57.2가 브랜치 기본 명령에 위치 인자를 넘기지 못하면 여기서 멈추고 보고한다**(명령 이름을 바꾸는 것은 사용자 결정이다). 파일 이름이 `pull`·`check`인 경우는 `./pull`로 쓰라고 README에 적는다(Task 14).
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS, `dotnet test tests/PellyCoder.Cli.Tests` 전체 PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(cli): sql becomes a branch with the edit command as default, pull and check placeholders"`

---

### Task 5: `pelly sql pull`

**Files:**
- Create: `src/PellyCoder.Core/Sql/SqlPullService.cs`, `src/PellyCoder.Cli/Commands/SqlPullCommand.cs`(Task 4 껍데기를 채움)
- Modify: `src/PellyCoder.Cli/Composition.cs`(리더 팩토리 `Func<string, ISqlDefinitionReader>` 등록), `README.md`(`pelly sql pull` 절, `.gitignore` 안내)
- Test: `tests/PellyCoder.Core.Tests/Sql/SqlPullServiceTests.cs`(가짜 리더 + MockFileSystem), `tests/PellyCoder.Cli.Tests/SqlPullCommandTests.cs`, `tests/PellyCoder.Integration.Tests/Sql/SqlPullRoundTripTests.cs`

**Interfaces:**
- Consumes: `SqlReaders.Production/Development`(Task 3), `SqlDefinitionText.RenderPullFile/Hash`, `PullRecord`(Task 2).
- Produces:
  ```csharp
  public enum PullSource { Prod, Dev }
  public sealed record SqlPullRequest(IReadOnlyList<string> Objects, PullSource Source, string OutDirectory, bool Force);
  public enum PullItemStatus { Written, Exists, NotFound, UnsupportedKind, Encrypted, NoPermission, BadName }
  public sealed record PullItem(string Input, PullItemStatus Status, string? Path, IReadOnlyList<string> Notices);
  public sealed class SqlPullService(IFileSystem fs, TimeProvider time, int compatLevel)
  {
      public Task<IReadOnlyList<PullItem>> PullAsync(SqlPullRequest req, ISqlDefinitionReader source, SqlEndpoint sourceEndpoint,
          ISqlDefinitionReader? compareWith, CancellationToken ct); // compareWith: 운영에서 받을 때 개발 리더(없으면 null)
  }
  ```
  명령: `pelly sql pull <OBJECTS...> [--from prod|dev] [--out <DIR>] [--force] [--config <PATH>] [--pause-on-exit]`. 기본 `--out`은 `<작업 공간 루트>/<sql.pullDirectory>`(작업 공간은 현재 폴더 기준 `WorkspaceLocator.Resolve`). 하나라도 `Written`이 아니면 종료 4.

- [ ] **Step 1: 실패하는 테스트를 쓴다** (가짜 리더, 고정 시각 `2026-10-26T09:12:33Z`)
  - `Writes_alter_script_with_record`: `db/pulled/dbo.usp_X.sql`이 생기고, 첫 줄 `PullRecord.TryParse` 결과의 `Source=="prod"`, `Sha256 == SqlDefinitionText.Hash(정의)`, 파일이 UTF-8 BOM으로 시작한다.
  - `Schema_defaults_to_dbo`: 입력 `usp_X` → 파일 이름 `dbo.usp_X.sql`.
  - `Existing_file_is_not_overwritten_without_force` → `Exists`, 파일 내용 그대로. `Force_overwrites`.
  - `Unsupported_kind_fails_only_that_object`: 테이블(`U`) 하나와 프로시저 하나 → 각각 `UnsupportedKind`, `Written`.
  - `Encrypted_object_fails_with_encrypted_message`: 정의 NULL + 프로브 `CanViewDefinition=true` → `Encrypted`, 문구 `암호화된 객체는 받을 수 없습니다`.
  - `Null_definition_without_permission_says_permission`: 정의 NULL + `CanViewDefinition=false` → `NoPermission`.
  - `Dev_differs_adds_line_and_notice`, `Dev_missing_object_warns`(`개발 DB에 없음 — pelly sql 검증이 실패할 수 있습니다`), `Dev_unreachable_notes_not_compared`(`개발 DB와 비교하지 못함`) — 세 경우 모두 파일은 쓴다.
  - `SqlPullCommandTests.Prod_not_configured_exits_4_with_guidance`(문구에 `sql.production`), `From_dev_uses_dev_connection`, `Not_ignored_folder_shows_notice`(가짜 `IProcessRunner`가 `git check-ignore` 종료 1을 돌려줌 → `db/pulled/가 .gitignore에 없습니다 — 받은 파일이 커밋될 수 있습니다`), `No_git_skips_ignore_check`.
  - 통합 `Pulled_file_reapplies_to_same_definition`: 운영 역할 DB에서 `dbo.usp_CancelOrder`를 받아 그 파일을 같은 DB에 실행(테스트가 sa로 직접 실행)한 뒤 `SqlDefinitionText.Hash`가 받기 전과 같다.
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Core.Tests --filter FullyQualifiedName~SqlPullServiceTests` 및 Cli 테스트 → FAIL.
- [ ] **Step 3: 구현한다** — `.gitignore` 확인은 `IProcessRunner`로 `git check-ignore -q <폴더>`를 작업 공간 루트에서 실행한다(0=무시됨, 1=무시 안 됨, 그 밖·실행 실패=건너뜀). pelly는 `.gitignore`를 쓰지 않는다. README에 `db/pulled/` 한 줄을 `.gitignore`에 넣으라는 안내를 쓴다.
- [ ] **Step 4: 통과를 확인한다** — 같은 명령들 → PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(sql): pelly sql pull writes SSMS-shaped ALTER scripts with a pull record"`

---

### Task 6: 운영 비교 판정

**Files:**
- Create: `src/PellyCoder.Core/Sql/SqlProdComparer.cs`
- Test: `tests/PellyCoder.Core.Tests/Sql/SqlProdComparerTests.cs`

**Interfaces:**
- Consumes: `ISqlDefinitionReader`, `SqlReaderOpen`(Task 3), `PullRecord`, `SqlDefinitionText`(Task 2), `SqlBatch`.
- Produces:
  ```csharp
  public enum ProdCheckOutcome { NotConfigured, SameAsProd, UnchangedSincePull, ChangedSincePull, DiffersNoRecord, NotInProd, Unavailable }
  public sealed record ProdCheck(ProdCheckOutcome Outcome, string? FileHash, string? ProdHash, string? PulledHash, string Message);
  public static class SqlProdComparer
  {
      // fileTextLf: 파일 전체(LF), target: 대상 배치. prod.Reader가 null이고 Problem도 null이면 NotConfigured.
      public static Task<ProdCheck> CompareAsync(string fileTextLf, SqlBatch target, SqlObjectName targetName, SqlReaderOpen prod, int compatLevel, CancellationToken ct);
  }
  ```
  판정 순서는 설계 §6.2·§6.3·§6.4: 설정 없음 → `NotConfigured`; 접속·권한 실패 → `Unavailable`(메시지 `운영 정의와 비교하지 못했습니다: <원인>`); 운영에 없음 → `NotInProd`; 파일 대상 배치 해시 == 운영 해시 → `SameAsProd`; 유효한 받은 기록(§6.2: `Source=="prod"`, `Server`·`Database`가 `prod.Endpoint`와 같음, `Object`가 대상과 `SameAs`)의 해시 == 운영 해시 → `UnchangedSincePull`, 다르면 `ChangedSincePull`; 그 밖 → `DiffersNoRecord`.

- [ ] **Step 1: 실패하는 테스트를 쓴다** (가짜 리더)
  - `Not_configured_returns_NotConfigured_without_calls`(리더 호출 0회).
  - `Same_as_prod_wins_over_stale_record`: 받은 기록 해시는 옛것, 파일 = 운영 → `SameAsProd`.
  - `Crlf_resaved_file_is_same_as_prod`: 파일 대상 배치가 CRLF·줄 끝 공백만 다름 → `SameAsProd`.
  - `Unchanged_since_pull`, `Changed_since_pull`(메시지에 `받은 뒤 운영에서 dbo.usp_X가 바뀌었습니다`와 `pelly sql pull dbo.usp_X --force`).
  - `Record_from_dev_is_invalid` → `DiffersNoRecord`. `Record_from_other_server_is_invalid` → `DiffersNoRecord`. `Record_for_another_object_is_invalid` → `DiffersNoRecord`.
  - `Missing_in_prod_is_NotInProd`(메시지 `운영에 dbo.usp_X가 없습니다 (새 객체)`).
  - `Unreachable_is_Unavailable`(리더가 `SqlUnavailableException`), `Problem_from_policy_is_Unavailable`.
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Core.Tests --filter FullyQualifiedName~SqlProdComparerTests` → FAIL.
- [ ] **Step 3: 구현한다** — 대상 배치 텍스트는 `target.Text`, 받은 기록은 `PullRecord.TryParse(fileTextLf)`.
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(sql): production comparison verdict for a target batch"`

---

### Task 7: `pelly sql`·`apply`의 운영 비교 단계

**Files:**
- Create: `src/PellyCoder.Core/Fsm/Phases/SqlProdCheckPhase.cs`
- Modify: `src/PellyCoder.Core/Fsm/FsmOrchestratorFactory.cs`(`CreateSql`에 `SqlReaderOpen prod` 매개변수, LOCATE와 SLICE 사이에 단계), `src/PellyCoder.Cli/Composition.cs`(`SqlReaders.Production` 결과 전달), `src/PellyCoder.Core/Logging/RunLogger.cs`(`RunSummary`에 `SqlProdCheckSummary? SqlProd = null`), `src/PellyCoder.Core/Reporting/FailureKinds.cs`(`SqlProdChanged`), `src/PellyCoder.Core/Fsm/RunRequest.cs`(`RunContext`에 `ProdCheck? SqlProd`), `RunSummary`를 만드는 `RunLogger` 코드
- Test: `tests/PellyCoder.Core.Tests/Fsm/SqlProdCheckFlowTests.cs`, `tests/PellyCoder.Core.Tests/Reporting/FailureKindsTests.cs`(있으면)

**Interfaces:**
- Consumes: `SqlProdComparer.CompareAsync`(Task 6).
- Produces: `public sealed record SqlProdCheckSummary(string Outcome, string? FileHash, string? ProdHash, string? PulledHash);` `FailureKinds.SqlProdChanged = "SqlProdChanged"`(그리고 `IsKnown`에 포함). `FsmOrchestratorFactory.CreateSql(..., ISqlExecutor sql, SqlReaderOpen? prod = null)` — 기존 호출부는 바뀌지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다** (가짜 백엔드·가짜 리더, 기존 `SqlLocateFlowTests`의 준비 방식)
  - `Changed_since_pull_stops_before_model`: 종료 4, `FailureKind=="SqlProdChanged"`, 모델 생성 요청 0회, 파일 바이트 그대로, 메시지에 `다시 받으십시오`와 `따로 보관`.
  - `Differs_without_record_warns_and_continues`: 경고 `파일의 dbo.usp_X가 운영 정의와 다릅니다` 후 정상 진행.
  - `Unavailable_warns_and_continues`.
  - `Not_configured_is_silent_and_records_nothing`: 메시지 추가 없음, `summary.json`의 `sqlProd`가 없음(null) — 운영 연결 없는 사용자 동작 불변.
  - `Summary_records_outcome_and_hashes`: `sqlProd.outcome=="UnchangedSincePull"`, 해시 셋.
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Core.Tests --filter FullyQualifiedName~SqlProdCheckFlowTests` → FAIL.
- [ ] **Step 3: 구현한다** — 단계의 `Phase`는 `RunPhase.Locate`(시간이 LOCATE에 잡힌다). `ChangedSincePull`이면 `ctx.FailureKind = "SqlProdChanged"`, `PhaseOutcome.EnvError`. `apply`의 `sql` 작업은 같은 러너를 쓰므로 따로 고치지 않는다 — `SpecRunner`가 실패 종류를 그대로 기록하는지 기존 테스트 패턴으로 하나 확인한다(`Apply_sql_task_prod_changed_fails_task`, `tests/PellyCoder.Core.Tests/Specs/`).
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS, `dotnet test tests/PellyCoder.Core.Tests` 전체 PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(sql): stop an edit when production changed since the pull, record the comparison"`

---

### Task 8: `pelly sql check`

**Files:**
- Create: `src/PellyCoder.Cli/Commands/SqlCheckCommand.cs`(Task 4 껍데기를 채움), `src/PellyCoder.Core/Sql/SqlTargetResolver.cs`
- Modify: `README.md`(`pelly sql check` 절)
- Test: `tests/PellyCoder.Cli.Tests/SqlCheckCommandTests.cs`, `tests/PellyCoder.Core.Tests/Sql/SqlTargetResolverTests.cs`

**Interfaces:**
- Consumes: `SqlProdComparer`(Task 6), `SqlReaders.Production`(Task 3).
- Produces:
  ```csharp
  public sealed record SqlTargetResolution(SqlBatch? Target, string? Problem, bool Ambiguous);
  public static class SqlTargetResolver { public static SqlTargetResolution Resolve(SqlScript script, string? objectName); } // --object 또는 유일한 객체; 여럿이면 Ambiguous
  ```
  명령: `pelly sql check <FILE> [--object <NAME>] [--config <PATH>] [--pause-on-exit]`. 종료 코드는 설계 §6.4 표 그대로: `SameAsProd`·`UnchangedSincePull`·`NotInProd` → 0, `ChangedSincePull` → 1, `DiffersNoRecord`·`Unavailable`·`NotConfigured`·파일 오류 → 4, 대상 여럿 → 3.

- [ ] **Step 1: 실패하는 테스트를 쓴다** — 결과마다 하나씩(`Same_as_prod_exits_0`, `Unchanged_exits_0`, `Changed_exits_1`, `No_record_and_differs_exits_4`(문구 `받은 기록 없음 — 운영 변경 여부를 확인할 수 없습니다. 파일이 운영과 다릅니다`), `Not_in_prod_exits_0`, `Not_configured_exits_4`, `Two_objects_without_object_option_exits_3`). 모델 백엔드를 만들지 않음을 확인한다(가짜 러너 호출 0회).
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Cli.Tests --filter FullyQualifiedName~SqlCheckCommandTests` → FAIL.
- [ ] **Step 3: 구현한다** — `SqlLocatePhase`의 이름 해석과 같은 규칙(`SqlObjectName.Parse`·`SameAs`)을 `SqlTargetResolver`로 쓰되 `SqlLocatePhase`는 고치지 않는다(대화형 선택·`--line`이 있는 다른 경로).
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(sql): pelly sql check before applying in SSMS"`

---

### Task 9: 쓰기 컬럼 추출

**Files:**
- Create: `src/PellyCoder.Core/Sql/SqlWriteColumns.cs`
- Test: `tests/PellyCoder.Core.Tests/Sql/SqlWriteColumnsTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum SqlWriteKind { Insert, Update, Merge, Delete }
  public sealed record SqlWrite(SqlObjectName Table, IReadOnlyList<string>? Columns, SqlWriteKind Kind); // Columns null = 모든 컬럼(INSERT 목록 없음), Delete는 항상 빈 목록
  public static class SqlWriteColumns { public static IReadOnlyList<SqlWrite> Collect(string definition, int compatLevel, bool quotedIdentifier); }
  ```

- [ ] **Step 1: 실패하는 테스트를 쓴다** (설계 §7.3 ③-1)
  - `Insert_with_column_list`, `Insert_without_list_is_all_columns`(`Columns==null`), `Update_set_columns`, `Update_through_alias_resolves_table`(`UPDATE o SET Status=1 FROM dbo.Orders o`), `Merge_update_and_insert_columns`, `Delete_has_no_columns`, `Temp_table_and_table_variable_are_skipped`, `Three_part_name_is_skipped`, `Schema_defaults_to_dbo_in_result`.
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Core.Tests --filter FullyQualifiedName~SqlWriteColumnsTests` → FAIL.
- [ ] **Step 3: 구현한다** — ScriptDom `TSqlFragmentVisitor`로 `InsertSpecification`·`UpdateSpecification`·`MergeSpecification`·`DeleteSpecification`을 방문한다. 별칭은 같은 문장의 `FromClause` 테이블 참조에서 푼다. 같은 테이블·종류의 결과는 합친다.
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(impact): collect the columns a SQL module writes"`

---

### Task 10: C# 문자열의 SP 이름 찾기

**Files:**
- Create: `src/PellyCoder.Core/Impact/SqlNameStringFinder.cs`
- Modify: `src/PellyCoder.Core/Impact/NameImpactFinder.cs`(`Enclosing`·파일 읽기를 이 클래스가 쓸 수 있게 `internal` 유지 또는 헬퍼로 분리 — 동작 불변)
- Test: `tests/PellyCoder.Core.Tests/Impact/SqlNameStringFinderTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record SqlNameSeed(string TaskId, SqlObjectName Name);
  public static class SqlNameStringFinder
  {
      public const string Reason = "이름 호출(문자열)";
      // 대상: 작업 공간 전체 .cs(BoundedWalker, 2000 폴더). Incomplete = 잘렸거나 못 읽은 폴더·파일이 있음.
      public static (IReadOnlyList<ImpactTarget> Targets, bool Incomplete) Find(IFileSystem fs, Workspace ws, IReadOnlyList<SqlNameSeed> seeds, int maxDirectories = 2000);
  }
  ```
  멤버 이름·`멤버 밖` 규칙은 `NameImpactFinder.Enclosing`과 같다. 이유는 멤버 안이면 `Reason`, 멤버 밖이면 `멤버 밖`.

- [ ] **Step 1: 실패하는 테스트를 쓴다** (MockFileSystem 작업 공간, 시드 `dbo.usp_Cancel`)
  - `Finds_exact_literal_forms`: `"dbo.usp_Cancel"`, `"usp_Cancel"`, `"[dbo].[usp_Cancel]"`, `"[usp_Cancel]"`, `"DBO.USP_CANCEL"`이 각각 다른 메서드에 있을 때 다섯 곳 모두.
  - `Finds_exec_inside_inline_sql`: `"EXEC dbo.usp_Cancel @Id"`, `@"EXECUTE usp_Cancel"`, 원시 문자열 `"""EXEC dbo.usp_Cancel"""`, 보간 `$"EXEC dbo.usp_Cancel {id}"`.
  - `Ignores_prefix_and_suffix_names`: `"dbo.usp_CancelOrder"`, `"x_usp_Cancel"`, `"SELECT usp_Cancel_flag"` → 0곳.
  - `Other_schema_is_not_a_match`: `"sales.usp_Cancel"` → 0곳.
  - `Field_initializer_is_outside_member`: 상수 `const string P = "dbo.usp_Cancel";` → 멤버 = 감싼 타입 이름, 이유 `멤버 밖`.
  - `Truncated_walk_sets_incomplete`(`maxDirectories: 1`).
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Core.Tests --filter FullyQualifiedName~SqlNameStringFinderTests` → FAIL.
- [ ] **Step 3: 구현한다** — 리터럴 토큰(`StringLiteralToken`, `Utf8StringLiteralToken`, 원시 문자열, 보간의 `InterpolatedStringTextToken`)의 `ValueText`를 본다. 비교 대상 이름 꼴: `스키마.이름`(시드 스키마 없으면 `dbo`), 스키마 생략 `이름`, 각 부분 대괄호 유무. 전체 일치는 앞뒤 공백을 뗀 리터럴이 그중 하나와 대소문자 무시로 같을 때. `EXEC` 일치는 `\bEXEC(UTE)?\s+` 뒤에 그 꼴이 오고 뒤가 식별자 문자(`[A-Za-z0-9_@#$\]]`)가 아닐 때.
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS, `FullyQualifiedName~NameImpactFinder` 기존 테스트 PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(impact): find SQL object names in C# string literals"`

---

### Task 11: SQL 영향 탐색기

**Files:**
- Create: `src/PellyCoder.Core/Impact/SqlImpactFinder.cs`, `src/PellyCoder.Core/Impact/SqlImpactModel.cs`
- Modify: `src/PellyCoder.Core/Impact/ImpactModel.cs`(`ImpactTarget`에 대상 종류)
- Test: `tests/PellyCoder.Core.Tests/Impact/SqlImpactFinderTests.cs`, `tests/PellyCoder.Integration.Tests/Impact/SqlImpactFinderDbTests.cs`

**Interfaces:**
- Consumes: `ISqlDefinitionReader`·`SqlReaderOpen`(Task 3), `SqlWriteColumns`(Task 9), `SqlNameStringFinder`(Task 10). 시드의 현재 정의는 리더의 `GetDefinitionAsync`로 읽는다.
- Produces:
  ```csharp
  public enum ImpactTargetKind { CSharpMember, SqlObject }
  // ImpactTarget에 마지막 선택 매개변수 추가: ImpactTargetKind TargetKind = ImpactTargetKind.CSharpMember. SqlObject면 Path=="" , Member == "dbo.usp_Y"
  public sealed record SqlImpactSeed(string TaskId, int Line, SqlObjectName Object);
  public enum SqlImpactBasis { Prod, Dev }
  public sealed record SqlTableShare(string SeedTaskId, SqlObjectName Table, IReadOnlyList<string> WrittenColumns, int Modules, int Warned, int Omitted, int Unverifiable);
  public sealed record SqlImpactModuleRow(SqlObjectName Module, IReadOnlyList<string> OverlapColumns, bool Unverifiable); // 전체 목록 한 줄
  public sealed record SqlImpactResult(SqlImpactBasis? Basis, string? Unavailable, IReadOnlyList<ImpactTarget> Targets,
      IReadOnlyList<SqlTableShare> Tables, IReadOnlyList<string> NewObjectSeeds, bool Incomplete,
      IReadOnlyDictionary<string, IReadOnlyList<SqlImpactModuleRow>> FullLists); // 키: "<작업 ID>|<테이블>"
  public interface ISqlImpactFinder
  {
      Task<SqlImpactResult> FindAsync(Workspace ws, IReadOnlyList<SqlImpactSeed> seeds, SqlReaderOpen db, SqlImpactBasis basis, CancellationToken ct);
  }
  public sealed class SqlImpactFinder(IFileSystem fs, int compatLevel) : ISqlImpactFinder;
  public const int SqlImpactFinder.MaxWarningsPerTable = 10;
  ```
  이유 문자열: ① `EXEC`(참조 모듈이 PROCEDURE이고 정의에 `EXEC`/`EXECUTE` 다음 시드 이름이 나옴) 또는 `참조`, ② `SqlNameStringFinder.Reason`/`멤버 밖`, ③ `같은 컬럼 읽음`.

- [ ] **Step 1: 실패하는 테스트를 쓴다** (가짜 리더)
  - `Referencing_procedure_is_exec_target`: 참조 모듈 `dbo.usp_A`(P, 정의에 `EXEC dbo.usp_Seed`) → `TargetKind==SqlObject`, `Member=="dbo.usp_A"`, `Reason=="EXEC"`. 뷰 참조 → `참조`.
  - `Column_readers_are_narrowed_to_written_columns`: 시드가 `dbo.Orders.Status`를 UPDATE, 후보 셋(Status 읽음, Amount만 읽음, `SELECT *`) → 경고 둘(Status, `*`), 요약 `Modules==3, Warned==2`.
  - `Warnings_capped_at_ten_sorted_by_overlap_then_name`: 후보 12개 → 경고 10개, `Omitted==2`, 정렬 확인.
  - `Unverifiable_candidate_counts_in_summary`: `GetColumnUsesAsync`가 null → 경고 아님, `Unverifiable==1`, 전체 목록 행 `Unverifiable==true`.
  - `Delete_only_table_has_summary_but_no_column_warnings`.
  - `Seed_missing_on_server_is_new_object` → `NewObjectSeeds`에 작업 ID, 그 시드의 ①③ 없음, ②는 그대로 돈다.
  - `Unavailable_db_runs_only_string_search`: `db.Problem` 있음 → `Unavailable` 채움, `Basis==null`, C# 대상만.
  - `Exec_and_column_hit_on_same_module_warns_once`(같은 모듈은 한 번, 이유는 먼저 찾은 ①).
  - 통합 `[SqlServerFact]` `Finds_corpus_callers_and_column_readers`: 운영 역할 DB에서 시드 `dbo.usp_CancelOrder` → `dbo.Orders`의 요약이 있고 `Modules >= 1`.
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Core.Tests --filter FullyQualifiedName~SqlImpactFinderTests` → FAIL.
- [ ] **Step 3: 구현한다** — 순서는 설계 §7.3. 쓰기 컬럼이 `null`(전체)이거나 `SelectAll`이면 `GetColumnsAsync`로 펼친다. 컬럼 비교는 대소문자 무시.
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS, V2-1c `Impact` 테스트 전체 PASS(`ImpactTarget` 기본값 덕에 불변).
- [ ] **Step 5: 커밋** — `git commit -m "feat(impact): SQL impact finder — referencing modules, column readers, C# strings"`

---

### Task 12: 명세 검사·apply 연결과 경고

**Files:**
- Modify: `src/PellyCoder.Core/Impact/ImpactCheck.cs`(`SqlSeeds`, `Covers`의 sql 규칙, SQL 경고), `src/PellyCoder.Core/Specs/SpecChecker.cs`(`AddImpactAsync`에 SQL 탐색), `src/PellyCoder.Core/Specs/SpecRunner.cs`(시작 검사 조건, `impact-sql.json`), `src/PellyCoder.Core/Specs/ApplySummary.cs`, `src/PellyCoder.Cli/Commands/SpecCheckCommand.cs`(`--impact` 설명·DB 리더 전달), `src/PellyCoder.Cli/Commands/ApplyCommand.cs`, `README.md`(`spec check`·`apply`의 SQL 영향 탐색, 한계)
- Test: `tests/PellyCoder.Core.Tests/Impact/ImpactCheckTests.cs`, `tests/PellyCoder.Core.Tests/Specs/SpecCheckerTests.cs`, `tests/PellyCoder.Core.Tests/Specs/SpecRunnerTests.cs`(또는 apply 시작 검사 테스트가 있는 파일)

**Interfaces:**
- Consumes: `ISqlImpactFinder`, `SqlImpactResult`(Task 11), `SqlReaders`(Task 3).
- Produces:
  ```csharp
  // ImpactCheck
  public static (IReadOnlyList<SqlImpactSeed> Seeds, IReadOnlyList<string> NotFound) SqlSeeds(IFileSystem fs, Workspace ws, ChangeSpec spec, int compatLevel); // 미완료 sql 작업; #객체 없고 객체 여럿이면 NotFound
  public static IReadOnlyDictionary<string, SqlObjectName> SqlTaskObjects(IFileSystem fs, Workspace ws, ChangeSpec spec, int compatLevel); // 모든 sql 작업(완료 포함) → 대상 객체
  public static bool Covers(SpecTask task, ImpactTarget target, IReadOnlyDictionary<string, SqlObjectName> sqlObjects);
  public static IReadOnlyList<SpecIssue> SqlIssues(ChangeSpec spec, IReadOnlyList<SqlImpactSeed> seeds, IReadOnlyList<string> notFound, SqlImpactResult result, IReadOnlyDictionary<string, SqlObjectName> sqlObjects, string pullDirectory, string? listPath);
  // SpecChecker
  public sealed record SqlImpactRequest(ISqlImpactFinder Finder, SqlReaderOpen Db, SqlImpactBasis Basis, string PullDirectory, string? ListPath);
  public static Task<SpecCheckResult> AddImpactAsync(SpecCheckResult check, IImpactFinder? impact, bool impactAllCsTasks, CancellationToken ct, SqlImpactRequest? sql = null);
  // SpecCheckResult에 SqlImpactResult? SqlImpact = null 추가. ApplySummary에 SqlImpactSummary? SqlImpact = null(WhenWritingNull): Basis, Seeds, Warnings, ListPath
  ```
  기존 2인자 `Covers(SpecTask, ImpactTarget)`는 남기고 새 3인자에서 cs 규칙을 그것에 맡긴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `ImpactCheckTests.Covers_sql_object_ignores_case_brackets_and_default_schema`: 작업 `#[dbo].[USP_Cancel]` ↔ 대상 `dbo.usp_Cancel`(SqlObject) → true. 작업 `#usp_Cancel` ↔ 같은 대상 → true. cs 작업 ↔ SqlObject 대상 → false.
  - `Covers_sql_task_without_object_uses_file_single_object`(`SqlTaskObjects`가 파일의 유일 객체로 채움).
  - `SqlIssues_missing_sql_object_message`: 문구가 정확히 `작업 T2의 영향 대상이 명세에 없습니다: dbo.usp_A (EXEC) — pelly sql pull dbo.usp_A 로 받아 작업 줄에 추가할 수 있습니다 (db/pulled/dbo.usp_A.sql)`.
  - `SqlIssues_table_summary_line`: 코드 `ImpactTableShared`, 문구 `작업 T2: dbo.Orders를 함께 쓰는 모듈 50개 — 쓰는 컬럼(Status, UpdatedAt)을 읽는 모듈 7개 경고, 외 0개 생략, 컬럼 확인 불가 2개. 전체 목록: <경로>`.
  - `SqlIssues_dev_basis_warns_once`(`ImpactSqlDevBased`, 줄 0, `영향 탐색: 운영 연결이 설정되지 않아 개발 DB 기준으로 찾았습니다`), `SqlIssues_unavailable_names_db`(`ImpactSqlUnavailable`), `SqlIssues_new_object_seed`(`ImpactSeedNotFound`, `작업 T2: dbo.usp_New가 서버에 없어(새 객체) SP 쪽 영향을 찾지 않았습니다`), `SqlIssues_ambiguous_seed_not_found`.
  - `SpecCheckerTests.Without_impact_flag_no_sql_search`(가짜 SQL 탐색기 호출 0회 — 운영 설정 여부와 무관), `Impact_flag_with_prod_uses_prod_basis`, `Impact_flag_without_prod_uses_dev_basis`, `Prod_configured_but_unreachable_does_not_fall_back_to_dev`(개발 리더 호출 0회, `ImpactSqlUnavailable`), `Spec_check_writes_list_under_pelly_impact`(`.pelly/impact/<명세 id>-sql.json`).
  - apply: `Apply_without_prod_does_not_search_sql`(운영 미설정이면 SQL 탐색기 호출 0회·질문 추가 없음 — 운영 연결 없는 사용자 동작 불변), `Apply_with_prod_searches_sql_and_writes_list`(`.pelly/runs/<apply-id>/impact-sql.json`, `apply.json`의 `sqlImpact.basis=="prod"`).
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~ImpactCheckTests|FullyQualifiedName~SpecCheckerTests|FullyQualifiedName~SpecRunner"` → FAIL.
- [ ] **Step 3: 구현한다** — 실행 조건은 설계 §7.1·§7.2 그대로: `spec check`는 `--impact`일 때만(운영 있으면 `Prod`, 없으면 `Dev`), `apply`는 운영이 설정되어 있을 때만(`Prod`). 운영이 설정되어 있는데 열 수 없으면 개발로 대신하지 않는다. SQL 탐색이 던진 예외(취소 제외)는 기존 `ImpactFailed` 경고로 바꾼다. C# 쪽 탐색(V2-1c)과 SQL 쪽 탐색은 서로 독립이며 둘 다 돌 수 있다.
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS, `dotnet test tests/PellyCoder.Core.Tests tests/PellyCoder.Cli.Tests` 전체 PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(spec): SQL impact warnings in spec check --impact and apply"`

---

### Task 13: `pelly doctor` 운영 연결 행

**Files:**
- Modify: `src/PellyCoder.Core/Doctor/DoctorService.cs`, `src/PellyCoder.Core/Doctor/DoctorFacts.cs`(`SqlProduction = "SQL 운영(읽기 전용)"`), `src/PellyCoder.Cli/Composition.cs`(운영 리더 전달)
- Test: `tests/PellyCoder.Core.Tests/Doctor/DoctorServiceTests.cs`

**Interfaces:**
- Consumes: `SqlReaders.Production`(Task 3).
- Produces: `DoctorService` 생성자 선택 매개변수 `SqlReaderOpen? prodSql = null`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Production_not_configured_is_ok_optional`: 상태 `Ok`, 문구 `운영 연결 없음(선택)` — 실패로 세지 않는다.
  - `Production_outside_allow_list_fails`, `Production_unreachable_fails`, `Production_without_view_definition_fails`(조치 문구에 `GRANT VIEW DEFINITION`), `Production_ok_shows_server_and_database`.
  - `Settings_line_masks_production_connection`: 설정 요약 줄에 운영 연결이 `SqlConnectionText.Mask`로 나온다.
- [ ] **Step 2: 실행해 실패를 확인한다** — `dotnet test tests/PellyCoder.Core.Tests --filter FullyQualifiedName~DoctorServiceTests` → FAIL.
- [ ] **Step 3: 구현한다** — 기존 `CheckSqlAsync`와 같은 모양으로 `CheckSqlProductionAsync`를 더하고 개발 행 바로 뒤에 둔다. 개발과 같은 대상 검사는 설정 로더(Task 1)가 이미 막으므로 여기서는 하지 않는다.
- [ ] **Step 4: 통과를 확인한다** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `git commit -m "feat(doctor): production read-only connection row"`

---

### Task 14: 문서·PRD

**Files:**
- Modify: `README.md`(SQL 설정 표에 `sql.production.*`·`sql.pullDirectory`, `PELLY_SQL_PROD_CONNECTION`, 파일 이름이 `pull`/`check`면 `./pull`, 한계: 동적 SQL·상수를 거친 이름·수정 전 정의 기준·다른 DB 참조), `deploy/usage.md`(SSMS 흐름: pull → `pelly sql` → `pelly sql check` → SSMS 실행), `deploy/server-checklist.md`(운영 DB에 `VIEW DEFINITION`만 가진 로그인 만드는 T-SQL), `docs/PRD.md`(§12.2 표에 `V2-1d | 운영 정의 읽기·sql pull·SQL 영향 탐색 | 1~2주 | V2-1c` 행, §12.3 아래 V2-1d 절, 변경 이력 새 버전 행)

- [ ] **Step 1: 문서를 고친다** — 설계 §10 항목을 모두 반영한다. 사용자 문구는 코드의 실제 문구와 같게 복사한다.
- [ ] **Step 2: 확인한다** — `grep -n "PELLY_SQL_PROD_CONNECTION\|sql pull\|sql check\|V2-1d" README.md deploy/usage.md deploy/server-checklist.md docs/PRD.md`가 네 파일 모두에서 나온다. `dotnet build -warnaserror` 경고 0, `dotnet test` 실패 0.
- [ ] **Step 3: 커밋** — `git commit -m "docs: V2-1d production reads, sql pull/check and SQL impact search"`

---

## 끝난 뒤 (사람 몫)

- 브랜치 push와 CI(특히 `sql-integration`의 `PellyCorpusProd` 배포) 확인, main 병합 여부는 사람이 고른다.
- 현장에서 운영 DB 주요 테이블 하나를 쓰는 SP로 `pelly spec check --impact`를 돌려 개별 경고 수(상한 10)가 읽을 만한지 본다(설계 §9).
- Windows에서 SQL 경로 확인은 기존과 같이 사람 몫이다.
