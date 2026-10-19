# Pelly-Coder CLI v1 설계 문서

* **기반 문서:** `docs/PRD.md` v1.4 — 요구사항(무엇을)은 PRD가 기준이며, 이 문서는 **어떻게 만들지**를 정합니다. PRD와 충돌하면 이 문서의 "결정 사항"을 우선하고, 그 외에는 PRD를 따릅니다.
* **범위:** PRD v1 (M0 ~ M3). M4(폐쇄망 이관)는 산출물 요구만 반영합니다. v2(§12)는 확장 지점만 남깁니다.
* **독자:** 이 문서를 읽고 구현 계획을 세워 직접 구현할 Claude Code.
* **작성일:** 2026-10-17

---

## 1. 의도

폐쇄망 개발자가 사내 GPU 서버의 작은 MoE 모델(`Qwen3.6-35B-A3B`, 활성 3B)로 C# 메서드와 SQL 저장 프로시저를 안전하게 고치게 하는 CLI입니다. 작은 모델은 긴 컨텍스트, JSON 도구 호출, 자율 판단에 약하므로, **흐름은 코드(FSM)가 정하고, 모델에는 아주 작은 컨텍스트만 주고, 결과는 컴파일러와 DB 엔진이 판정**합니다.

### 1.1 목표
* PRD §2.2 KPI 달성 (특히 검증 불변 조건 100%, 최종 작업 성공률 ≥ 85% 잠정).
* Mac에서 대부분 개발·테스트할 수 있고, Windows 전용 부분은 CI에서 자동 검증되는 구조.
* 폐쇄망 반입 후 코드 재반입 없이 설정만으로 튜닝 가능한 구조.

### 1.2 비목표 (v1)
PRD §1.4의 "v1 제외" 항목 전부. 특히 다중 파일 수정, 새 파일 생성, 시그니처 변경, DB 직접 배포, 대화형 모드.

### 1.3 실패로 간주하는 것 (기능이 동작해도)
* 검증을 통과하지 못한 코드가 사용자 파일에 남는 경우 (중단·예외·크래시 포함)
* 사용자 파일의 인코딩·줄바꿈이 바뀌거나, 외부에서 수정된 파일을 덮어쓰는 경우
* 실행 중 설정된 Ollama·SQL 서버 외의 네트워크 접속
* 비식별 리포트(`report --sanitize`)에 코드·경로·식별자가 섞여 나가는 경우
* 흐름 제어를 프롬프트 지시에 맡기는 설계 (예: "다음에는 빌드를 확인하세요")

---

## 2. 제약과 환경

| 항목 | 내용 | 출처 |
| :--- | :--- | :--- |
| 런타임 | .NET 10 (SDK 10.0.302 설치됨) | PRD |
| 개발 장비 | Apple M5 Pro, 48GB, Ollama 0.40.0, Docker 29 | 확인함 |
| Windows 장비 | **없음.** Windows 전용 검증은 GitHub Actions `windows-latest`에서 수행 | 사용자 |
| 저장소 | GitHub에 올림 (CI 사용 가능) | 사용자 |
| 모델 | 폐쇄망 서버와 같은 모델을 Mac에 내려받아 사용. 이름은 설정값 `llm.model` | 사용자 |
| 운영 | 폐쇄망 Windows 11 x64 + 원격 Ollama 서버, 인터넷 없음 | PRD |
| 대상 코드 | SDK 스타일 + **레거시 .NET Framework** 프로젝트. .NET Framework 4.5 / 4.8과 .NET Core 혼재. 앱 종류는 MVC(ASP.NET MVC 5, ASP.NET Core MVC)와 콘솔 배치. DB 접근은 Dapper와 ADO.NET | 사용자 |
| 메서드 크기 | 보통 50~150줄(약 700~2,200토큰) → 대상 멤버 전체를 컨텍스트에 넣는 방식 유지. 예산(3,000토큰)을 넘는 메서드는 v1에서 명확한 메시지와 함께 종료 코드 4 | 사용자 / 설계 |
| 구현 주체 | Claude Code | 사용자 |

---

## 3. 결정 사항

"사용자"는 사용자가 정한 것, "설계"는 이 문서가 정한 것(구현자가 근거 있게 바꿀 수 있음)입니다.

| # | 결정 | 주체 |
| :--- | :--- | :--- |
| D1 | LLM 서버는 Ollama만 지원 (`/api/generate`, `raw: true`) | 사용자 |
| D2 | Windows 전용 동작(레거시 빌드, 파일 잠금, CP949 콘솔)은 GitHub Actions `windows-latest`에서 검증. 로컬 Mac에서는 녹화된 출력(fixture)으로 테스트 | 설계 |
| D3 | SQL 통합 테스트는 SQL Server Docker 컨테이너(로컬은 Docker, CI는 `ubuntu-latest` 서비스 컨테이너) | 설계 |
| D4 | 실제 모델 E2E 테스트는 로컬 Mac에서만 실행 (CI 제외) | 설계 |
| D5 | 솔루션을 `PellyCoder.Core`(로직) + `PellyCoder.Cli`(명령·콘솔 UI)로 분리. PRD §6.2의 단일 프로젝트 구조에서 바꾼 이유: Core를 콘솔 없이 테스트하기 위함. 폴더(네임스페이스) 구성은 PRD §6.2를 따름 | 설계 |
| D6 | 모든 외부 의존(프로세스 실행, 파일 시스템, LLM, SQL, 시간, 사용자 입력)은 인터페이스 뒤에 둠 | 설계 |
| D7 | **편집 가능 영역 제한:** SEARCH 매칭은 대상 멤버 범위와 `using` 블록 안에서만 수행 (SQL은 파일 전체) | 설계 |
| D8 | **Baseline 빌드를 모델 생성과 병렬 실행.** APPLY 단계는 Baseline 빌드 완료를 기다린 뒤 파일을 바꿈 | 설계 |
| D9 | 시스템 프롬프트는 영어, 사용자 요청은 입력 그대로. 템플릿은 코드가 아닌 내장 리소스 파일 + 설정 덮어쓰기 | 설계 |
| D10 | chat template·think 태그·특수 토큰은 하드코딩하지 않고 리소스/설정에서 읽음. 기본값은 ChatML(`<|im_start|>`…) + `<think>` (M0 스파이크 S1에서 확정) | 설계 |
| D11 | 비식별 리포트는 **허용 목록(whitelist) 방식**: 타입이 정해진 수치·열거값 필드만 출력하고 로그의 문자열은 복사하지 않음 | 설계 |
| D12 | 같은 워크스페이스에서 pelly 동시 실행 금지 (`.pelly/lock`) | 설계 |
| D13 | diff 생성은 `DiffPlex`, 파일 시스템 추상화는 `System.IO.Abstractions` 사용 | 설계 |
| D14 | **멤버 모양 검사(MemberShapeGuard):** APPLY 후 대상 멤버의 시그니처가 바뀌었거나, 허용되지 않은 멤버가 추가·삭제되면 거부하고 교정 피드백. 빌드가 대상 프로젝트만 검사하므로, 다른 프로젝트의 호출부를 깨는 변경을 구조적으로 막기 위함 (§8.7.1) | 설계 |
| D15 | **SQL 파일은 `GO`로 나눈 배치 단위로 다룸.** SSMS가 스크립트한 형태(`SET ANSI_NULLS ON` / `GO` / `CREATE PROC` / `GO`)와 한 파일 여러 객체를 지원. 편집 가능 영역·안전 가드·NOEXEC 검증은 **대상 객체 배치에만** 적용하고, 나머지 배치는 바뀌면 안 됨 (§8.10) | 설계 |
| D16 | `pelly sql`은 DB 연결이 필수. 연결할 수 없으면 PREFLIGHT에서 EnvError(4). ScriptDom만으로 "검증 통과"를 주지 않음(검증 불변 조건 유지) | 설계 |
| D17 | 설정 파일의 `sql.connectionString`은 **비밀번호가 없는 연결(Windows 통합 인증)만** 허용. `Password`/`Pwd`가 있으면 설정 오류(4). 비밀번호가 있는 연결은 환경 변수 `PELLY_SQL_CONNECTION`으로만 받음. `dotnet user-secrets`는 전역 도구에서 의미가 없어 제외 (PRD FR-07, M2b R3) | 설계 |
| D19 | **IDE 연동은 External Tools 방식.** 사용자들은 Visual Studio와 Rider를 혼용하므로 IDE별 플러그인 대신 두 IDE 공통 기능인 External Tools로 `pelly cs <파일> --line <커서 줄>`을 실행. v1 UI는 콘솔 창. GUI 승인 창(`pelly ui`, Avalonia)은 v1 다음 단계 | 사용자 |
| D20 | `Core`는 콘솔에 직접 출력하지 않음. 모든 진행 표시·입력은 `IUserInteraction`을 통해서만 함 (다음 단계의 GUI 승인 창이 같은 `Core`를 쓰기 위함) | 설계 |
| D21 | **Think 단계는 교정 시도부터 사용** (`llm.thinkMode = retry` 기본, `off`/`always` 선택 가능). 첫 시도는 빈 think 프리필로 바로 코드 생성. 근거: S3 — 이 모델은 512토큰 안에 생각을 끝내지 못하고(30/30), 생략해도 10건 결과가 같았으며 평균 9.0초 → 1.7초 | 사용자 |
| D23 | **`stop` 옵션에 의존하지 않음.** 이 모델 엔진(raw 모드)은 `stop`을 무시함(S1 보완 측정). `stop`은 보내되, THINK 응답은 클라이언트가 첫 `</think>` 앞까지만 쓰고 나머지는 버림. CODE 응답은 종료 토큰(`<|im_end|>`)·`num_predict`로 끝나며, 파서는 마지막 `>>>>>>> REPLACE` 뒤 텍스트를 무시함. 생성 길이는 항상 `num_predict`로 제한 | 설계 |
| D22 | **Think 예산은 서버 `num_predict`로 제한**하고 `done_reason == "length"`로 잘림을 판단. 클라이언트가 스트림을 끊지 않음(Ctrl+C 제외). 근거: S2 — 클라이언트 취소 시 다음 요청이 약 2초 늦어짐 | 설계 |
| D24 | **모델 제어 토큰 문자열 차단.** raw 모드에서는 서버가 프롬프트 안의 `<|im_end|>` 같은 문자열을 실제 제어 토큰으로 읽으므로, 요청문이나 대상 파일에 토크나이저 특수 토큰(26개) 문자열이 있으면 서버를 부르기 전에 PREFLIGHT에서 종료 코드 4로 끝냄 (요청문: "요청문에 모델 제어 토큰 '…'이(가) 있어 실행할 수 없습니다", 파일: "파일에 모델 제어 토큰 문자열 '…'이(가) 있어 안전하게 편집할 수 없습니다 (줄 N)"). 근거: M1 최종 리뷰 (M2a T10) | 설계 |
| D25 | **Baseline 빌드는 LOCATE 끝에서 시작.** 대상 프로젝트와 빌드 도구를 확정한 뒤에야 시작할 수 있고, 모델 생성(SLICE·EDIT) 동안 병렬로 돌며 APPLY가 첫 쓰기 전에 기다림. 드라이런은 도구만 확인하고 Baseline을 시작하지 않음. 실행이 끝날 때 오케스트레이터가 취소하고 최대 10초 기다린 뒤 롤백·잠금 해제 (M2a T6, §7.3) | 설계 |
| D26 | **`ISqlExecutor`는 범용 질의 대신 목적별 메서드.** 가짜 구현으로 단위 테스트하기 쉽게 하려는 것(D6의 목적). 새 질의가 필요하면 메서드를 하나 더 추가한다 (M2b R1, §5) | 설계 |
| D27 | **접속 대상 허용 목록이 비어 있으면 아무 데도 접속하지 않음**(종료 5, 메시지는 §8.1의 두 문구: 서버는 sql.allowedHosts에, 데이터베이스는 sql.allowedDatabases에 추가하라는 안내). 연결 문자열에 서버·데이터베이스 키가 둘 이상(`Server`/`Data Source`/`Address`/`Addr`/`Network Address`, `Database`/`Initial Catalog`)이거나 `Failover Partner`가 있으면 SqlClient의 마지막 값 우선을 흉내 내지 않고 거부(종료 5) — fail closed (M2b R4, 허용 목록 검토 판정, §8.1) | 설계 |
| D28 | **원본 대상 배치에 금지 구문이 이미 있으면 LOCATE에서 종료 5로 끝냄**("대상 객체에 이미 금지 구문(…)이 있어 pelly로 편집할 수 없습니다 (PRD FR-07)", FailureKind `SqlForbiddenInOriginal`). 재시도해도 매번 거부되기 때문. 본문에 `GRANT`·`xp_cmdshell` 등을 가진 프로시저는 pelly로 고칠 수 없다 (M2b R2, §8.10) | 설계 |
| D18 | `--test`의 테스트 프로젝트는 솔루션 안에서 대상 프로젝트를 `ProjectReference`(레거시는 `<ProjectReference Include>`)로 참조하는 프로젝트 중 이름이 `Test`를 포함하는 것. 설정 `test.projects`로 직접 지정 가능 | 설계 |

---

## 4. 솔루션 구조

```text
PellyCoder.sln
├── src/
│   ├── PellyCoder.Core/                # net10.0 클래스 라이브러리 (콘솔 의존 없음)
│   │   ├── Abstractions/               # IProcessRunner, ILlmBackend, ISqlExecutor, IUserInteraction, ITokenCounter
│   │   ├── Config/                     # PellyOptions, 기본값, 검증
│   │   ├── Core/                       # FsmOrchestrator, RunContext, Transitions, ContextBudget, Workspace
│   │   ├── Llm/                        # OllamaBackend, QwenStreamClient, QwenChatTemplate, TokenCounter, Prompts
│   │   │   └── Resources/              # vocab.json, merges.txt, tokenizer-meta.json, prompts/*.txt, template.json (EmbeddedResource)
│   │   ├── Locators/
│   │   ├── Slicers/
│   │   ├── Patchers/
│   │   ├── Verifiers/
│   │   └── Logging/                    # RunLogger, RunReport, ReportSanitizer
│   └── PellyCoder.Cli/                 # net10.0 exe, 어셈블리 이름 pelly, PackAsTool
│       ├── Program.cs                  # DI 구성, Ctrl+C, 콘솔 인코딩
│       ├── Commands/                   # CsCommand, SqlCommand, DoctorCommand, ReportCommand
│       └── Console/                    # SpectreUserInteraction, DiffRenderer
├── tests/
│   ├── PellyCoder.Core.Tests/          # 단위·스냅샷 (어디서나 실행)
│   ├── PellyCoder.Integration.Tests/   # Trait로 구분: Windows / SqlServer / Ollama
│   └── fixtures/                       # 녹화된 msbuild/dotnet 출력, Ollama NDJSON, 샘플 소스(CP949 등)
├── eval/
│   ├── PellyCoder.Eval/                # 벤치마크 실행기 (exe)
│   ├── benchmark/                      # 작업 정의 YAML
│   └── corpus/
│       ├── Modern/                     # SDK 스타일 솔루션 (net10.0 + net48)
│       ├── Legacy/                     # 레거시 csproj, packages.config, C# 7.3, CP949·CRLF 파일 포함
│       └── Sql/                        # 샘플 DB 스키마·SP, 초기화 스크립트
├── deploy/                             # publish 스크립트, server-checklist.md, package-manifest 양식
├── docs/
│   └── spikes/                         # M0 스파이크 결과
└── .github/workflows/ci.yml
```

### 4.1 패키지
| 용도 | 패키지 |
| :--- | :--- |
| CLI | `Spectre.Console`, `Spectre.Console.Cli` |
| DI·설정 | `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Configuration.Json`, `.EnvironmentVariables` |
| C# 분석 | `Microsoft.CodeAnalysis.CSharp` |
| SQL | `Microsoft.SqlServer.TransactSql.ScriptDom`, `Microsoft.Data.SqlClient`, `Dapper` |
| 토큰 | `Microsoft.ML.Tokenizers` **2.0.1** (S4: 서버와 토큰 수 100% 일치) |
| 기타 | `DiffPlex`, `System.IO.Abstractions` (+ `.TestingHelpers`), `System.Text.Encoding.CodePages`는 런타임 내장 provider 등록만 |
| 테스트 | `xunit.v3`(또는 xunit 2 최신), `Verify.Xunit`, `NSubstitute` |

라이선스가 MIT/Apache 2.0이고 실행 중 외부 통신이 없는 패키지만 씁니다. 버전은 `Directory.Packages.props`로 중앙 관리합니다.

---

## 5. 핵심 추상화

```csharp
// 프로세스 실행 — 빌드, vswhere, git, 테스트 러너 모두 이것을 통해 실행
public sealed record ProcessSpec(
    string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    TimeSpan Timeout, Encoding? OutputEncoding = null);
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration, bool TimedOut);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct); // 취소 시 프로세스 트리 종료
}

// LLM
public sealed record SamplingOptions(double Temperature, double TopP, int TopK, double RepeatPenalty, double PresencePenalty);
public sealed record LlmRequest(string RawPrompt, SamplingOptions Sampling, int NumPredict,
    IReadOnlyList<string> Stop, int NumCtx, string KeepAlive);
public sealed record LlmChunk(string Text, bool Done, int? PromptEvalCount, int? PromptEvalCachedCount,
    int? EvalCount, TimeSpan? PromptEvalDuration, string? DoneReason);   // PromptEvalCount는 캐시분 포함 전체 (S2)
public sealed record LlmServerStatus(bool Reachable, string? Version, bool ModelInstalled, bool ModelLoaded,
    string? ModelTemplate, TimeSpan Latency);
public interface ILlmBackend
{
    IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, CancellationToken ct); // ct 취소 = 스트림 중단
    Task<LlmServerStatus> GetStatusAsync(CancellationToken ct);
}

public interface ITokenCounter { int Count(string text); }

// SQL
// M2b R1: 범용 QueryAsync<T> 대신 목적별 메서드 (D26). 문장은 항상 매개변수로 보내고,
// 문자열에 이어 붙이는 것은 CompileWithNoExecAsync의 대상 배치 하나뿐
public interface ISqlExecutor
{
    Task<SqlServerProbe> ProbeAsync(CancellationToken ct);                       // 버전·DB 이름·VIEW DEFINITION 보유 여부
    Task<IReadOnlyList<SqlObjectName>> FindMissingAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct); // OBJECT_ID가 NULL인 이름
    Task<IReadOnlyList<SqlTableSchema>> GetSchemasAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct); // 테이블·뷰의 컬럼·인덱스
    Task<IReadOnlyList<SqlObjectName>> GetReferencedEntitiesAsync(SqlObjectName obj, CancellationToken ct);       // sys.dm_sql_referenced_entities
    Task<IReadOnlyList<SqlCompileError>> CompileWithNoExecAsync(string batch, SqlSessionSettings settings, CancellationToken ct); // 트랜잭션+ROLLBACK 포함, 심각도 11 이상
}

// 사용자 상호작용 — Cli가 Spectre로 구현, 테스트는 스크립트형 가짜
public interface IUserInteraction
{
    bool IsInteractive { get; }
    Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct);
    Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct);
    void ShowDiff(string path, string before, string after);
    void ShowStatus(RunPhase phase, string message);
    void Warn(string message);
}
```
시간은 `TimeProvider`, 파일 시스템은 `IFileSystem`(System.IO.Abstractions)을 씁니다. `File.*`, `Process.Start`, `HttpClient` 직접 사용은 각 구현 클래스 안에서만 허용합니다.

---

## 6. 데이터 모델

```csharp
public enum EditKind { CSharp, Sql }
public sealed record Workspace(string Root, string PellyDir);           // Root: .git/.svn/*.sln가 있는 가장 가까운 상위 폴더, 없으면 대상 파일 폴더
public sealed record EditTarget(EditKind Kind, string FilePath, string SymbolDisplay,
    TextSpanInfo MemberSpan, string? ProjectPath);                     // MemberSpan: 대상 멤버의 원본 범위
public sealed record TextSpanInfo(int StartLine, int EndLine);          // 1-based, 포함 범위
public sealed record EditableRegion(TextSpanInfo Span, string Reason);  // D7: 대상 멤버, using 블록

public sealed record SlicePart(string Kind, string Text, int Tokens, int Priority); // Kind: target, usings, fields, signatures, dto, schema
public sealed record Slice(IReadOnlyList<SlicePart> Included, IReadOnlyList<SlicePart> Omitted, string LanguageHint); // "C# 7.3" 등

public sealed record PatchBlock(string Search, string Replace);
public enum MatchStage { Exact = 1, TrailingWhitespace = 2, Indentation = 3 }
public sealed record PatchOutcome(bool Success, string? NewText, IReadOnlyList<MatchStage> Stages, PatchFailure? Failure);
public sealed record PatchFailure(string Kind, string Detail, string? NearestOriginal); // Kind: Parse, NotFound, Ambiguous, OutsideRegion, Overlap

public sealed record Diagnostic(string File, int Line, int Column, string Severity, string Code, string Message);
public sealed record VerifyOutcome(bool Passed, IReadOnlyList<Diagnostic> NewErrors, IReadOnlyList<Diagnostic> NewWarnings,
    IReadOnlyList<string> Notices);                                     // Notices: "동적 SQL 미검증" 등
public sealed record Feedback(string Text, int Tokens);                 // 다음 시도에 넣을 최신 피드백 1건 (누적 금지)
```

`RunContext`는 실행 1회의 가변 상태(run-id, 현재 상태, 시도 번호, 백업 정보, Baseline 작업 `Task<BaselineSnapshot>`, 최신 Feedback, 타이밍)를 담습니다.

---

## 7. FSM

### 7.1 상태와 전이
```csharp
public enum RunPhase { Preflight, Locate, Slice, Edit, Apply, Verify, Review, Done, Rollback, Failed }
```
전이 규칙은 **순수 함수** `Transitions.Next(RunPhase current, PhaseOutcome outcome, int attempt, int maxRetries)`로 구현하고 표 기반 단위 테스트로 검증합니다.

| 현재 | 결과 | 다음 |
| :--- | :--- | :--- |
| Preflight | Ok | Locate |
| Preflight | EnvError | Failed(4) |
| Locate | Ok | Slice |
| Locate | Ambiguous/NotFound | Failed(3) |
| Slice | Ok | Edit |
| Slice | BudgetExceeded | Failed(4) |
| Edit | Ok | Apply |
| Edit | FormatError (블록 파싱 실패) | 시도 < 1+max ? Slice : Rollback→Failed(1) |
| Apply | Ok | Verify |
| Apply | MatchFailed | 시도 < 1+max ? Slice : Rollback→Failed(1) |
| Preflight, Locate | SafetyRejected (접속 대상 허용 목록 위반 · 원본 대상의 금지 구문, M2b T6·T7) | Failed(5) |
| Apply | SafetyRejected (SQL 가드) | 시도 < 1+max ? Slice : Rollback→Failed(5) |
| Apply | ShapeRejected (멤버 모양 검사, D14) | 시도 < 1+max ? Slice : Rollback→Failed(1) |
| Apply | NotFound (멤버 모양 검사에서 원본에 대상이 없음, M2a T7) | Rollback→Failed(3) |
| Apply | FileChangedExternally | Rollback→Failed(4) |
| Verify | Ok | Review |
| Verify | NewErrors | 시도 < 1+max ? Slice : Rollback→Failed(1) |
| Review | Approved (또는 `--yes`) | Done(0) |
| Review | Rejected | Rollback→Failed(2) |
| 임의 | Cancelled (Ctrl+C) | Rollback→Failed(2) |
| 임의 | 처리되지 않은 예외 | Rollback→Failed(4) |

* "시도"는 EDIT 단계 진입 횟수입니다. 최초 1회 + 교정 최대 `maxRetries`(기본 3)회.
* `--dry-run`은 첫 APPLY 직전에 diff를 보여주고 Done(0)으로 끝납니다(파일 변경 없음, 빌드 검증 없음).
* SQL 안전 가드 위반은 모델 출력 문제이므로 교정 기회를 주되, 최종 실패 시 종료 코드 5입니다.
* SQL 파싱 오류는 안전 위반이 아니라 `MatchFailed`입니다(APPLY, 전이표 변경 없음). 파싱되지 않은 텍스트는 안전 가드로 검사할 수 없으므로 쓰기 전에 걸러 교정 기회를 주고, 소진하면 종료 코드 1입니다 (M2b R5).

### 7.2 오케스트레이터 골격
```csharp
public async Task<int> RunAsync(RunRequest req, CancellationToken ct)
{
    await using var lockHandle = workspaceLock.Acquire(ws);           // D12
    var ctx = new RunContext(...);
    try
    {
        while (!ctx.Phase.IsTerminal)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await phases[ctx.Phase].ExecuteAsync(ctx, ct);
            log.Phase(ctx, outcome);
            ctx.Phase = Transitions.Next(ctx.Phase, outcome, ctx.Attempt, opts.MaxRetries);
        }
    }
    catch (OperationCanceledException) { ctx.MarkCancelled(); }
    catch (Exception ex) { ctx.MarkCrashed(ex); }
    finally
    {
        if (ctx.NeedsRollback) await fileGuard.RestoreAsync(ctx, CancellationToken.None); // 취소 토큰 무시
        await log.CompleteAsync(ctx);
    }
    return ctx.ExitCode;
}
```
각 단계는 `IPhase` 구현 클래스(`PreflightPhase`, `LocatePhase`, …)이며 서로 직접 호출하지 않습니다. v2의 `SpecRunner`는 이 `RunAsync`를 작업마다 호출하는 방식으로 확장합니다.

### 7.3 Baseline 병렬화 (D8, D25)
1. LOCATE가 대상 멤버와 프로젝트·빌드 도구를 확정한 **끝에서** `ctx.Baseline`(원본 상태 빌드)을 **시작만** 하고 다음 단계로 갑니다. PREFLIGHT에서 시작하지 않는 이유: 대상 프로젝트(`--line`·식별자·모델 선택으로 정해짐)와 빌드 도구를 그 전에는 알 수 없고, LOCATE가 실패하면 빌드를 낭비하지 않기 위함입니다 (M2a T6).
2. SLICE → EDIT(모델 생성)가 진행되는 동안 Baseline 빌드가 돌아갑니다.
3. APPLY는 **첫 쓰기 전에** 한 번 `await ctx.Baseline`으로 완료를 기다립니다(멤버 모양 검사가 먼저 돌고, 거부되면 기다리지 않음). 결과 판정은 §8.9 BaselineSnapshot을 따릅니다.
4. `--dry-run`은 빌드 도구까지 확인하지만 Baseline을 시작하지 않습니다.
5. 실행이 끝나면(성공·실패·취소 모두) 오케스트레이터가 `finally`에서 Baseline을 취소해 프로세스 트리를 종료하고 최대 10초 기다린 뒤 롤백과 잠금 해제를 합니다. 10초 안에 멈추지 않으면 "원본 상태 빌드가 10초 안에 멈추지 않았습니다. 남은 빌드 프로세스가 있는지 확인하세요" 알림을 남깁니다 (M2a T6).

---

## 8. 구성 요소 설계

### 8.1 Workspace·설정
* **워크스페이스 루트:** 대상 파일에서 위로 올라가며 `.git`, `.svn` 폴더 또는 `*.sln` 파일이 처음 나오는 폴더. 없으면 대상 파일 폴더.
* **`.pelly/` 폴더:** 루트 아래 `.pelly/runs/`, `.pelly/backup/`, `.pelly/lock`. 최초 생성 시 `.pelly/.gitignore`(`*`)를 만들어 형상 관리에서 자동 제외.
* **설정 병합 순서:** 내장 기본값 → 사용자 설정(`%APPDATA%\pelly\config.json` / `~/.config/pelly/config.json`) → `<루트>/pelly.json` → 환경 변수(`PELLY__LLM__ENDPOINT` 형식) → CLI 옵션.
* **설정 스키마 (기본값):**
```jsonc
{
  "llm": {
    "endpoint": "http://localhost:11434",
    "model": "qwen3.6:35b-a3b",          // 폐쇄망 서버와 같은 태그로 변경
    "keepAlive": "30m",
    "numCtx": 8192,
    "headers": {},                       // 인증 프록시용
    "connectTimeoutSeconds": 3,
    "firstTokenTimeoutSeconds": 120,
    "streamIdleTimeoutSeconds": 10,
    "template": null,                    // null이면 내장 template.json 사용
    "thinkMode": "retry"                 // D21: off | retry | always
  },
  "budget": { "inputTotal": 4096, "system": 600, "request": 200, "context": 3000, "feedback": 296,
              "think": 512, "codeOutput": 2048 },
  "sampling": {
    // think: 모델 카드 "Thinking, 정밀 코딩" 값 = Ollama Modelfile 기본값 (S3)
    "think": { "temperature": 0.6, "topP": 0.95, "topK": 20, "repeatPenalty": 1.0, "presencePenalty": 0.0 },
    // code: S3에서 0.2/0.95/1.0과 차이 없음 → 결정론 쪽 값 유지
    "code":  { "temperature": 0.1, "topP": 0.9,  "topK": 20, "repeatPenalty": 1.05, "presencePenalty": 0.0 }
  },
  "retry": { "max": 3 },
  "build": { "msbuildPath": null, "timeoutSeconds": 120, "warnAsError": false },
  "test": { "command": null, "projects": [], "timeoutSeconds": 300 },   // projects: 비어 있으면 D18 규칙으로 탐색 (M3a R11)
  "sql": { "allowedHosts": [], "allowedDatabases": [], "compatLevel": 160,
           "connectionString": null },            // D17: 비밀번호 없는 연결만 허용. allowedHosts/allowedDatabases가 비면 아무 데도 접속하지 않음 (D27)
  "log": { "retentionDays": 30 }
}
```
* 설정 로드 시 검증 실패(예산 합계가 `numCtx` 초과, 알 수 없는 키)는 종료 코드 4와 함께 어떤 키가 문제인지 출력합니다.
* **M3b 튜닝 결과 (M3b T6, `docs/m3b-tuning.md`):** 위 기본값(`budget.think` 512, `sampling.code` 0.1/0.9/20/1.05)은 **바뀌지 않았습니다.** 미리 정한 후보(think 768, code 샘플링 0.2/0.95, DTO 예시 보강)를 50건 × 3회로 쟀고 R4 채택 규칙을 만족한 것이 없습니다(§10.6).
* **`sql` 설정 검증과 연결 문자열 (M2b T3·T6):**
  * `sql.compatLevel`은 `90, 100, 110, 120, 130, 140, 150, 160, 170` 중 하나여야 합니다("sql:compatLevel 값은 90, 100, …, 170 중 하나여야 합니다").
  * **비밀번호 (D17):** 설정에서 온 `sql.connectionString`에 `Password`/`Pwd` 키가 있으면 종료 4와 "sql:connectionString에 비밀번호가 있습니다 — 비밀번호가 있는 연결은 환경 변수 PELLY_SQL_CONNECTION으로만 지정하세요". 키는 `DbConnectionStringBuilder`로 해석해 대소문자를 구분하지 않고, 파싱이 안 되는 문자열도 정규식으로 비밀번호 키를 찾습니다. 로그·메시지·doctor에서는 비밀번호 값을 `***`로 가립니다(파싱이 안 되면 문자열 전체를 가림).
  * **`PELLY_SQL_CONNECTION` 우선 (R3):** 이 환경 변수(접두사 `PELLY__`와 별개)가 비어 있지 않으면 `sql.connectionString`보다 우선하고, 비밀번호가 있어도 됩니다. `dotnet user-secrets`는 지원하지 않습니다.
  * **허용 목록 (R4, D27):** 접속 전에 연결 문자열의 서버(`tcp:`/`np:`/`lpc:` 접두사·포트·인스턴스 이름을 뗀 호스트 또는 인스턴스 포함 이름)가 `allowedHosts`에, 데이터베이스가 `allowedDatabases`에 있어야 합니다(대소문자 무시, 빈 항목은 무시). 둘 중 하나라도 비어 있거나 없으면 SafetyRejected(5, FailureKind `SqlTargetNotAllowed`)이고 "… sql.allowedHosts에 추가하세요"/"… sql.allowedDatabases에 추가하세요"로 안내합니다. 서버 키(`Server`, `Data Source`, `Address`, `Addr`, `Network Address`)나 데이터베이스 키(`Database`, `Initial Catalog`)가 둘 이상이거나 `Failover Partner`가 있으면 허용 목록 밖으로 바뀔 수 있으므로 거부합니다(종료 5). 연결 문자열에 `Database`가 없으면 설정 오류(4)입니다.

### 8.2 LLM 계층
* **M3b 튜닝 결과 (M3b T6, R4):** 채택한 후보가 없어 프롬프트 버전(`cs-v2`·`sql-v1`), 샘플링, think 예산은 M3a 그대로입니다. 후보 1(시스템 프롬프트에 「요청한 동작을 없애지 말 것」 한 줄)은 M3a T14에서 이미 더 나쁘게 나왔고(회피 8/14, 되돌림) 벤치마크가 `--test`를 쓰지 않아 KPI를 움직일 수 없어서, 측정 전에 후보에서 뺐습니다(M3b 계획 단계 판단 P5). 후보 2·3·4는 각각 +0.00 / −0.67 / −0.33으로 거절됐습니다. 남은 실패는 1차 시도에서 종료 0으로 끝난 뒤 판정에서 떨어지는 경계값 실수라 교정 경로(think·피드백)가 개입하지 않습니다.

#### OllamaBackend
* `POST {endpoint}/api/generate` 요청 본문:
```json
{ "model": "...", "prompt": "<RawPrompt>", "raw": true, "stream": true, "keep_alive": "30m",
  "options": { "num_ctx": 8192, "num_predict": 512, "temperature": 0.6, "top_p": 0.95, "top_k": 20,
               "repeat_penalty": 1.0, "presence_penalty": 0.0, "stop": ["</think>"] } }
```
* 응답은 NDJSON. 줄마다 `response`(텍스트), `done`, 마지막 줄의 `prompt_eval_count`, `prompt_eval_cached_count`, `eval_count`, `prompt_eval_duration`, `done_reason`을 `LlmChunk`로 변환합니다. 알 수 없는 필드는 무시합니다(버전 차이 대비). raw 모드에서는 생각 텍스트도 `response`로 옵니다(S1 확인 — `thinking` 필드는 오지 않음).
* 로그에는 "새로 처리한 입력 토큰 = `prompt_eval_count` − `prompt_eval_cached_count`"를 함께 기록합니다(S2).
* `HttpClient`는 `HttpCompletionOption.ResponseHeadersRead`로 스트림을 읽습니다. 취소 토큰이 취소되면 응답 스트림을 닫습니다. 단, 서버는 끊긴 요청을 바로 멈추지 않아 다음 요청이 약 2초 늦어지므로(S2), **취소는 사용자 Ctrl+C에만** 쓰고 길이 제한은 `num_predict`로 합니다(D22).
* `generate`는 첫 NDJSON 줄까지 `firstTokenTimeoutSeconds`(기본 120초)를 기다리고(무응답 시 1회 재요청, 그래도 실패하면 EnvError), 이후 줄 사이 무응답은 `streamIdleTimeoutSeconds`로 끊습니다. `connectTimeoutSeconds`는 상태 조회(`GetStatusAsync`) 호출의 타임아웃이자 TCP 연결 타임아웃입니다. Ollama는 모델이 로드될 때까지 응답 헤더를 보내지 않으므로(S1 콜드 로드 25.7초) 첫 줄 대기에 별도 값이 필요합니다.
* `GetStatusAsync`: `/api/version`, `/api/tags`(모델 존재), `/api/ps`(로드 여부), `/api/show`(Modelfile의 `RENDERER`/`PARSER` 이름, 기본 파라미터). 이 모델은 Go 템플릿 대신 내장 렌더러(`qwen3.5`)를 써서 `template`이 `{{ .Prompt }}`뿐이므로 템플릿 문자열 비교는 하지 않습니다(S1).
* **서버 대기(공용 서버) 시간 (M3a T2, R3):** `QueueSeconds = max(0, 클라이언트 TTFT − load_duration − prompt_eval_duration)`로 계산해 `summary.json`과 리포트에 기록하고, `TtftSeconds`에는 대기를 뺀 값을 남깁니다(PRD §5 "대기는 TTFT와 분리"). 첫 청크가 3초 안에 오지 않으면 `QwenStreamClient`가 1초마다 대기 콜백(`onWaiting`)을 불러, `EditPhase`가 "서버 응답 대기 중 (N초) — 다른 요청을 처리 중이거나 모델을 불러오는 중일 수 있습니다"를 표시합니다. 단독 사용이면 대기 ≈ 0입니다(M2b 기준선 재측정 0.02~0.03초). 첫 토큰 생성 시간 같은 작은 오차가 대기로 잡힐 수 있습니다.

#### QwenChatTemplate (D10)
내장 `template.json`:
```json
{
  "systemStart": "<|im_start|>system\n", "systemEnd": "<|im_end|>\n",
  "userStart": "<|im_start|>user\n", "userEnd": "<|im_end|>\n",
  "assistantStart": "<|im_start|>assistant\n",
  "thinkOpen": "<think>\n", "thinkClose": "\n</think>\n\n",
  "endOfTurn": "<|im_end|>"
}
```
`Build(system, user)` → `systemStart+system+systemEnd+userStart+user+userEnd+assistantStart`. **S1에서 확정**(`docs/spikes/S1-template.json`): 이 조립 결과의 토큰 수가 `/api/chat` 내장 렌더러 결과와 같았습니다(50 = 50). `</think>`·`<|im_end|>`는 단일 특수 토큰입니다. 설정 `llm.template`로 파일 경로를 주면 덮어씁니다.

#### QwenStreamClient — 2단계 생성
```text
basePrompt = template.Build(system, user)

useThink = thinkMode == always || (thinkMode == retry && attempt >= 2)     // D21

[THINK]  (useThink일 때만)
  req1 = basePrompt + thinkOpen ; sampling.think ; num_predict = budget.think ; stop = ["</think>"]   // D22
  응답에 "</think>"가 있으면 그 앞까지만 사용 (stop이 무시될 수 있음 — D23)
  없고 done_reason == "length" 이면 잘린 것 → 마지막 '\n' 이전까지만 남김 (잘린 줄 제거)
  thinkText = 결과 (끝 공백 제거), thinkTokens = eval_count (서버 값)

[CODE]
  prefill = useThink ? thinkOpen + thinkText + thinkClose : thinkOpen + thinkClose     // 빈 think = "<think>\n" + "\n</think>\n\n"
  req2 = basePrompt + prefill + "<<<<<<< SEARCH\n" ; sampling.code ; num_predict = budget.codeOutput ; stop = ["<|im_end|>"]
  output = "<<<<<<< SEARCH\n" + 스트림 전체
```
* 두 요청은 `basePrompt`가 같아 Ollama가 접두사 KV 캐시를 재사용합니다. **S2 측정:** 2단계 TTFT 중앙값 299ms, 새로 처리한 입력 514토큰(생성한 think 토큰은 재사용되지 않고 다시 처리됨).
* 시스템 프롬프트·few-shot은 항상 프롬프트 맨 앞에 고정합니다. 실행 사이에도 그 부분이 캐시됩니다.
* 결과 `GenerationResult(ThinkText, ThinkTokens, ThinkTruncated, Output, Timings, ServerCounts)`를 로그에 남깁니다.
* LOCATE의 "모델이 후보 고르기"는 생각 없이 단일 요청: `basePrompt + thinkOpen + thinkClose`(빈 think) 프리필, `sampling.code`, `num_predict = 32`, 출력 첫 줄을 후보 목록과 정확히 비교.

#### TokenCounter
* **`QwenTokenCounter` 확정 (S4: 샘플 50개, 13,368토큰에서 서버와 오차 0%).** 용도는 입력 예산 계산입니다(think 예산은 서버 `eval_count`, D22).
  ```csharp
  var options = new BpeOptions(vocabStream, mergesStream)   // 내장 vocab.json, merges.txt
  {
      ByteLevel = true,
      SpecialTokens = meta.SpecialTokens,                     // tokenizer-meta.json (tokenizer.json의 added_tokens 26개)
      PreTokenizer = new RegexPreTokenizer(new Regex(meta.PreTokenizerRegex, RegexOptions.Compiled), meta.SpecialTokens),
      Normalizer = new NfcNormalizer(),                       // string.Normalize(NormalizationForm.FormC)
  };
  BpeTokenizer.Create(options);   // 로드 약 0.4초 → 프로세스당 1회, 지연 초기화
  ```
  `tokenizer-meta.json`은 빌드 시점이 아니라 저장소에 커밋된 작은 파일입니다(모델의 `tokenizer.json`에서 특수 토큰과 Split 정규식만 뽑음). `BpeOptions`가 스트림 생성자를 제공하지 않으면 임시 파일로 풀어 로드합니다.
* 서버 카운트와의 차이는 매 실행 로그에 기록합니다(`tokens.local` vs `tokens.server`). 0이 아니면 모델·토크나이저 불일치 신호입니다.

#### Prompts
내장 리소스 `prompts/system.cs.txt`, `prompts/system.sql.txt`, `prompts/fewshot.cs.txt`, `prompts/fewshot.sql.txt`, `prompts/locate.txt`. 설정으로 경로를 주면 덮어씁니다(폐쇄망 튜닝용). 프롬프트 파일 첫 줄에 `# version: N`을 두고 로그에 기록합니다.

시스템 프롬프트 요지(C#):
* 너는 하나의 C# 멤버만 고치는 편집기다. 출력은 SEARCH/REPLACE 블록만, 설명 금지.
* SEARCH는 `[EDITABLE]` 표시 구간의 원문을 **그대로** 복사한다. `[READ-ONLY]` 구간은 참고만 한다.
* 대상 언어 버전: `{LanguageHint}`. 그보다 새로운 문법 금지.
* 짧은 예시 1개(약 250토큰).

사용자 메시지 구성(섹션 순서 고정):
```text
### TARGET
OrderService.Cancel(int orderId)  — file: OrderService.cs
### CONTEXT
[READ-ONLY] usings / fields / other member signatures / DTOs ...
[EDITABLE: usings]
using System; ...
[EDITABLE: target]
public void Cancel(int orderId) { ...원문 그대로... }
### REQUEST
<사용자 요청 원문>
### FEEDBACK            (재시도일 때만)
<최신 피드백 1건>
```
컨텍스트에 줄 번호를 붙이지 않습니다(모델이 SEARCH에 줄 번호를 복사하는 것을 막기 위함).

### 8.3 ContextBudget
* 입력: 각 섹션 텍스트. 출력: 예산 안에 맞춘 최종 프롬프트 또는 `BudgetExceeded(섹션, 필요량, 한도)`.
* 규칙: 요청 > `budget.request` → 실패(4). 피드백 > `budget.feedback` → 피드백 생성기가 앞 에러부터 잘라서 맞춤. 컨텍스트는 슬라이서가 우선순위대로 제거(§8.5). `target` 파트 단독으로 `budget.context` 초과 → 실패(4).
* 합계 검증은 **최종 조립된 raw 프롬프트 전체**를 토큰 카운터로 다시 세어 `budget.inputTotal` 이하인지 확인합니다(템플릿 토큰 포함).

### 8.4 SymbolLocator
1. 파일을 Roslyn으로 파싱(언어 버전은 §8.5 감지값).
2. `--line <n>` 지정 (IDE External Tools용, D19): 줄 `n`을 포함하는 가장 안쪽의 메서드·생성자·프로퍼티(접근자 안이면 그 프로퍼티)·연산자를 대상으로 합니다. 지역 함수·람다 안이면 그것을 감싼 멤버입니다. 어떤 멤버에도 속하지 않는 줄(필드, using, 클래스 선언 줄)이면 NotFound(3)와 "`N`번 줄은 메서드 안이 아닙니다. 메서드 안에 커서를 두고 실행하세요" 메시지(FailureKind `TargetNotFound`). `--line`은 `--class`/`--method`와 함께 쓸 수 없고(설정 오류 4: "--line은 --class/--method와 함께 쓸 수 없습니다") 1 이상이어야 합니다("--line은 1 이상이어야 합니다") (M2a T8, T9). SQL의 `--line`은 그 줄을 포함하는 배치의 객체를 대상으로 합니다.
3. `--class`/`--method` 지정: 이름 정확 일치(대소문자 구분). 오버로드가 여러 개면 후보 다수.
4. 미지정: 요청문에서 식별자 후보 추출 — 정규식 `` `([^`]+)` ``, `"([^"]+)"`, `\b[A-Z][A-Za-z0-9_]+\b`. `\b`는 ASCII 단어 경계(ECMAScript 옵션)여서 `CancelAsync에`처럼 한글 조사가 붙어도 이름이 끝납니다. 따옴표·백틱 안의 `A.B`는 마지막 조각 `B`도 후보로 하고, 따옴표 구간 안의 PascalCase는 따로 더하지 않습니다(`` `OrderService.CancelAsync` ``가 `OrderService` 생성자를 가리키지 않게). 파일의 메서드·생성자·프로퍼티 이름과 일치하는 것만 남김 (M2a T8).
5. 후보 0개: `--class` 필터를 적용한 멤버 시그니처 목록을 모델에 주고 하나 고르게 함(§8.2, `locate.txt` v1, `NumPredict` 32, 응답은 `<|im_end|>`에서 자름). 멤버가 **60개를 넘으면** 모델을 부르지 않고 NotFound(3)와 "멤버가 60개를 넘어 자동으로 고를 수 없습니다. --method나 --line을 쓰세요". 목록에 없는 답이면 NotFound("요청문에서 대상 멤버를 찾지 못했습니다. --method나 --line을 쓰세요"). 모델이 고르면 "대상: … (모델이 선택)" 알림. 모델 서버 장애는 EnvError(4) (M2a T8).
6. 후보 2개 이상: `IsInteractive`면 선택 UI, 아니면 Ambiguous(3, FailureKind `TargetAmbiguous`).
* **재시도 때 재탐색:** 교정 시도에서 파일이 바뀐 뒤에는 포함 타입과 이름으로 대상을 다시 찾되, 오버로드가 여럿이면 처음의 Display와 정확히 같은 것을 우선합니다 (M2a T8).
* **요청문 입력:** `pelly cs <파일> --line <n>`처럼 요청문 없이 실행하면(External Tools 사용 시) 대화형 콘솔에서 "수정 요청을 입력하세요:"로 입력받습니다. 비대화형이면 설정 오류(4). `--method`·요청문은 모두 선택입니다 (M2a T9).
* SQL (M2b T1·T7): 파일을 `GO` 줄(대소문자 무시, 앞뒤 공백 허용, `GO <숫자>` 포함)로 배치로 나눈 뒤 ScriptDom으로 각 배치를 파싱합니다. 공백뿐인 조각(마지막 `GO` 뒤, 연속된 `GO` 사이)은 배치로 세지 않습니다(R-P1). `CREATE/ALTER/CREATE OR ALTER PROCEDURE|FUNCTION|VIEW` 배치가 정확히 1개면 그것이 대상입니다. 0개면 NotFound("파일에 프로시저·함수·뷰 정의가 없습니다", 구문 오류가 있는 배치의 줄 범위를 덧붙임), 2개 이상이면 `--object`(또는 `--line`)로 지정해야 합니다: 대화형이면 선택 UI, 비대화형이면 Ambiguous(3, FailureKind `TargetAmbiguous`, "객체가 N개입니다. --object로 지정하세요: …"). **모델 선택(위 5단계)은 SQL에 넣지 않습니다** (M2b R8). `--line`은 그 줄을 포함하는 객체 배치(정의 안이 아니면 NotFound), `--object`는 이름이 같은 배치(없으면 NotFound)입니다. `--line`과 `--object`를 함께 주면 설정 오류(4)입니다. `GO` 분리는 문자열·주석 안의 `GO`를 무시하도록 ScriptDom 토큰 스트림 기준으로 합니다.
* **편집할 수 없는 원본 대상 (M2b T7, R-T3):** 고른 원본 대상 배치에 구문 오류가 있거나, 객체 이름이 없거나, 최상위 문장이 둘 이상이면 NotFound(3)로 끝납니다("대상 객체 배치에 정의 외의 문장이 있거나 구문 오류가 있어 편집할 수 없습니다 (줄 a-b)"). VERIFY가 원본 배치를 컴파일해 기준선을 만들기 때문입니다. 원본의 금지 구문은 §8.10을 보십시오.
* `EditTarget.ProjectPath`: 대상 파일에서 위로 올라가며 처음 나오는 `*.csproj`. 레거시 csproj가 `<Compile Include>`로 파일을 나열하는 경우, 그 목록에 대상 파일이 있는지 확인(없으면 경고).

### 8.5 RoslynSlicer
* **언어 버전 감지:** csproj의 `<LangVersion>` → 없으면 `TargetFramework(s)`로 추정 → 레거시 csproj(SDK 속성 없음)는 `<LangVersion>`이 없으면 7.3. `CSharpParseOptions(LanguageVersion)`에 반영하고 `Slice.LanguageHint`로 프롬프트에 전달.

  | TargetFramework | 기본 C# 버전 |
  | :--- | :--- |
  | `net4*`, `netstandard2.0` | 7.3 |
  | `netcoreapp2.x` | 7.3 |
  | `netcoreapp3.x`, `netstandard2.1` | 8.0 |
  | `net5.0` / `net6.0` / `net7.0` / `net8.0` / `net9.0` / `net10.0` | 9 / 10 / 11 / 12 / 13 / 14 |

  여러 타겟(`TargetFrameworks`)이면 가장 낮은 버전을 씁니다. `LangVersion`이 `latest`/`preview`면 SDK 기본값으로 봅니다.
* **Directory.Build.props (M2a T4):** csproj에 없는 속성(`LangVersion`, `TargetFramework(s)`, 레거시의 `TargetFrameworkVersion`)은 csproj 폴더에서 위로 올라가며 **처음 만나는** `Directory.Build.props` 하나에서 읽습니다(MSBuild처럼 가까운 것이 이기고 연쇄 임포트는 하지 않음). `Condition`이 붙은 `PropertyGroup`/속성은 평가하지 않고 무시하며, 같은 속성이 여럿이면 마지막 값을 씁니다. csproj의 값이 props보다 우선하고, csproj 안에서는 `Condition`을 보지 않고 처음 나온 값을 씁니다. props의 XML이 깨졌으면 파일 경로를 담은 EnvError(4).
* **API 표면 힌트:** `.NET Framework 4.5`처럼 오래된 타겟이면 `LanguageHint`에 타겟 프레임워크도 함께 넣습니다(예: `C# 7.3, .NET Framework 4.5`). 해당 버전에 없는 API를 쓰면 빌드가 잡아내므로 별도 검사는 하지 않습니다.
* **파트 구성과 우선순위** (숫자가 낮을수록 먼저 제거):

  | 파트 | 내용 | 우선순위 |
  | :--- | :--- | :--- |
  | target | 대상 멤버 원문(앞쪽 XML 주석·특성 포함) | 제거 불가 |
  | usings | 파일의 using 지시문 원문 | 3 |
  | fields | 같은 클래스의 필드·생성자 시그니처(DI 파악용) | 4 |
  | dto | 대상 멤버가 쓰는 타입 중 프로젝트 안에 선언된 타입의 데이터 멤버 목록(프로퍼티·public 필드·enum 멤버, 아래 "DTO 찾기") | 2 |
  | signatures | 같은 클래스의 다른 멤버 시그니처(본문은 `{ ... }`로 대체) | 1 |
* **DTO 찾기 (M3a T7, R5):** `DtoCollector`가 만듭니다.
  * 후보 이름: 대상 멤버의 `IdentifierNameSyntax`/`GenericNameSyntax`에, 대상을 품은 타입의 필드·프로퍼티 타입 중 대상이 쓰는 것을 더합니다(본문이 `var order = …`라 이름이 안 보이는 `CancelAsync`의 `Order`를 찾기 위함). 1회차에서 찾은 타입이 데이터 멤버가 없으면(저장소 인터페이스 등) 대상이 그 타입에 호출한 메서드의 시그니처에 나온 타입을 한 단계만 더 따라갑니다(스캔은 최대 2회).
  * 스캔: 프로젝트 폴더의 `*.cs`를 텍스트로 훑어 `class|record|struct|interface|enum <이름>\b`가 있는 파일만 파싱합니다. 건너뛰는 폴더는 `bin`, `obj`, `.pelly`, `.git`, `.vs`, `node_modules`, `packages`, `TestResults`이고, 링크·재해석 지점은 따라가지 않으며, 한 번의 스캔은 디렉터리 2000개(`MaxDirectories`)·파일 5000개·파일당 1MiB에서 멈춥니다. 같은 이름의 `packages`·`TestResults` 폴더에 있는 DTO는 찾지 못합니다. 같은 소스에 대한 결과는 캐시합니다(재시도마다 다시 훑지 않음).
  * 렌더 형식: 타입마다 한 줄 — `class Order: OrderStatus Status; decimal Total`, `record …`, `struct …`, `interface …`, `enum OrderStatus: Pending, Shipped, …`. 프로퍼티와 public/internal 인스턴스 필드(`const`·`static` 제외)를 담고, 레코드의 위치 매개변수도 포함합니다. 최대 5개 타입, 타입당 최대 30개 멤버(넘으면 끝에 `…`). 읽기·파싱 실패 파일은 건너뛰며 스캔 전체가 실패해도 dto 파트만 없을 뿐 실행은 계속됩니다.
* **편집 가능 영역(D7):** `target` 범위와 `usings` 범위를 `EditableRegion`으로 반환합니다. using이 없는 파일은 파일 첫 줄 위치를 빈 영역으로 둡니다(이 경우 using 추가는 첫 `namespace` 줄을 SEARCH로 써야 하므로 namespace 선언 줄도 usings 영역에 포함).

### 8.6 SqlSchemaSlicer
1. ScriptDom으로 대상 객체 본문 파싱 → `NamedTableReference`, `SchemaObjectName`을 방문자로 수집. `#temp`, `@table` 제외.
2. 객체가 DB에 이미 있으면 `sys.dm_sql_referenced_entities('<schema.name>', 'OBJECT')` 결과와 합칩니다.
3. 테이블별로 `sys.columns`(이름, 타입, 길이, null 여부)와 `sys.indexes`(인덱스 이름, 키 컬럼)를 조회해 요약 DDL로 만듭니다.
   ```text
   TABLE dbo.Orders (OrderId int NOT NULL PK, Status tinyint NOT NULL, ShippedAt datetime2 NULL, ...)
   INDEX IX_Orders_Status (Status)
   ```
4. 예산 초과 시 아래 순서로 줄입니다(M2b T8). 각 단계는 **마지막 테이블부터** 하나씩 줄이다가 예산 안에 들어오면 멈춥니다. 대상 객체 배치는 줄이지 않으며, 대상 혼자서 예산을 넘으면 스키마 전체를 뺍니다(`SliceResult.TargetTooLarge`, SLICE가 BudgetExceeded→종료 4).
   1. 모든 인덱스 줄 (`Omitted` 종류 `schema-indexes`)
   2. 기본 키도 아니고 대상이 참조하지도 않는 컬럼 (`schema-columns`). **이 단계에서 컬럼이 전부 사라질 테이블은 건드리지 않고** 빈 컬럼 목록이 모델을 오도하지 않게 3단계로 넘깁니다.
   3. 테이블 줄 통째 (`schema-tables`)
   4. 대상 혼자서 예산 초과 (`schema`)
   `Omitted`에 올라간 줄은 `Slice.Omitted`에 종류별로 기록됩니다. 컬럼 줄은 `TABLE|VIEW 이름 (컬럼 타입 NULL|NOT NULL [PK], …)`, 인덱스는 `[UNIQUE ]INDEX 이름 (키 컬럼, …)` 형식입니다. 참조 수집은 같은 DB 객체만 대상이며(3·4부 이름은 R7에 따라 제외) 테이블과 뷰가 아닌 참조는 스키마 요약에서 조용히 빠집니다.
5. 편집 가능 영역은 **대상 객체 배치 범위**입니다(D15). 같은 파일의 다른 배치(`SET ANSI_NULLS ON`, 다른 객체, 권한 부여 등)는 `[READ-ONLY]`로도 넣지 않습니다.

### 8.7 SearchReplaceParser·Patcher
* **파서:** 줄 단위 상태 기계. 마커는 줄 전체가 `<<<<<<< SEARCH`, `=======`, `>>>>>>> REPLACE`와 일치(끝 공백 허용). 블록 밖 텍스트는 무시. 마지막 블록에 `>>>>>>> REPLACE`가 없으면 Parse 실패(출력 잘림). SEARCH가 비어 있으면 Parse 실패. 블록 0개면 Parse 실패.
* **정규화:** 원본과 블록을 모두 `\n` 줄 배열로 바꿔 비교합니다(원본 줄바꿈은 FileGuard가 보존).
* **매칭 (블록마다, 앞 블록을 적용한 텍스트에 순서대로):**
  1. Exact: 줄 배열 완전 일치
  2. TrailingWhitespace: 각 줄 `TrimEnd()` 비교
  3. Indentation: 각 줄 `Trim()` 비교(빈 줄은 빈 줄끼리만 일치)
  * 각 단계에서 **편집 가능 영역 안에 완전히 포함된 일치만** 셉니다. 영역 밖에서만 일치하면 OutsideRegion 실패.
  * 일치 1개 → 적용. 2개 이상 → Ambiguous 실패. 0개 → 다음 단계. 3단계까지 0개 → NotFound 실패.
* **들여쓰기 보정 (3단계 일치 시):** SEARCH의 첫 비어 있지 않은 줄과 원본 해당 줄의 앞 공백 차이(문자열)를 구해, REPLACE의 모든 비어 있지 않은 줄에 더하거나 뺍니다. 파일이 탭 들여쓰기면 탭 기준으로 계산합니다.
* **편집 가능 영역 갱신:** 블록 적용 후 줄 수 변화만큼 뒤쪽 영역 범위를 이동합니다.
* **변화 없음 가드 (M3a T14):** C# APPLY(대상 멤버 텍스트)와 SQL APPLY(대상 배치)는 패치한 대상을 **원본** 대상과 공백 정규화(연속 공백을 한 칸으로) 후 비교해 같으면 쓰기 전에 `MatchFailed`, FailureKind `NoChange`, 피드백 `NO CHANGE: the edit leaves the target identical to the original. Make the requested change.`로 거부합니다. 이전 시도가 아니라 원본과 비교하므로 2번째 시도가 1번째를 되돌리는 경우도 잡습니다. 문자열 리터럴 안의 공백도 접어 비교하므로 리터럴의 공백만 고친 수정도 거부됩니다(받아들인 한계). 요청의 정답이 "변경 없음"인 경우는 종료 1이 됩니다. 주석 처리·로그로 대체 같은 의미상 회피는 잡지 못합니다(§8.9 4번).
* **NearestOriginal (NotFound 피드백용):** 편집 가능 영역 안에서 SEARCH 첫 줄과 `Trim()` 기준 편집 거리가 가장 가까운 줄을 찾아 ±3줄을 돌려줍니다.
* **마커 충돌:** 원본 파일에 마커 줄이 이미 있으면 PREFLIGHT에서 EnvError(4).

#### 8.7.1 MemberShapeGuard (C#, APPLY 단계에서 파일 쓰기 전, D14)
수정 전·후 파일을 Roslyn으로 파싱해 모든 타입의 멤버를 (네임스페이스·타입 경로, 종류, 표시 이름) 키로 비교합니다. **비교 기준은 그 시도의 직전 쓰기가 아니라 실행의 원본 텍스트**입니다(APPLY가 원본을 넘김). 그래서 앞선 시도에서 모델이 더한 도우미가 이후 시도에서 "추가된 멤버"로 계속 허용되고 편집 가능합니다 (M2a T7).
* **대상 멤버 시그니처(변경 불가):** 매개변수 타입·`ref`/`out`/`in`/`params`/`this` 한정자·기본값(이름은 제외), 반환 타입, 제네릭 매개변수·제약, 명시적 인터페이스, 프로퍼티는 접근자와 그 한정자, 그리고 `async`를 뺀 모든 한정자(접근 수준·`static`·`virtual` 등). `async`는 반환 타입이 같을 때만 추가·제거할 수 있습니다. 본문, XML 주석, 지역 함수는 자유롭게 바꿀 수 있습니다.
* **특성:** 대상 멤버와 그 매개변수의 attribute는 바꿀 수 없습니다.
* **다른 멤버:** 선행 trivia를 뺀 텍스트를 공백만 접어 비교해, 사라지거나 바뀌면 거부합니다(편집 가능 영역 제한이 대부분 막지만 마지막 방어선).
* **추가 멤버:** 대상과 같은 타입 범위의 `private` 메서드만 허용합니다(접근 한정자가 없으면 class·struct·record에서만 private으로 봄. `private protected`, 인터페이스 멤버, 명시적 인터페이스 구현은 제외). 필드·프로퍼티·다른 접근 수준 메서드·중첩 타입 추가는 거부합니다. 허용된 도우미는 `ctx.AddedHelpers`에 쌓여 다음 시도의 프롬프트에 `[EDITABLE: helper]` 구간으로 실립니다(시스템 프롬프트 규칙 3에 명시, 프롬프트 버전 `cs-v2`) (M2a T7).
* **using:** 추가만 허용, 삭제는 거부합니다.
* **원본에 대상이 없으면**(Display 불일치) 검사하지 않고 NotFound(3, FailureKind `TargetNotFound`), "대상 멤버 '…'을(를) 원본에서 찾을 수 없습니다".
* **거부 사유 문구** (피드백은 `SHAPE REJECTED: ` + 사유를 `budget.feedback` 안으로 자름. FailureKind `ShapeRejected`, 종료 코드는 재시도 소진 시 1):
  * 시그니처 변경, Display가 달라짐: `signature of Cancel(int) changed to Cancel(int, bool). Signature changes are not allowed.`
  * 시그니처 변경, Display는 같음(반환 타입·기본값·한정자 등): `signature of <Display> changed from `<h1>` to `<h2>`. Signature changes are not allowed.` (`h1`·`h2`는 본문 앞까지의 선언부, 특성 제외·공백 정규화)
  * `attributes of <Display> changed. Attribute changes are not allowed.`
  * `member <Display> was removed or changed. Only the target member may change.`
  * `added <종류> <Display>. Only private helper methods may be added.`
  * `using <이름> was removed. Usings may only be added.`

### 8.8 FileGuard
* **읽기:**
  1. BOM 검사(UTF-8, UTF-16 LE/BE) → 해당 인코딩.
  2. BOM 없으면 `new UTF8Encoding(false, throwOnInvalidBytes: true)`로 디코딩 시도 → 성공 시 UTF-8(무 BOM).
  3. 실패하면 CP949(`Encoding.GetEncoding(949)`, Program 시작 시 `CodePagesEncodingProvider` 등록).
  4. 줄바꿈: `\r\n` 개수와 단독 `\n` 개수 중 많은 쪽. 마지막 줄 끝 줄바꿈 유무도 기록.
  * 결과 `FileSnapshot(Path, Encoding, HasBom, NewLine, EndsWithNewLine, Sha256, LastWriteUtc, Text)`.
* **쓰기:** 수정 텍스트(`\n` 기준)를 기록된 줄바꿈으로 되돌리고 같은 인코딩·BOM으로 인코딩 → 같은 폴더 `.<이름>.pelly-tmp` 에 쓰기 → Windows는 `File.Replace(tmp, path, null)`, 그 외 `File.Move(tmp, path, overwrite: true)`.
  * `IOException`(공유 위반, HResult `0x80070020`) 시 200ms 간격 최대 5회 재시도 → 실패 시 EnvError(4)와 파일 경로.
  * CP949로 표현할 수 없는 문자가 수정 텍스트에 있으면 쓰기 전에 실패(교정 피드백: "파일 인코딩 CP949에서 쓸 수 없는 문자").
* **백업:** 첫 APPLY 전에 원본 바이트를 `.pelly/backup/<runId>/<루트 기준 상대 경로>`로 복사. 복원은 바이트 그대로.
* **외부 변경 감지:** APPLY·Rollback 직전에 현재 파일 SHA-256이 "마지막으로 pelly가 쓴 내용(또는 원본)"과 같은지 확인. 다르면 덮어쓰지 않고 FileChangedExternally. Rollback 중 감지되면 복원하지 않고 백업 경로를 사용자에게 알립니다.
* **형상 관리 경고:** 루트에 `.git`이 있으면 `git status --porcelain -- <파일>`로 확인(git이 없으면 생략).

### 8.9 빌드 검증

#### BuildToolResolver
* csproj XML 루트에 `Sdk` 속성 또는 `<Sdk>` 자식이 있으면 SDK 스타일 → `dotnet`.
* 아니면 레거시 → `MSBuild.exe`:
  1. 설정 `build.msbuildPath`
  2. `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe` 결과 첫 줄
  3. 둘 다 없으면 EnvError(4) — doctor가 Build Tools 설치 안내.
* Windows가 아닌 OS의 레거시 프로젝트는 빌드하지 않고 알림 "레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증합니다 — 구문 검사만 합니다"와 함께 **구문 검사만** 합니다(오류 아님). 같은 방식으로 `ProjectStyle == None`(csproj 없음)이면 "csproj가 없어 빌드 검증을 건너뜁니다 (구문 검사만 합니다)" 알림과 함께 빌드를 생략합니다. Windows에서 MSBuild를 못 찾으면 EnvError(4, FailureKind `BuildToolUnavailable`) (M2a T6).

#### 명령
| 형식 | 명령 |
| :--- | :--- |
| SDK | `dotnet build <csproj> --no-restore --nologo -v q -clp:NoSummary;ErrorsOnly` (`--warn-as-error`면 `ErrorsOnly` 대신 경고 포함) |
| 레거시 | `MSBuild.exe <csproj> /t:Build /nologo /v:q /clp:NoSummary;ErrorsOnly /nr:true /m` |

* 환경 변수 `DOTNET_CLI_UI_LANGUAGE=en`, `VSLANG=1033`을 지정해 메시지를 영어로 고정합니다(파싱 안정성, Baseline 비교 일관성).
* 출력 인코딩: stdout을 UTF-8로 디코딩해 대체 문자(U+FFFD)가 있으면 원시 바이트를 CP949로 다시 디코딩하고(CP949에도 있으면 UTF-8 결과 유지), stdout과 stderr를 합쳐 파싱합니다.
* 빌드 서버와 MSBuild 작업자 노드(노드 재사용, 레거시의 `/nr:true`)는 성공한 실행 뒤에도 살아 있을 수 있으며, 시간 초과와 취소 때는 프로세스 트리 전체를 종료합니다 (M2a 최종 리뷰).
* SDK 기본 명령은 `-clp:ErrorsOnly`를 유지하므로 새 경고 알림("새 경고 N개: …")은 `--warn-as-error`(경고 포함 출력) 때만 실질적으로 나옵니다 (M2a T6).
* **`BuildRun`:** 빌드 한 번의 결과(`Errors`, `Warnings`, `ExitCode`, `TimedOut`, `Duration`). **파싱하지 못한 실패는 오류로 봅니다:** 종료 코드가 0이 아닌데 파싱된 오류가 하나도 없으면 합성 오류 `BUILD`("build failed with exit code N")를 더해 통과로 보이지 않게 합니다. 타임아웃은 EnvError(4)입니다 (M2a T5).

#### BuildErrorParser
```regex
^\s*(?<file>.+?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Z]{2,}\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$
```
파일 경로는 워크스페이스 루트 기준 상대 경로로 정규화(Windows는 대소문자 무시). 컴파일러는 심볼릭 링크를 푼 실제 경로를 출력하므로(macOS `/var` → `/private/var`) 루트와 그 실경로 두 가지를 모두 기준으로 삼습니다 (M2a T5). 같은 진단이 여러 번 나오면 중복 제거.

#### BaselineSnapshot
* 키: `(정규화 경로, 코드, 메시지)` — 줄 번호 제외.
* 신규 에러 = 검증 빌드 에러 − Baseline 에러(키 기준 다중집합 차감).
* **판정 (APPLY가 첫 쓰기 전에 한 번, M2a T6):**
  1. Baseline 타임아웃 → EnvError(4).
  2. CS가 아닌 오류가 하나라도 있으면(`NETSDK*`, `MSB*`, `NU*`, 합성 `BUILD`) 컴파일러가 돌지 않은 것이므로 이후 모든 빌드가 같은 이유로 실패하거나 편집이 공허하게 통과합니다. `--strict-baseline`과 무관하게 EnvError(4, FailureKind `BuildUnverifiable`)와 "빌드 검증을 할 수 없습니다: <코드> <메시지>. 프로젝트를 복원(dotnet restore 또는 Visual Studio 복원)하고 빌드되는지 먼저 확인하세요". 출력 파일 잠금(`MSB3021`·`MSB3026`·`MSB3027`)이면 대신 "빌드 출력 파일이 잠겨 있어 빌드 검증을 할 수 없습니다: <코드> <메시지>. 실행 중인 프로그램(디버거 포함)을 닫고 다시 시도하세요".
  3. CS 오류만 있으면 "원본 상태에서 이미 빌드 오류가 N개 있습니다 — 새로 생긴 오류만 검사합니다" 알림을 실행당 한 번 보이고 진행. `--strict-baseline`이면 EnvError(4, FailureKind `BaselineErrors`).
  * 신규 경고도 같은 방식으로 차감하며 `--warn-as-error`(`build.warnAsError`)일 때만 실패로 셉니다.

#### BuildVerifier 순서
1. **구문 사전 검사:** 수정된 파일을 감지된 언어 버전으로 파싱 → `GetDiagnostics()`에 Error가 있으면 빌드 없이 NewErrors로 반환.
2. **빌드** → 파싱 → Baseline 차감. 신규 오류 중 CS가 아닌 것(`MSB*`·`NETSDK*`·`NU*`·합성 `BUILD`)이 있으면 모델이 고칠 수 없는 오류이므로 재시도·피드백 없이 EnvError(4, FailureKind `BuildUnverifiable`)로 끝내고 파일을 복원합니다. 잠금 계열은 위 잠금 문구, 나머지는 "빌드 검증을 할 수 없습니다: <코드> <메시지>. 프로젝트가 빌드되는지 먼저 확인하세요" (M2a 최종 리뷰).
3. **피드백 생성(FeedbackBuilder):**
   ```text
   BUILD FAILED (2 new errors)
   OrderService.cs(41): CS0103 The name 'shippedAt' does not exist in the current context
     39 |     var order = _repo.Get(orderId);
     40 |     if (order.Status == OrderStatus.Shipped)
   > 41 |         throw new InvalidOperationException(shippedAt);
     42 |     _repo.Cancel(orderId);
   ```
   대상 파일의 에러를 먼저, 그다음 다른 파일의 에러. `budget.feedback` 안에 들어가는 만큼만.
   (피드백에는 줄 번호를 붙이지만, 다음 시도의 CONTEXT는 다시 SLICE한 원문이라 줄 번호가 없습니다.)
4. **`--test` (`TestStep`, 단계 이름 `Test`, FailureKind `TestErrors`, 진단 코드 `TEST`) (M3a T8·T9, R6·R7·R11):**
   * **테스트 프로젝트 찾기 (D18, R6):** 설정 `test.projects`가 있으면 그것, 없으면 솔루션의 프로젝트 중 이름에 `Test`가 있고 대상 프로젝트를 참조하는 것. `.sln`이 없는 작업 공간은 작업 공간의 `*.csproj`(bin/obj 제외)를 같은 규칙으로 훑습니다. 없으면 경고 후 테스트를 생략하고 빌드 결과로만 판정합니다.
   * **실행 방법은 csproj에서 판정한 프로젝트 종류별로 하나입니다:**

     | 종류 | 판별 | 실행 | 비교 단위 |
     | :--- | :--- | :--- | :--- |
     | xunit v3 | `xunit.v3*` 패키지 | `dotnet run --project <csproj> --no-build -- -result-trx <trx>` | 테스트별(TRX) |
     | 그 밖의 Microsoft Testing Platform | `IsTestingPlatformApplication`, `EnableMicrosoftTestingPlatform`, `MSTest.Sdk`, `Microsoft.Testing.Platform` 등 | `dotnet run --project <csproj> --no-build` | 종료 코드만 |
     | VSTest(SDK) | 위에 해당 없음 | `dotnet test <csproj> --no-build --logger trx` | 테스트별(TRX) |
     | 레거시 | `Sdk` 속성 없음 | 설정 `test.command`(`{project}`, `{dll}`, `{trx}` 자리 표시자) | `{trx}`가 있으면 테스트별, 없으면 종료 코드만 (R7) |

     .NET 10의 `dotnet test`는 Testing Platform 프로젝트에 `--logger`를 받지 않고, xunit v3의 `--report-xunit-trx`는 `dotnet run` 경로에서 거부되므로(실측) 한 종류에 한 경로만 둡니다. MSTest/NUnit Testing Platform 프로젝트와 다중 타겟 프로젝트의 테스트별 비교는 확인하지 않았습니다. 레거시 `test.command` 경로는 **Windows 미확인**입니다.
   * **순서:** 테스트 프로젝트를 먼저 빌드합니다. 패치 전 테스트(기준선)는 **Baseline 빌드 직후** 돌립니다. 원본 상태에서 테스트 프로젝트가 빌드되지 않으면 "원본 상태에서 테스트 프로젝트가 빌드되지 않아 테스트 판정을 건너뜁니다" 알림을 남기고 같은 방식으로 빌드로만 검증합니다. 기준선 테스트를 실행하지 못하면(오류·시간 초과) "원본 상태 테스트를 실행하지 못해 테스트 판정을 건너뜁니다: …" 알림을 남기고 빌드로만 검증합니다(환경 문제로 편집을 막지 않기 위해, T9 판단).
   * **판정:** 패치 전에 통과하던 테스트가 지금 실패하거나, 테스트 호스트가 죽었거나, 테스트가 사라졌으면 실패로 셉니다(**새로 실패한 것만** — 원래 실패하던 테스트는 세지 않음). 종료 코드만 보는 종류는 원본이 통과했는데 지금 실패하면 실패이고, 원본도 실패했으면 "비교할 수 없음" 알림입니다.
   * **피드백:** `TESTS FAILED (2 new failures)` 다음에 테스트마다 `<전체 이름>: <메시지 첫 줄>`, 종료 코드만 보는 종류는 `TESTS FAILED (exit n)`. 두 형식 모두 끝에 고정 문장 `Keep the requested change; do not undo it to make tests pass.`를 붙이며 항목을 줄여도 이 문장은 남깁니다(요청과 모순되는 테스트가 있을 때 모델이 자기 수정을 지우지 않게, M3a T14).
   * **시간 제한:** 설정 `test.timeoutSeconds`(기본 300초, R11)가 테스트 실행 1회의 제한이며 빌드 제한(`build.timeoutSeconds`, 120초)과 별개입니다. TRX는 `.pelly/runs/<runId>/tests/baseline-<n>.trx`, `verify-<시도>-<n>.trx`에 남습니다.
   * **알려진 한계:** 요청이 기존 테스트와 모순될 때(예: 테스트가 고정한 옛 동작을 바꾸라는 요청) 모델이 `throw`를 로그로 바꾸는 식으로 요청을 회피하고 테스트를 통과해 **종료 0**으로 끝나는 일이 있습니다. 최종 코드에서 20회 중 5회(25%, `docs/m3a-e2e.md` §3)였고 pelly는 의미상 회피를 판정할 수 없어 받아들인 한계입니다. `--test`를 쓸 때도 diff를 읽고 승인하십시오.

### 8.10 SQL 검증

#### SqlSafetyGuard (APPLY 단계, 파일 쓰기 전)
* 수정된 파일을 다시 배치로 나눕니다(§8.4와 같은 방식). **대상 배치를 뺀 나머지 배치는 원본과 바이트 단위로 같아야** 합니다(D15). 다르면 SafetyRejected.
* 대상 배치를 ScriptDom(`sql.compatLevel`에 맞는 `TSqlNNNParser`, `QUOTED_IDENTIFIER`는 파일에서 대상보다 앞선 마지막 설정)으로 파싱. **파싱 에러는 SafetyRejected가 아니라 `MatchFailed`**: 피드백 `SQL PARSE FAILED`(진단 코드 `SQL{번호}`), FailureKind `SqlParseErrors`, 소진 시 종료 1 (M2b T5·T9, R5).
* 대상 배치의 최상위 문장 수 = 1, 그 문장은 원본과 같은 종류(`CREATE`/`ALTER`/`CREATE OR ALTER`)이며 대상 객체 이름과 같아야 합니다. 대상 배치 안에 `GO`가 새로 생겨 배치가 늘거나 줄면 거부. 거부 사유는 모델에 영어 피드백으로 갑니다(예: "text outside the target object batch changed. Only the target object may change.").
* 방문자로 금지 요소를 **대상 배치 전체(객체 본문 포함)**에서 검사(PRD FR-07 목록): `SET NOEXEC`/`PARSEONLY`/`FMTONLY`, `SHUTDOWN`, `RECONFIGURE`, `xp_cmdshell` 실행, `OPENROWSET`(`OPENROWSET`·`BULK`·내부 형태 포함)/`OPENDATASOURCE`(테이블 참조 형태와 **`EXEC … OPENDATASOURCE(…)` 형태** 둘 다), `ALTER DATABASE`(`ALTER DATABASE SCOPED CONFIGURATION`·`ALTER DATABASE ENCRYPTION KEY`·`ALTER DATABASE AUDIT SPECIFICATION`도 같은 항목으로 셈 — 「위치 무관」 목록의 보수적 해석이며, 이것들을 정당하게 쓰는 프로시저는 pelly로 고칠 수 없음, M2b T5), `CREATE|ALTER|DROP LOGIN`, `GRANT`/`REVOKE`/`DENY`.
* 위반 → SafetyRejected(`SqlSafetyRejected`, 교정 기회 후 종료 5).
* **원본의 금지 구문 (M2b R2, D28):** 같은 검사를 LOCATE에서 **원본 대상 배치**에도 한 번 돌립니다. 이미 금지 구문이 있으면 모델을 부르지 않고 SafetyRejected(5, FailureKind `SqlForbiddenInOriginal`)로 끝납니다. PRD FR-07이 "위치 무관"이라 했고, 재시도해도 매번 거부될 뿐이기 때문입니다.

#### SqlSyntaxVerifier (VERIFY 단계)
0. **검증 대상은 대상 배치 하나뿐**입니다. 파일의 다른 배치(`SET ANSI_NULLS` 등)는 실행하지 않고, 대신 연결에 `SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON;`을 기본으로 설정합니다. 원본 파일에 이 두 설정의 `OFF`가 있으면 그 값을 따릅니다.
1. 연결 문자열이 없거나 접속할 수 없으면 PREFLIGHT에서 EnvError(4) (D16). 접속 대상이 `allowedHosts`/`allowedDatabases`에 없으면 SafetyRejected(5, `SqlTargetNotAllowed`) — 역시 PREFLIGHT에서 검사(§8.1, D27). 순서는 연결 문자열 → 허용 목록 → 서버 접속 → `VIEW DEFINITION` 확인이고, **모두 모델 생성 요청 전에** 끝납니다(모델 서버 상태 확인은 포함 안 함). 접속은 `ProbeAsync`가 하며 실패하면 `pelly doctor` 안내와 함께 EnvError(4)입니다. 이 권한이 없으면(`ProbeAsync`의 `CanViewDefinition` 거짓) EnvError(4, FailureKind `SqlPermission`, "검증 계정에 VIEW DEFINITION 권한이 없어 객체 존재 검사를 할 수 없습니다…") (M2b T6).
2. 객체 존재 검사(`SqlObjectStep`, 단계 이름 `SqlObjects`, FailureKind `SqlObjectsErrors`): 참조 객체 이름을 `OBJECT_ID(@name)`로 확인. 없으면 `Msg208 Invalid object name '...'` 형식으로 NewErrors(피드백 `SQL CHECK FAILED`). **원본 대비 새 오류만 셉니다** — 원본 대상이 이미 없는 객체를 참조하면 알림("원본 객체가 이미 없는 객체(…)를 참조합니다 — 새로 생긴 것만 검사합니다")을 남기고 그 이름은 기준선으로 뺍니다. **3부·4부 이름(다른 DB·연결된 서버)은 존재 검사에서 빼고 알림만** 합니다("다른 데이터베이스·연결된 서버 객체(…)는 존재를 검사하지 않습니다", M2b R7 — 그런 참조의 오타는 잡지 못함). 함수 호출은 호출 대상이 2부 이상이면 메서드 호출로 보아 무시하고, 1부면 스키마 없는 함수로 보되 테이블 별칭이면 제외합니다(컬럼과의 모호함을 메타데이터 없이 풀 수 없음, 새로 생긴 이름만 세므로 잡음은 제한적).
3. NOEXEC 컴파일 (**S5에서 확정**): 같은 연결에서 배치를 다음 순서로 따로 보냅니다.
   1. `BEGIN TRAN;`
   2. `SET NOEXEC ON;`
   3. `<대상 문장>` (단독 배치 — `CREATE`/`ALTER`는 배치의 첫 문장이어야 함)
   4. `SET NOEXEC OFF;`
   5. `IF @@TRANCOUNT > 0 ROLLBACK;`
   트랜잭션은 결과에 영향이 없었지만(S5) 안전장치로 유지합니다. NOEXEC 컴파일은 `ALTER` 권한을 검사하지 않습니다.
   `SqlException.Errors`(심각도 11 이상)를 `Diagnostic(Code = "Msg" + Number, Line = SqlError.LineNumber + (파일 안 대상 배치의 시작 줄 − 1))`로 변환합니다(`LineNumber`는 배치 기준, S5). 단계 이름 `SqlCompile`, FailureKind `SqlCompileErrors`. **원본 대비 새 오류만 셉니다:** 원본 대상 배치를 같은 방법으로 먼저 컴파일해 기준선("원본 객체에 이미 컴파일 오류가 N개 있습니다 — 새로 생긴 오류만 검사합니다")으로 삼고, 비교에서 줄 번호는 무시합니다(편집으로 줄이 밀려도 옛 오류가 새 오류가 되지 않게). 명령 시간 제한은 30초입니다.
   **VERIFY는 쓰인 파일을 다시 나눠** 현재 대상 배치(`Batches[TargetIndex]`)를 얻습니다. SLICE가 정한 `CurrentTarget`은 편집 전 텍스트이므로 APPLY 뒤에는 낡았습니다. 안전 가드가 배치 수와 대상 위치를 지켜 주므로 인덱스가 유지됩니다 (M2b T10).
   **S5 검출 결과:** 없는 컬럼 207, 구문 오류 102는 잡히고, **없는 테이블은 NOEXEC·실제 ALTER 모두 잡지 못합니다**(지연 이름 확인) → 2번 객체 존재 검사가 필수입니다.
   **최소 권한: DB 수준 `VIEW DEFINITION`.** 이 권한이 없으면 `OBJECT_ID`가 NULL, `sys.columns`가 0건이 되어 존재 검사·스키마 요약이 모두 틀어집니다(S5).
4. 동적 SQL(`ExecuteStatement`의 문자열 실행, `sp_executesql`)이나 `#temp` 사용이 있으면 Notices에 경고 추가(한 실행에 한 번): "동적 SQL 안의 문장은 검증하지 않습니다 (설계 §8.10)", "임시 테이블(#)의 컬럼은 검증하지 않습니다 (설계 §8.10)".
5. 두 단계 중 서버를 쓸 수 없으면(`SqlUnavailableException`) 모델에 되묻지 않고 EnvError(4)입니다. 그 밖의 예외도 같습니다.
6. **별칭 제외 범위:** 스키마 없는 테이블 이름은 같은 문장 안에서 **다른** 테이블 참조가 그 이름을 별칭으로 선언했을 때만 존재 검사에서 뺍니다. 다른 문장의 별칭은 가리지 않고, `FROM Orders AS Orders`처럼 자기 자신의 별칭도 검사합니다 (M3a T13, M2b 최종 리뷰 M-1·M-2 해소). 취소된 SQL 호출은 `OperationCanceledException`으로 올라가 종료 코드 2가 됩니다.
7. **배포된 객체가 이미 깨진 경우 (M2b 최종 리뷰 I-1):** 삭제된 컬럼을 참조하는 등 배포본이 깨져 있으면 `sys.dm_sql_referenced_entities`가 연결을 유지한 채 Msg 207을 냅니다. 실행기는 연결 수준이 아닌 `SqlException`이면 빈 목록을 돌려주고 SLICE는 수집기가 찾은 참조만으로 진행하며, 그 밖의 예외는 EnvError(4, `스키마 정보를 가져오지 못했습니다`)입니다. 원본의 컴파일 오류는 기준선으로 빼고 새 오류만 셉니다.
8. **변화 없음 가드 (M3a T14):** APPLY에서 패치한 대상 배치가 원본 대상 배치와 공백 정규화 후 같으면 쓰기 전에 거부합니다(FailureKind `NoChange`, §8.7).

### 8.11 REVIEW·사용자 상호작용
* DiffPlex로 원본·수정본의 unified diff(문맥 3줄)를 만들어 Spectre로 색 표시.
* `IsInteractive` = 콘솔 입력이 리디렉션되지 않았고 `--yes`가 아님. 비대화형이고 `--yes`도 없으면 REVIEW에서 Rejected로 처리(안전 쪽).
* **`--verbose` (M3a T5, R4):** 실행이 끝난 뒤 단계별 시간·토큰·시도 요약과 실행 폴더(`.pelly/runs/<runId>/`) 경로를 보여줍니다. 프롬프트 원문은 4천 토큰이라 콘솔을 덮으므로 출력하지 않고 `attempt-<n>/prompt.txt` 경로를 안내합니다.
* 콘솔: 시작 시 `Console.OutputEncoding = Console.InputEncoding = UTF8`. 단계 진행은 Spectre `Status` 스피너로 표시.

### 8.12 로그·리포트
* `.pelly/runs/<runId>/` (runId = `yyyyMMdd-HHmmss-<4자리 hex>`)
  * `events.jsonl`: 단계 시작·종료, 결과, 소요 시간, 토큰 수(local/server), 시도 번호, 매칭 단계, 진단 코드, 종료 코드
  * `attempt-<n>/prompt.txt`, `think.txt`, `output.txt`, `feedback.txt`, `build.log`
  * `summary.json`: `RunSummary` (아래)
* `retentionDays`보다 오래된 run 폴더는 **실행이 끝날 때**, 작업 공간 잠금을 쥔 채로만 삭제합니다(현재 실행은 지우지 않음) (M2a T12).
* `FailureKind` 값에는 M2a에서 `BuildErrors`(빌드 신규 오류), `BuildToolUnavailable`, `BaselineErrors`, `BuildUnverifiable`, `ShapeRejected`, `TargetNotFound`(LOCATE·APPLY 모두)가 더해졌습니다 (M2a T6, T7). 검증 단계의 값은 `{단계 Name}Errors`(`SyntaxErrors`, `BuildErrors`)입니다.
* `FailureKind` 값에는 M2b에서 SQL용 `SqlTargetNotAllowed`(PREFLIGHT 허용 목록), `SqlPermission`(`VIEW DEFINITION` 없음), `SqlForbiddenInOriginal`(LOCATE, R2), `SqlParseErrors`(APPLY 파싱 오류, R5), `SqlSafetyRejected`(APPLY 가드), `SqlObjectsErrors`·`SqlCompileErrors`(VERIFY 단계 `{Name}Errors`)가 더해졌습니다. 객체를 못 찾음·모호함은 기존 `TargetNotFound`·`TargetAmbiguous`입니다 (M2b T5~T10).
* **`FailureKind` 카탈로그 (M3a T1, `FailureKinds.cs`):** 실행기가 낼 수 있는 값의 닫힌 목록입니다. 패치 실패 `Parse`·`NotFound`·`Ambiguous`·`OutsideRegion`·`Overlap`·`Unencodable`, 환경·빌드 `BaselineErrors`·`BuildToolUnavailable`·`BuildUnverifiable`·`Crashed`·`FileChangedExternally`·`ShapeRejected`·`NoChange`(M3a, §8.7), SQL `SqlForbiddenInOriginal`·`SqlParseErrors`·`SqlPermission`·`SqlSafetyRejected`·`SqlTargetNotAllowed`, 대상 `TargetAmbiguous`·`TargetNotFound`, 검증 단계 `SyntaxErrors`·`BuildErrors`·`SqlObjectsErrors`·`SqlCompileErrors`·`TestErrors`(M3a, 단계 `Test`), 종료 `EnvError`·`BudgetExceeded`·`Rejected`·`Cancelled`. 비식별 리포트는 `FailureKinds.IsKnown`을 통과한 값만 내보냅니다.
* **진단 코드 (R6):** DB 오류는 `Msg{번호}`(예: `Msg207`, `Msg208`), ScriptDom 파싱 오류는 `SQL{번호}`(예: `SQL46010`)로 `ErrorCodes`에 남습니다. 테스트 실패는 `TEST`입니다 (M3a R8).
* **요약의 종류 (M2b T9):** LOCATE가 대상을 정하지 못하고 끝난 실행의 `Kind`와 프롬프트 버전은 `ctx.Target?.Kind ?? ctx.Request.Kind`를 따릅니다(요청 종류). 앞의 `?? CSharp`로 두면 일찍 실패한 SQL 실행이 C#으로 집계되어 벤치마크·KPI 집계가 틀어집니다.
* 연결 문자열은 `Password=`, `Pwd=` 값을 마스킹.

```csharp
public sealed record RunSummary(
    string RunId, string PellyVersion, string? OllamaVersion, string ModelName, string PromptVersion,
    EditKind Kind, string ProjectStyle /* Sdk|Legacy|Sql */, int ExitCode, int Attempts,
    IReadOnlyDictionary<RunPhase, double> PhaseSeconds, double TtftSeconds, double QueueSeconds,
    string ThinkMode, int ThinkTokens, bool ThinkTruncated,
    int InputTokensLocal, int? InputTokensServer, int? InputTokensCached,
    IReadOnlyList<MatchStage> MatchStages, IReadOnlyList<string> ErrorCodes /* CS0103, Msg207, TEST … */,
    int FormatErrors /* EDIT 결과가 FormatError였던 횟수 (M3a R2) */, string? FailureKind);
```
* **`FormatErrors` (M3a R2):** 리포트가 `summary.json`만 읽고도 파싱 성공률(= (시도 − `FormatErrors`) / 시도)을 내도록 둔 필드입니다. 벤치마크 실행기의 파싱 성공률도 같은 계산(`KpiMath.ParseSuccess`)을 쓰므로 이 필드가 없는 M3a 이전 `summary.json`을 다시 집계하면 100%로 나옵니다.
* **`QueueSeconds` (M3a R3):** §8.2의 대기. `TtftSeconds`는 대기를 뺀 값입니다.
* **`pelly report [--since yyyy-MM-dd] [--sanitize [--out <폴더>]]` (M3a T3·T4):** 현재 폴더의 작업 공간 `.pelly/runs/*/summary.json`만 읽어(다른 파일은 읽지 않음) PRD §2.2 순서의 지표 표와 종료 코드·실패 유형·시도 수 분포, 단계별 평균 시간을 출력합니다.
  * 지표: 검증 불변 조건(독립 검증이 없어 "벤치마크 실행기로 측정"으로 표시), TTFT(중위·P90), 대기(Queue), Thinking(think 최대 토큰·잘림 건수), 패치 포맷 파싱 성공률, 최종 작업 성공률(종료 0), 단일 작업 완료 시간(단계 합).
  * 읽을 수 없는 `summary.json`은 건수만 알리고 건너뜁니다. 기록이 없으면 안내하고 종료 0입니다. `--since`는 형식이 틀리면, `--out`은 `--sanitize` 없이 쓰면 사용 오류(종료 4)입니다.
* **`--sanitize` (D11, M3a T4):** 허용 목록 방식이며, 화면 대신 `pelly-report-<yyyyMMdd-HHmmss>.json`과 `.md` 두 파일을 `--out`(기본 현재 폴더)에 씁니다(같은 초에는 `-2` 접미사, 덮어쓰지 않음; 저장 실패는 종료 4). 요약(`RunSummary`)의 값만 쓰며, 출력할 수 있는 문자열은 다음뿐입니다(M3a 구현에서 계획보다 조였습니다 — 리포트가 폐쇄망 밖으로 나가므로 모든 정규식은 `\A…\z`로 고정하고 숫자는 ASCII입니다).

  | 값 | 허용 규칙 | 그 밖 |
  | :--- | :--- | :--- |
  | `ErrorCodes` | `\A(CS\|MSB\|NU\|Msg\|SQL)[0-9]{1,6}\z` 또는 정확히 `TEST` (R8). `SQL`은 ScriptDom 파싱 오류(M2b R6) | 버리고 `DroppedErrorCodes` 건수만 |
  | 버전(`PellyVersion`, `OllamaVersion`) | `\A[0-9][0-9A-Za-z.+\-]{0,39}\z` (숫자로 시작) | `"invalid"` |
  | `PromptVersion` | `\A(cs\|sql)-v[0-9]{1,4}\z` | `"invalid"` |
  | `FailureKind` | `FailureKinds.IsKnown` 통과 값 | `"Other"` |
  | `ProjectStyle` | `Sdk`·`Legacy`·`None`·`Sql` | `"Other"` |
  | `Kind`·`ThinkMode`·`MatchStages` | 열거형 이름 | — |

  `ModelName`·`RunId`·경로·메시지·소스 문자열은 어떤 경로로도 출력하지 않습니다. 숫자 필드(시간·토큰·시도·종료 코드·`FormatErrors`)와 집계(성공률·분포)는 그대로 냅니다. 숫자로 시작하지 않는 버전 문자열(현재 없음)은 `invalid`로 나옵니다. 실제 실행 2건으로 소스 식별자·경로·모델 이름·RunId가 어느 파일에도 없음을 확인했습니다(`docs/m3a-e2e.md`).

### 8.13 doctor
PRD FR-08 항목을 순서대로 검사하고 표로 출력합니다. 각 항목은 `Ok / Warn / Fail`과 조치 안내 한 줄. 추가로:
* **템플릿 일치:** 고정된 짧은 대화를 `/api/chat`과 raw(`QwenChatTemplate` 조립)로 각각 `num_predict: 1`로 보내 `prompt_eval_count`가 같으면 Ok, 다르면 Warn(S1 방식). `/api/show`의 `RENDERER`/`PARSER` 이름도 표시.
* **토크나이저 일치:** 고정 문자열 1개의 로컬 토큰 수와 서버 `prompt_eval_count`가 같으면 Ok, 다르면 Fail(S4 — 모델과 내장 토크나이저 불일치).
* **SQL 권한:** 설정된 DB에서 `OBJECT_ID`가 알려진 테이블에 대해 NULL이면 "VIEW DEFINITION 권한 필요"로 Fail(S5).
* **행 목록 (M3a T10, 순서):** `Ollama 서버`, `모델`, `렌더러`, `템플릿 일치`, `토크나이저 일치`, `응답 시간`, `pelly`(버전·토크나이저 해시), `설정`(사용자·작업 공간 설정 경로와 적용 값), `.NET SDK`(`dotnet --version`), `MSBuild (레거시)`(Windows에서만, 아니면 Warn), `타겟팅 팩`(`--project <csproj>`가 있을 때 그 프로젝트의 .NET Framework 타겟팅 팩 설치 여부; Windows 전용, **Windows 미확인**), `서버 설정 확인 필요`(`deploy/server-checklist.md` 내용; 기존 안내에 체크리스트 위치를 덧붙여 환경 변수 이름을 그대로 보입니다), `SQL 연결`. Fail이 하나라도 있으면 종료 4입니다.
* **SQL 연결 (M2b T11):** 한 행 `SQL 연결`. 연결 문자열이 없으면 Warn("pelly sql을 쓰려면 sql.connectionString 또는 PELLY_SQL_CONNECTION을 설정하세요"), 허용 목록 위반이면 Fail(§8.1과 같은 메시지), 접속·`VIEW DEFINITION` 실패는 Fail(연결 문자열의 비밀번호는 `***`로 가림), 통과하면 Ok(`서버/DB, 서버 버전`). 전체 doctor는 M3a에서 완성했습니다.
* 1회 짧은 생성(`num_predict: 8`)으로 TTFT 실측.
* 서버 설정 체크리스트(`deploy/server-checklist.md` 내용)를 표시.
* **`pelly doctor --project <csproj>` (M3a T10):** 타겟팅 팩 행의 검사 대상 프로젝트입니다. 파일이 없으면 종료 4입니다.
* **`pelly doctor --sanitize [--out <폴더>]` (D11, R9, M3a T11):** 화면 대신 비식별 결과를 `json`·`md` 두 파일로 씁니다. 행 이름(위 알려진 목록, 그 밖은 `기타`)과 상태, 그리고 행마다 정한 키의 `Facts`만 남기며 `Detail`·`Action` 문장은 읽지도 않습니다. `Facts` 키는 `DoctorFacts` 한 곳에서 만들고 허용합니다(이름이 바뀌어 사실이 조용히 비는 일을 막음). 행별 키: Ollama 서버 `ollamaVersion`·`latencyMs`, 모델 `loaded`, 토크나이저 일치 `match`, 응답 시간 `ttftMs`, pelly `pellyVersion`·`tokenizerHash`, .NET SDK `dotnetVersion`, MSBuild `found`, 타겟팅 팩 `frameworkVersion`·`installed`, SQL 연결 `canViewDefinition`·`serverMajor`. 버전 값(`ollamaVersion`·`dotnetVersion`·`pellyVersion`)은 리포트와 같은 버전 규칙(숫자로 시작하는 `[0-9A-Za-z.+\-]{1,40}`, 아니면 `"invalid"`), `frameworkVersion`은 별도의 모니커 규칙 `\A[vV][0-9]{1,2}(\.[0-9]{1,2}){1,2}\z`(`v4.8`, `v4.5.2`; 아니면 `"invalid"`), 나머지는 짧은 토큰 규칙을 통과해야 하며 비어 있는 사실은 생략합니다. 연결 문자열·경로·메시지는 나오지 않습니다.
* **`pelly doctor --offline-selftest` (숨은 옵션, M3a T12):** 설정·HTTP 클라이언트·네트워크 객체를 만들기 전에 반환하며, 내장 구성요소(토크나이저, Roslyn 구문 분석, ScriptDom 파싱, CP949 인코딩 왕복, 내장 리소스, SqlClient 네이티브 계층 — 여섯 행)를 확인해 행으로 보입니다. 하나라도 Ok가 아니면 종료 4입니다. 단일 파일 배포본 확인에 쓰며 M3b T7의 CI `publish-check`가 이를 실행해 녹색입니다(S6 통과).
  * **여섯 행 (M3b T7, S6):** 토크나이저 · Roslyn · ScriptDom · 인코딩 · 리소스 · `SqlClient`. `SqlClient` 행은 Windows에서 존재하지 않는 로컬 파이프(`np:\\.\pipe\pelly-selftest-none\sql\query`, `Connect Timeout=1`)에 연결을 시도해 `SqlException`이 나면 네이티브 SNI가 로드된 것으로 보고 Ok입니다(네트워크는 쓰지 않음). Windows가 아니면 SqlClient가 관리 코드 SNI를 쓰므로 `SqlConnection`을 만들기만 합니다. SqlClient 호출은 `SqlServerExecutor.SelfTestNativeLayer()` 안에만 둡니다.

### 8.14 IDE External Tools 연동 (D19)
* `deploy/ide-external-tools.md`에 두 IDE 설정 방법을 씁니다.

  | 항목 | Visual Studio (도구 → 외부 도구) | Rider (Settings → Tools → External Tools) |
  | :--- | :--- | :--- |
  | 명령 | `pelly.exe` 전체 경로 | 같음 |
  | 인수 | `cs "$(ItemPath)" --line $(CurLine)` | `cs "$FilePath$" --line $LineNumber$` |
  | 초기 디렉터리 | `$(SolutionDir)` | `$ProjectFileDir$` |
  | 기타 | "출력 창 사용" 끔(별도 콘솔 창에서 대화형 입력) | "Synchronize files after execution" 켬 |

* SQL용 항목(`sql "$(ItemPath)" --line $(CurLine)`)도 같은 방식으로 하나 더 등록합니다.
* **실행 전 저장:** 저장하지 않은 편집이 IDE에 있으면 pelly는 디스크의 파일만 보고 수정하게 됩니다. 가이드에서 실행 전 저장(VS: 단축키로 모두 저장 후 실행, Rider: 외부 도구 실행 전 자동 저장 설정)을 안내합니다. 저장되지 않은 IDE 편집과 충돌하는 경우에도 pelly 쪽 파일은 FileGuard가 보호합니다.
* **실행이 끝나면 창을 바로 닫지 않습니다:** External Tools로 띄운 콘솔은 종료하면 바로 사라지므로, `--pause-on-exit`(External Tools 가이드 인수에 포함) 옵션이 있으면 "종료 코드 n. 아무 키나 누르면 창을 닫습니다…"를 보여주고 키 입력을 기다립니다 (M3a R10). 판정은 `Program`에서 **원시 인자**로 하므로 인자 해석 오류나 예외로 끝나도 창이 바로 닫히지 않습니다. `pelly doctor`와 `pelly report`도 같은 원시 인자 판정을 거치므로 `--pause-on-exit`가 똑같이 동작합니다(입력을 리디렉션해 실행해 종료 코드 0 확인, 키 대기 자체는 Windows 미확인). 입력이 리디렉션되어 있으면 기다리지 않고(누를 사람이 없음), `--` 뒤의 인자는 무시합니다. **Ctrl+C로 중단한 실행도 정상 종료 코드로 끝난 뒤 키를 기다립니다(M3b).** 종료 신호(창 닫기, SIGTERM, SIGHUP)로 중단한 실행은 기다리지 않습니다. 대기 중 두 번째 Ctrl+C나 종료 신호가 오면 바로 끝납니다. 창 닫기(Windows CTRL_CLOSE)와 Ctrl+C의 구분은 **Windows 미확인**입니다. 콘솔 키 대기(`Console.KeyAvailable` 폴링)는 macOS에서만 확인했고 **Windows 미확인**입니다.
* 콘솔 창에서 한글 입력·출력이 깨지지 않는지(D20, §8.11 UTF-8 설정) 확인하는 것을 S7에 포함합니다.

### 8.15 v1의 알려진 한계 (사용자에게 안내)
* **C# 문자열 안의 SQL은 DB로 검증하지 않습니다.** Dapper 호출(`Query<T>("SELECT …")`)이나 ADO.NET `SqlCommand.CommandText`에 들어 있는 SQL은 C# 빌드만 통과하면 승인됩니다. 잘못된 컬럼명은 실행 시점에야 드러납니다. REVIEW 단계에서 대상 멤버의 문자열 리터럴이 바뀌었고 그 리터럴에 `SELECT|INSERT|UPDATE|DELETE|EXEC`(대소문자 무시)가 있으면 "인라인 SQL이 바뀌었습니다 — 빌드는 통과했지만 DB로 검증되지 않았습니다 (설계 §8.15)" 경고를 `--yes`일 때도 표시합니다 (M2a T13). 인라인 SQL 추출·NOEXEC 검증은 v2 후보입니다.
* Razor 뷰(`.cshtml`), 설정 파일(`web.config`, `appsettings.json`)은 수정 대상이 아닙니다.
* 컨트롤러 액션의 라우팅·필터 특성(`[HttpPost]`, `[Authorize]` 등)은 MemberShapeGuard 때문에 바꿀 수 없습니다(의도된 동작).

### 8.16 다음 단계·v2 확장 지점 (구현은 하지 않음)
* **GUI 승인 창 (`pelly ui`, v1 다음 단계):** Avalonia로 만든 작은 창. External Tools 인수를 `ui cs "$(ItemPath)" --line $(CurLine)`로 바꾸면 요청 입력·진행 표시·diff 승인을 창에서 합니다. `IUserInteraction`의 Avalonia 구현만 추가하면 되도록 v1에서 D20을 지킵니다. 진행 표시를 위해 `IUserInteraction.ShowStatus`는 단계·시도 번호·경과 시간을 구조화된 값으로 받습니다.
* `FsmOrchestrator.RunAsync`는 외부에서 반복 호출 가능하게 둡니다(`SpecRunner`).
* VERIFY는 `IReadOnlyList<IVerifierStep>` 파이프라인으로 구성합니다(v2의 `ForbiddenPatternVerifier`, `TestRunner` 추가 지점). **M2a에서 실현 (T6):** `VerifyPhase`가 `[SyntaxStep, BuildStep]`을 순서대로 돌리고 첫 실패에서 멈추며, 단계의 `Name`이 실패 라벨(`{Name}Errors`)이 됩니다. 단계가 검사를 아예 못 돌린 경우(`EnvironmentFailure`: 타임아웃·도구 없음)는 모델에 되묻지 않고 EnvError(4)로 끝냅니다.
* 시스템 프롬프트 조립에 `Rules` 슬롯(v1에서는 항상 비어 있음)을 둡니다.

---

## 9. M0 스파이크

본 구현 전에 Mac에서 실행하는 짧은 검증입니다. 각 결과는 `docs/spikes/S<n>-<이름>.md`에 남기고, 결정된 값은 기본 설정·리소스에 반영합니다. 스파이크 코드는 `spikes/` 폴더에 두고 제품 코드에 섞지 않습니다.

| ID | 질문 (PRD Q) | 방법 | 통과 기준 / 산출물 |
| :--- | :--- | :--- | :--- |
| S1 | 실제 chat template·think 태그 (Q1) | `ollama show <model> --template`, raw 요청 응답 관찰(`thinking` 필드 분리 여부 포함) | `template.json` 확정값 |
| S2 | raw 프리필 + 접두사 캐시 재사용 (Q3) | 3,500토큰 프롬프트로 1단계 → 2단계 요청 시 2단계의 `prompt_eval_count`·TTFT 측정 | 2단계 `prompt_eval_count`가 새로 붙인 부분 수준이고 TTFT < 1초. 아니면 대안 기록(예: 1단계를 끊지 않고 `</think>` 이후 그대로 이어받기) |
| S3 | think/code 샘플링 기본값 (Q2) | 모델 카드 확인 + 샘플 작업 10건으로 think 반복·잘림 비율 비교 | `sampling` 기본값 |
| S4 | .NET에서 Qwen 토크나이저 (—) | `Microsoft.ML.Tokenizers`로 `tokenizer.json` 로드, 문장 50개를 서버 `prompt_eval_count`와 비교 | 오차 ≤ 2% 이면 `QwenTokenCounter` 채택, 아니면 `CalibratedEstimator` |
| S5 | NOEXEC 최소 권한·트랜잭션 처리 (Q4) | Docker SQL Server에서 권한 조합별로 `ALTER PROCEDURE` NOEXEC 컴파일 시험 | 필요 권한 목록, §8.10 배치 순서 확정 |
| S7 | IDE External Tools 연동 (—, M3b) | Mac의 Rider에서 External Tools로 `--line` 실행, 대화형 입력·한글·`--pause-on-exit`·파일 다시 불러오기 확인. Visual Studio는 M4 폐쇄망에서 확인 | `deploy/ide-external-tools.md` 확정 |
| S6 | 단일 파일 publish (—, M3b) | `win-x64` self-contained single-file로 Roslyn·ScriptDom·임베디드 토크나이저 동작 확인(CI windows 러너) | 동작 여부와 필요한 `IncludeNativeLibrariesForSelfExtract` 등 설정 |

S1~S5는 M0, S6·S7은 M3b에 수행합니다. 결과는 §9.1(S1~S5)과 §9.2(S6·S7)에 있습니다.

### 9.1 M0 결과 (2026-10-17, `qwen3.6:35b-a3b-coding-nvfp4` — 폐쇄망 모델과 같은 모델을 받으면 S1·S2·S4 재실행)
| ID | 결과 | 문서 |
| :--- | :--- | :--- |
| S1 | 통과. ChatML + `<think>\n` 확정(렌더러와 토큰 수 일치). raw 모드는 생각도 `response`로 옴. **raw 모드에서 `stop` 무시됨 → D23** | [S1](../../spikes/S1-template.md) |
| S2 | 통과. 2단계 TTFT 299ms. 클라이언트 취소는 다음 요청 +2초 → D22. 1단계(무캐시 4,300토큰) TTFT 1.56초는 이 Mac에서 KPI 초과 | [S2](../../spikes/S2-prefill-cache.md) |
| S3 | 샘플링 기본값 유지. think 30/30이 512 초과, 생략해도 결과 동일·5배 빠름 → D21 | [S3](../../spikes/S3-sampling.md) |
| S4 | 통과. `Microsoft.ML.Tokenizers` 2.0.1로 서버와 오차 0% → `QwenTokenCounter` | [S4](../../spikes/S4-tokenizer.md) |
| S5 | 최소 권한 `VIEW DEFINITION`, 배치 순서 확정, 없는 테이블은 존재 검사로만 잡힘 | [S5](../../spikes/S5-sql-noexec.md) |

### 9.2 M3b 결과 (2026-10-18~19)
| ID | 결과 | 문서 |
| :--- | :--- | :--- |
| S6 | 통과(CI `windows-latest`, `publish-check` 잡). `win-x64` self-contained single-file `pelly.exe` 약 113.8 MB(119,305,264 B), 트리밍·압축 없음. 첫 실행에 풀리는 파일은 `Microsoft.Data.SqlClient.SNI.dll`(약 0.5 MB) 하나. 자체 점검 여섯 행 모두 Ok, `--version` 0.9.0, 첫 실행 1.6~2.5초. 실제 폐쇄망 PC(권한·백신·`%TEMP%` 정책)는 **Windows 미확인**이라 M4에서 잰다 (M3b T7) | [S6](../../spikes/S6-single-file.md) |
| S7 | **결과 대기.** Rider External Tools 확인표와 준비물은 만들었다(M3b T11). 개발 Mac에 Rider가 없어 사용자가 직접 확인한 뒤 결과를 기입한다. Visual Studio는 M4 폐쇄망에서 확인 | [S7 확인표](../../m3b-s7-rider.md) |

---

## 10. 테스트 전략

### 10.1 분류
| 분류 | 위치 | 실행 위치 | 필요 조건 |
| :--- | :--- | :--- | :--- |
| Unit | `Core.Tests` | 어디서나, CI 전체 | 없음 (가짜 구현·fixture) |
| Windows | `Integration.Tests` `[Trait("Env","Windows")]` | CI `windows-latest` | Windows, VS Build Tools |
| SqlServer | `Integration.Tests` `[Trait("Env","SqlServer")]` | 로컬 Docker, CI `ubuntu-latest` 서비스 컨테이너 | `PELLY_TEST_SQL` 환경 변수 |
| Ollama | `Integration.Tests` `[Trait("Env","Ollama")]` | 로컬 Mac만 | `PELLY_TEST_OLLAMA`, `PELLY_TEST_MODEL` 환경 변수 |

조건이 없으면 테스트는 실패가 아니라 Skip으로 표시합니다.

### 10.2 필수 단위 테스트 (구성 요소별 최소 기준)
* **Transitions:** §7.1 표의 모든 행.
* **Parser/Patcher:** 3단계 매칭 각각, Ambiguous, OutsideRegion, 여러 블록 순차 적용, 겹침, 출력 잘림, 들여쓰기 보정(공백·탭), 빈 SEARCH, 마커 충돌.
* **FileGuard:** UTF-8/UTF-8 BOM/CP949/UTF-16 왕복 시 바이트 동일, CRLF/LF/혼합, 마지막 줄바꿈 유무 보존, CP949 표현 불가 문자, 외부 변경 감지, 공유 위반 재시도(가짜 파일 시스템).
* **RoslynSlicer:** fixture 소스 → 슬라이스 스냅샷(Verify), 예산별 제거 순서, 언어 버전 감지 표.
* **BuildErrorParser:** `tests/fixtures/build/` 의 실제 `dotnet build`·`MSBuild.exe` 출력(영어, 한글 경로, 프로젝트 접미사, 중복) 파싱 스냅샷.
* **Baseline 차감:** 다중집합 차감, 줄 번호만 바뀐 기존 에러 무시.
* **SqlSafetyGuard:** 허용 문장 3종, SSMS 스크립트 형태(SET/GO 포함) 파일, 한 파일 여러 객체, 대상 외 배치 변경 거부, 대상 배치 안 `GO` 추가 거부, 금지 요소 각각, 문자열·주석 안의 금지어·`GO`는 허용.
* **MemberShapeGuard:** 시그니처 변경(매개변수, 반환 타입, async, 접근 한정자, 특성) 거부, private 헬퍼 추가 허용, public 메서드·필드·프로퍼티 추가 거부, using 추가 허용·삭제 거부.
* **설정 검증:** 설정 파일 `sql.connectionString`에 `Password`/`Pwd`가 있으면 오류.
* **QwenStreamClient:** 녹화한 Ollama NDJSON을 재생하는 가짜 `ILlmBackend`로 512 도달 시 취소, 잘린 줄 제거, 프리필 문자열 정확성, `</think>` 자연 종료.
* **ReportSanitizer:** 일부러 코드·경로·식별자를 넣은 RunSummary에서 출력에 해당 문자열이 하나도 없는지.
* **Orchestrator:** 가짜 의존성으로 성공, 교정 후 성공, 최종 실패 롤백, 취소 시 롤백, 예외 시 롤백, dry-run 무변경.

### 10.3 Fixture 녹화
`tests/fixtures/README.md`에 녹화 방법을 적습니다. Windows 전용 출력(MSBuild.exe, vswhere)은 CI의 `windows-latest`에서 `record-fixtures` 워크플로(수동 실행)로 녹화해 아티팩트로 받은 뒤 저장소에 커밋합니다.

### 10.4 CI (`.github/workflows/ci.yml`)
| 잡 | 러너 | 내용 |
| :--- | :--- | :--- |
| unit | `macos-latest`, `windows-latest` | 빌드 + Unit 테스트 |
| windows-integration | `windows-latest` | `eval/corpus/Legacy`·`Modern` 빌드(레거시는 `nuget restore` 후 MSBuild), Windows 테스트, `pelly`의 BuildVerifier를 코퍼스에 대해 실제 실행(LLM 없이 미리 정의된 패치 적용) |
| sql-integration | `ubuntu-latest` + `mcr.microsoft.com/mssql/server:2022-latest` 서비스 | 코퍼스 SQL 배포 후 SqlServer 테스트 |
| publish-check | `windows-latest` | `win-x64` self-contained single-file publish 후 `pelly doctor --offline-selftest`(여섯 행 Ok), `--version`(`0.9.0`으로 시작), `cs --help`, 닫힌 루프백 포트로 `doctor`(종료 4와 내장 서버 체크리스트 출력)를 확인하고 exe를 아티팩트로 올림 (M3b T7) |
| package | `windows-latest` | publish-check의 exe로 반입 zip `pelly-0.9.0-win-x64.zip` 생성(`deploy/build-package.ps1`): 라이선스 게이트 모의 실행(예외 승인의 버전·해시를 바꾼 사본은 실패해야 함), 패키지 생성, `SHA256SUMS.txt`를 `install.md` 방식으로 확인, `Package` 테스트 6개(`--fail-skips on`) 후 zip 업로드 (M3b T9) |

M3b 이후 CI는 **실행 6회 = 잡 키 5개**입니다(unit은 macOS·Windows 매트릭스라 2회): unit ×2, windows-integration, sql-integration, publish-check, package (M3b T7·T9).

`pelly doctor --offline-selftest`는 네트워크 없이 토크나이저 로드, Roslyn 파싱, ScriptDom 파싱, 인코딩 provider, 내장 리소스, SqlClient 네이티브 계층(여섯 행)을 확인하는 숨은 옵션입니다.

### 10.5 샘플 코퍼스 구성 (`eval/corpus`)
실제 코드 구성(사용자 답변)을 따라 만듭니다. 메서드 길이는 50~150줄 위주로, 일부(약 10%)는 예산 경계(200줄 이상)로 둡니다.

| 솔루션 | 프로젝트 | 형식 | 특징 |
| :--- | :--- | :--- | :--- |
| `Legacy` | `Shop.Web` | 레거시 csproj, .NET Framework 4.8, ASP.NET MVC 5 | `packages.config`, 컨트롤러·서비스, C# 7.3, 일부 파일 CP949·CRLF |
| `Legacy` | `Shop.Batch` | 레거시 csproj, .NET Framework 4.5, 콘솔 | ADO.NET(`SqlCommand`, `SqlDataReader`) 직접 사용 |
| `Legacy` | `Shop.Data` | 레거시 csproj, .NET Framework 4.5 | Dapper 리포지토리, DTO |
| `Legacy` | `Shop.Tests` | 레거시 csproj, 4.8, MSTest 또는 NUnit | `Shop.Data`·`Shop.Web` 참조 |
| `Modern` | `Shop.Api` | SDK 스타일, `net8.0`, ASP.NET Core MVC | Dapper, async |
| `Modern` | `Shop.Jobs` | SDK 스타일, `net48`(SDK 스타일로 Framework 타겟) | 콘솔 배치 |
| `Modern` | `Shop.Api.Tests` | SDK 스타일, xUnit | |
| `Sql` | — | `.sql` 파일 | SSMS 스크립트 형태(SET/GO), 한 파일 여러 객체 사례 포함, 샘플 스키마·데이터 |

* **.NET Framework 4.5 빌드 환경:** Visual Studio 2022에는 4.5 타겟팅 팩이 기본 포함되지 않을 수 있습니다. CI `windows-integration` 잡은 빌드 전에 NuGet 패키지 `Microsoft.NETFramework.ReferenceAssemblies.net45`의 참조 어셈블리를 `%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5`에 풀어 넣는 단계를 둡니다. SDK 스타일의 `net48`은 같은 계열 NuGet 패키지 참조로 Mac에서도 빌드합니다. `pelly doctor`는 대상 프로젝트의 타겟팅 팩 설치 여부를 확인합니다.
* 레거시 테스트 프로젝트는 `test.command` 예시(`vstest.console.exe {dll}`)를 코퍼스 `pelly.json`에 둡니다.

### 10.6 벤치마크 (`eval/`)
* 작업 정의 YAML:
  ```yaml
  id: cs-legacy-007
  kind: cs
  project: Legacy/Shop.Orders/Shop.Orders.csproj
  file: Legacy/Shop.Orders/OrderService.cs
  method: Cancel
  request: "배송 완료 상태면 InvalidOperationException을 던지도록 수정"
  judge:
    build: true
    tests: ["Shop.Orders.Tests.OrderServiceTests.Cancel_WhenShipped_Throws"]   # 선택
    mustContain: ["InvalidOperationException"]                                  # 선택 — 원본 대상에 없어야 함
    mustAdd: ["OrderStatus.Shipped"]                                            # 선택 — 원본보다 많이 나와야 함
  ```
* 공통 규칙: 대상이 원본과 같으면 실패(아무것도 안 고친 패치 차단). **공백 차이뿐인 수정(줄바꿈·들여쓰기만 바뀐 재포맷)도 "같다"로 셉니다**(공백 연속을 한 칸으로 접어 비교). **모든 작업은 내용 판정(`mustContain`·`mustAdd`·`mustNotContain`)을 하나 이상 가집니다** — 빌드·컴파일 판정만으로는 의미 없는 수정도 통과합니다(`AllTasks_HaveContentCriterion`, M2b T14). 정의 형식과 규칙은 `eval/benchmark/README.md`.
* `PellyCoder.Eval`은 코퍼스를 임시 폴더로 복사 → 작업마다 `FsmOrchestrator.RunAsync`(`--yes`) 실행 → 판정 → `summary.json`과 판정 결과를 모아 KPI 표 출력.
* **작업 구성 (M3b T4):** M3b는 **50건** = C# 30건(Legacy 18, Modern 12) + SQL 20건입니다. Legacy 18건의 앱 종류는 YAML `category`로 `mvc` 8 · `adonet` 5 · `dapper` 5입니다. 합격 판정(R4)의 범주는 이 `category`가 아니라 작업 id 접두사(cs-legacy·cs-modern·sql)입니다(M3b 판단 P2). **Modern 12건은 모두 동작 판정**을 가집니다(아래). 코퍼스에는 SQL 객체 넷을 더했고 C# 코퍼스는 바꾸지 않았습니다. 구성은 `Composition_Is30Cs20Sql_With18Legacy`가 검사합니다.
* **작업 구성 (M2b R9, 이전 단계):** M2는 **30건** = C# 18건(Legacy 10, Modern 8) + SQL 12건. 설계 초안의 "C# 30 + SQL 20"은 M2 완료 기준("30건 이상 실행 가능")에 맞춰 줄였고, 50건 확장은 M3b에서 했습니다(위). 시그니처를 바꾸는 요청(async 전환 등)은 넣지 않습니다 — MemberShapeGuard가 막는 의도된 동작입니다. SQL 매개변수 추가는 허용합니다.
* **실행기 사용법 (M2b T13):** `dotnet run --project eval/PellyCoder.Eval -- [--tasks <접두사|글롭,…>] [--kind cs|sql] [--out <폴더>] [--config <pelly.json>]`. 결과는 `eval/results/<yyyyMMdd-HHmmss>/`(git 추적 제외)의 `report.md`(KPI 표·실패 유형·작업별 결과), `results.json`, `work/<id>/`(작업별 코퍼스 복사본과 `.pelly/runs`). 종료 코드는 0 실행 완료 · 1 검증 불변 조건 위반 · 2 중단 · 4 설정 오류 또는 LLM 서버 연결 불가입니다. SQL 연결은 `PELLY_SQL_CONNECTION`, 허용 목록은 `--config`의 pelly.json에 둡니다.
* **SQL 작업은 허용 목록이 비어 있으면 건너뜁니다**(보고서에 사유). 실행기가 스스로 허용 목록을 넓히지 않습니다 — 허용 목록이 비면 연결이 없거나 목록에 없는 것과 같은 취급입니다 (M2b T13).
* **독립 재검증 (M2b T13):** 모델 실행이 종료 0으로 끝난 작업은 실행기가 결과 파일을 pelly와 별개 경로로 다시 검증합니다(C#: 빌드, SQL: 원본과 같은 방식의 NOEXEC 컴파일에서 새 오류 없음). 이것이 "검증 불변 조건"(종료 0 = 검증 통과)의 판정자입니다. **분모는 독립 검증이 `Passed` 또는 `Failed`인 종료 0 실행만**이고, 검증이 생략된 실행(Windows가 아닌 환경의 Legacy, 검증기 예외)은 분모에서 빼 따로 "독립 검증 생략 N건"으로 보입니다. 판정된 것이 하나도 없으면 "판정 불가"로 표시해 미검증 실행이 100%로 보이지 않게 합니다. 검증기 예외는 위반이 아니라 생략으로 기록합니다(검증기가 죽었다는 것이 수정이 틀렸다는 증거는 아님).
* **불변 조건은 독립 빌드·컴파일로만 판정합니다 (M3b T4 판정, 지시 T4-I1):** 불변 조건은 「승인된 패치는 항상 빌드/SQL 검증을 통과한다」(PRD §2.2)이므로 위반은 종료 0인데 독립 빌드(C#)나 NOEXEC 컴파일(SQL)이 `Failed`인 경우뿐입니다. 종료 0 작업이 동작 판정(`tests`·`failingTests`)이나 문자열 판정에서 떨어지면 **모델 실패**(실패 유형 `Judge`)이지 위반이 아닙니다. pelly는 숨겨진 판정 테스트(R2)를 볼 수 없으므로 거기에 책임질 수 없습니다. 동작 판정 도구 자체의 오류(판정 프로젝트 복원 실패, 테스트 프로젝트 없음, 테스트 호스트 기동 실패)는 독립 검증 **생략**이고 실패 유형 `JudgeTooling`으로 따로 세어 KPI 표의 최종 작업 성공률 칸에 「판정 도구 오류로 미판정 N건」으로 보입니다(일시적 NuGet 문제가 위반으로 세지지 않게 함). 판정 프로젝트의 빌드 오류·시간 초과·TRX 없는 종료는 실패입니다.
* **동작 판정 (M3b T3, R1~R3):** 동작 판정은 **Modern C# 작업에만** 둡니다. Legacy는 Mac에서 빌드·테스트를 할 수 없어 문자열 판정을 유지하고(M4에서 전체 판정), SQL은 모델 SQL을 실행해야 해서 DB 무변경 원칙과 부딪히므로 문자열 + NOEXEC 판정을 유지합니다.
  * **판정 프로젝트 둘** (코퍼스 밖 `eval/benchmark/judges/Modern/`, MSBuild 속성 `JudgeTargetRoot`로 대상 코퍼스를 바꿈. 코퍼스 CI와 `--test`가 원본에서 실패하는 판정 테스트를 보지 않게 함, R2): `Shop.Api.JudgeTests`(`Shop.Api`를 참조해 서비스·컨트롤러를 가짜 저장소·서비스로 실행)와 `Shop.Data.JudgeTests`(데이터 접근 코드를 소스로 컴파일해 가짜 Dapper·`SqlConnection`에 묶음). 후자는 DB 없이 보낸 SQL·매개변수와 결과를 보며, 가짜 DB가 SQL 안의 `CASE`·`OFFSET`·`IIF` 같은 다른 올바른 수정을 떨어뜨릴 수 있는 **알려진 거짓 실패 부류**가 있습니다(M3b T4 이월 항목).
  * **`judge.failingTests` (R3):** 수정 뒤 **실패해야 하는** 코퍼스 테스트(수정 전 동작을 고정한 `*_CurrentlyCancels` 등). 판정 프로젝트가 아니라 코퍼스 테스트 프로젝트에서 찾습니다. `judge.tests`는 모두 Passed, `failingTests`는 모두 Failed여야 하고 이름이 결과(TRX)에 없으면 실패입니다.
  * 동작 판정이 있는 작업에는 다른 올바른 수정을 떨어뜨릴 수 있는 문자열 판정을 두지 않습니다(cs-modern-001 `mustAdd: OrderStatus.Shipped`, cs-modern-002 `Math.Round`·`Math.Floor` 제거, T3).
* **반복 실행과 비교 (M3b T5·T6):** `--repeat N`은 같은 실행을 N회 돌려 `run-1/`…`run-N/`과 상위 `summary.md`·`summary.json`을 만듭니다. 요약은 평균 성공 작업 수·표본 표준편차(n − 1)·범주별(작업 id 접두사) 평균/최소/최대·작업별 회차 결과·회차별 불변 조건 위반·`Judge`·`JudgeTooling` 수·시간 중위값·실행 조건을 담고, `formatVersion`(현재 2, 1도 읽음. M4 준비에서 실행 조건에 Ollama 버전·모델 digest를 더하며 올림)이 없거나 모르는 값이면 읽지 않습니다. `dotnet run --project eval/PellyCoder.Eval -- compare <기준선 summary.json> <후보 summary.json>`은 R4 규칙을 ✓/✗로 판정합니다(종료 0 채택 · 3 거절 · 4 판정 불가). 중단된 반복(`cancelled`), 실행 조건·회차·범주 누락, 회차 수·작업 집합·회차별 실행 작업 수(`ran`)가 다른 요약은 판정하지 않고 사유를 출력합니다(거부, 종료 4). SQL은 반복 사이에 다시 배포하지 않고 한 번의 `--repeat`으로 돌리되 실행 전후 DB 객체(이름·종류·`modify_date`·정의 체크섬)를 비교해 무변경을 확인합니다.
* **R4 채택 규칙 (M3b R4, `eval/PellyCoder.Eval/Acceptance.cs`):** 튜닝 후보는 ① 3회 평균 성공 작업 수가 기준선보다 **1.0건 이상** 많고 ② 어떤 범주(cs-legacy·cs-modern·sql)도 평균이 0.5건 넘게 줄지 않고 ③ 3회 모두 검증 불변 조건 위반이 0건이고 ④ 작업 시간 중위값이 기준선의 1.5배 이하일 때만 채택합니다. 만족하는 후보가 없으면 기본값을 유지하고 그것도 결과로 기록합니다. 후보는 계획 단계에 정한 목록(R5, 상한 4개)에서만 고릅니다.
* Legacy 작업은 Mac에서 빌드 판정을 할 수 없으므로, 로컬 Mac에서는 "빌드 판정 생략" 모드(패치 적용·모양 검사까지만)로 측정합니다. 이때 검증 불변 조건의 분모는 (M2b·M3a의 30건 기준) Modern 8 + SQL 12 = 20건이었고, M3b의 50건 기준으로는 Modern 12 + SQL 20 = 32건(독립 검증 생략 18건 = Legacy)입니다. 전체 판정은 Windows 환경에서 실행합니다(CI 수동 워크플로 `benchmark-legacy`. LLM이 필요하므로 셀프 호스티드 러너가 생기기 전까지는 미실행). 폐쇄망 벤치마크는 같은 실행기로 M4에서 작성합니다.
* **M3b 기준선과 최종 KPI (M3b T5·T6):** `docs/m3b-baseline.md`(50건 × 3회, 2026-10-18). 평균 성공 **47.67건**(48 · 47 · 48, 표본 표준편차 0.58), 범주별 cs-legacy 18.00 · cs-modern 9.67 · sql 20.00, 작업 시간 중위값 1.90초, 검증 불변 조건 위반 3회 모두 0건, `JudgeTooling` 0건. 실패는 cs-modern-003(2/3)·007(0/3)·008(0/3)뿐이고 모두 빌드를 통과한 뒤 판정에서 떨어진 모델의 경계값 실수입니다. 튜닝 후보 3개(think 768 · code 샘플링 0.2/0.95 · DTO 예시 보강)는 모두 R4 규칙 1에서 거절돼(`docs/m3b-tuning.md`) **채택된 것이 없으므로 M3b의 최종 KPI는 기준선 그대로**입니다. 요약은 `deploy/benchmark-summary.md`로 반입 zip에 들어갑니다.
* **첫 측정(M2b 기준선):** `docs/m2b-benchmark.md` — 28/30(93.3%), 검증 불변 조건 위반 0건. 약한 판정 4건을 강화한 뒤 해당 작업만 다시 돌린 결과는 기준선을 바꾸지 않고 별도 기록으로 덧붙입니다(약한 판정으로 잰 사실을 숨기지 않기 위함).

* **스위트·사전 검사·반출 (M4 준비):** `suite.json`(`roots`·`supportFiles`·`resultsDir`)이 있는 폴더가 스위트이고 내장 스위트는 `eval/benchmark/suite.json`입니다. 작업의 `file`·`project`는 `<루트 이름>/<경로>`이고 Legacy 여부는 가장 가까운 csproj가 비 SDK 형식인지로 정합니다. 실행기 옵션은 `--suite <폴더>`·`--keep-work <all|failed|none>`이고, 하위 명령은 `validate [--deep]`(실행 전 정의 점검, `--deep`은 원본에서 판정 시험 실행), `compare`(digest·Ollama 버전이 다른 요약은 거부), `record`(`version-record.json` 작성, 이전 `deploy/record-versions.sh` 대체, `modelGgufSha256` 추가), `export`(결과를 비식별해 반출)입니다. 내장 스위트 회귀는 48/50, 위반 0건입니다(`docs/m4-suite-regression.md`). 실행기는 `pelly-eval.exe` 단일 파일로 현장 키트 zip(`pelly-field-kit-<버전>-win-x64.zip`)에 들어가 반입되며, 절차는 `deploy/field-guide.md`·`field-benchmark.md`·`field-results.md`입니다.

---

## 11. 구현 순서와 완료 기준

| 마일스톤 | 내용 | 완료 기준 |
| :--- | :--- | :--- |
| **M0** | 저장소·솔루션 골격, CI(unit 잡), 스파이크 S1~S5, 코퍼스 Modern/Legacy/Sql 초안, 벤치마크 YAML 10건 | 스파이크 문서 5개, CI 녹색, 코퍼스가 CI에서 빌드됨 |
| **M1** | 설정·Workspace·로그, OllamaBackend, TokenCounter, QwenStreamClient, Parser/Patcher, FileGuard, RoslynSlicer(기본), Transitions·Orchestrator, `pelly cs`(SDK 스타일), `doctor` 기본 | Modern 코퍼스에서 `pelly cs` 실제 모델로 1건 이상 성공, §10.2 해당 단위 테스트 통과 |
| **M2** | BuildToolResolver(레거시), Baseline 병렬화, BuildVerifier·FeedbackBuilder, 교정 루프·롤백·REVIEW, MemberShapeGuard, SymbolLocator(`--line` 포함), SqlSafetyGuard·SqlSyntaxVerifier·SqlSchemaSlicer, `pelly sql` | windows-integration·sql-integration 잡 녹색, 벤치마크 30건 이상 실행 가능 |
| **M3a** (M3를 둘로 나눔, M3a R1 — 튜닝은 DTO·`--test`가 들어간 최종 제품으로 해야 하고 패키지는 튜닝 뒤에 만들기 때문) | DTO 슬라이스(§8.5), `--test`(§8.9), `pelly report`·`--sanitize`(§8.12), doctor 완성(§8.13), `--verbose`, `--pause-on-exit`(§8.14), 대기 시간 분리(§8.2), 변화 없음 가드(§8.7), M2b 이월 항목 해소(SQL 취소 → 종료 2, 별칭 제외를 문장 단위로) | **달성(2026-10-18):** CI 4잡 녹색. 벤치마크 30건 회귀 측정(`docs/m3a-benchmark.md`): 최종 코드에서 30/30, 검증 불변 조건 위반 0건, 대기 0.02~0.03초. 실제 모델 E2E(`docs/m3a-e2e.md`): DTO·`--test`·리포트·비식별 통과. 요청이 테스트와 모순될 때 의미상 회피는 20회 중 5회(25%)로 받아들인 한계 |
| **M3b** | 벤치마크 50건(C# 30 + SQL 20)과 `judge.tests` 연동 판정, 그 결과로 하는 프롬프트·샘플링 튜닝, S6(`win-x64` 단일 파일 publish)·CI `publish-check` 잡(`doctor --offline-selftest`), S7(Rider External Tools 실기), 반입 패키지(SBOM·서드파티 라이선스·SHA-256·버전 기록·`pelly.json` 템플릿·zip 스크립트), `deploy/` 문서(설치·사용 가이드, IDE External Tools 가이드, "실행 중 외부 통신 없음" 명세) | **달성(2026-10-19, S7 결과와 사람 확인 사항 대기):** 벤치마크 50건(cs-legacy 18 · cs-modern 12 동작 판정 · sql 20)을 3회 반복한 기준선이 최종 KPI다 — 평균 성공 47.67건, 검증 불변 조건 위반 0건(`docs/m3b-baseline.md`). 튜닝 후보 3개는 R4로 모두 거절돼 기본값 유지(`docs/m3b-tuning.md`). CI 실행 6회(잡 키 5개) 녹색: `publish-check`가 배포본 exe로 자체 점검 6행 Ok, `package`가 `pelly-0.9.0-win-x64.zip`(13개 파일: 허용 목록·체크섬·SBOM(CycloneDX 6.3.1)·서드파티 라이선스 표·버전 기록)을 만든다. `deploy/` 설치·사용·IDE·외부 통신 없음 문서와 S6 결과(`docs/spikes/S6-single-file.md`)가 있다. **대기 중:** ① S7 Rider 실기 확인표는 준비됐으나 개발 Mac에 Rider가 없어 결과가 비어 있다 ② 라이선스 게이트에 **잠정 예외 하나**(`Microsoft.Data.SqlClient.SNI.runtime`, 네이티브 SNI의 Microsoft 재배포 라이선스)를 두었고 사람 확인 전까지 잠정이다. 또 버전 기록에는 GGUF SHA-256 대신 개발 PC 모델의 Ollama digest를 담았고(GGUF 해시는 M4), pelly 자체의 `LICENSE` 파일은 사람이 정할 때까지 zip에 넣지 않았다 |
| **M4 준비** | 이 계획의 산출물: 벤치마크 스위트 일반화(`--suite`·`--keep-work`), `validate`·`validate --deep`, 모델 identity(Ollama 버전·digest, 요약 `formatVersion` 2)와 `record`·`export`, doctor SQL 행의 인증 방식, 현장 절차 문서와 스위트 템플릿, `pelly-eval.exe` 단일 파일과 둘째 zip, CI 패키지 단계, 버전 `0.9.1` | 현장 키트 zip이 CI에서 생성되고 내장 스위트 회귀 결과가 `docs/m4-suite-regression.md`에 있음 |

각 마일스톤 안의 작업 순서와 단위는 구현 계획(writing-plans)에서 정합니다.

---

## 12. 구현자에게 맡기는 것

* 클래스 내부 구조, private 메서드 분할, 네이밍 세부.
* 정규식·휴리스틱의 세부 조정(테스트로 근거를 남길 것).
* Spectre 출력의 시각적 세부(색, 표 모양).
* 로그 이벤트의 세부 필드(§8.12의 필수 항목은 유지).
* 스파이크 결과에 따른 기본값 변경 — 이 문서의 해당 값과 PRD를 함께 갱신합니다.

## 13. 남은 확인 사항 (구현을 막지 않음)
* Legacy 벤치마크의 실제 모델 + 빌드 전체 판정은 Windows와 GPU가 함께 있는 환경이 필요합니다. 외부에서는 이 조합이 없으므로 M4 폐쇄망에서 처음으로 전체 판정됩니다(외부 KPI는 Modern·SQL과 Legacy 부분 판정 기준).
* 폐쇄망 정보(PRD Q9~Q13)는 M4 전까지 사용자가 확인합니다.
* 사용자는 폐쇄망 서버와 같은 모델을 Mac에 내려받아 `llm.model`로 지정합니다. 그 전까지 S1~S3은 이미 있는 `qwen3.6:35b-a3b-coding-nvfp4`로 진행하고, 같은 모델을 받은 뒤 S1·S2를 다시 확인합니다.
