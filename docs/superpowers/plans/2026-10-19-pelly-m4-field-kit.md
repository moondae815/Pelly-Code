# Pelly-Coder M4 (폐쇄망 이관 준비 — 현장 키트) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** M4 현장 작업(설치·점검·실제 코드 벤치마크·KPI 최종 판정·설정 튜닝·결과 반출)을 **재반입 없이** 끝낼 수 있게, 반입 전에 외부에서 만들 수 있는 것을 모두 만든다. 할 일은 다섯 가지다.
1. 벤치마크 실행기가 저장소 코퍼스가 아니라 **현장의 실제 코드베이스**를 대상으로 돌게 한다(스위트).
2. 현장에서 작업 정의를 실행 전에 검사하는 `validate`를 만든다.
3. 모델 파일 동일성(Ollama digest, GGUF SHA-256)을 실행 조건과 버전 기록에 남기고, Windows에서 그대로 쓰는 `record`로 바꾼다.
4. 벤치마크 결과를 폐쇄망 밖으로 가지고 나올 수 있게 비식별 `export`를 만든다.
5. 실행기를 단일 파일 `pelly-eval.exe`로 만들어 두 번째 zip(현장 키트)에 담고, 현장 절차 문서와 결과 양식을 함께 넣는다.

**Architecture:**
* **스위트(suite):** 폴더 하나에 `suite.json`(대상 코드 위치 = 루트 목록)과 `tasks/*.yaml`, 선택적으로 `judges/<루트>/`를 둔다. 작업의 `file`은 `<루트 이름>/<루트 안 경로>`다. 저장소의 내장 벤치마크도 `eval/benchmark/suite.json`을 가진 스위트 하나가 되어, 실행 경로가 하나뿐이다.
* **Legacy 판정**은 경로 접두사(`corpus/Legacy/`)가 아니라 대상 파일에서 가장 가까운 csproj의 프로젝트 형식으로 한다. 합격 판정 범주는 지금처럼 id 접두사(`cs-legacy`·`cs-modern`·`sql`)이고, `validate`가 둘이 맞는지 검사한다.
* **현장 키트 zip**은 `pelly-<버전>-win-x64.zip`과 따로 만든다(`pelly-field-kit-<버전>-win-x64.zip`). pelly 본 패키지의 허용 목록·심사 자료는 그대로 두고, 같은 스크립트(`deploy/build-package.ps1 -Package field-kit`)가 같은 라이선스 게이트로 만든다.

**Tech Stack:** .NET 10 (self-contained single-file publish, `win-x64`), xUnit v3, YamlDotNet(기존), CycloneDX .NET 6.3.1(기존 고정), PowerShell 7, GitHub Actions `windows-latest`, Ollama 0.40.0, SQL Server 2022 컨테이너

**Spec:**
* 설계 `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md`는 범위를 M0~M3으로 두고 **M4는 산출물 요구만 반영**한다(머리말). 그래서 이 계획의 근거는 주로 PRD다.
  * 설계 §10.6(실행기), §11 M3b 행(대기 항목), §13(Legacy 전체 판정·폐쇄망 정보는 M4)
* PRD `docs/PRD.md`:
  * §9 Milestone 4: 설치, `pelly doctor`로 서버·빌드 도구·SQL·**Windows 인증** 점검, 실제 코드 기반 폐쇄망 벤치마크 작성·실행 → KPI 최종 판정, 설정으로 하는 튜닝은 폐쇄망 안에서, 코드 수정은 비식별 리포트 반출 → 외부 수정 → 재반입
  * §2.1: 같은 모델 파일(GGUF SHA-256 일치)과 같은 Ollama 버전으로 측정, 폐쇄망 세트는 외부와 같은 규모
  * §8.1: "평가 실행기는 폐쇄망에서도 그대로 동작해야 합니다"
  * §11.1: 버전 기록의 GGUF SHA-256은 M4에서 현장 모델로 기록
  * §11.3: 반입본마다 버전 기록을 남겨 폐쇄망 결과와 외부 결과를 대조
* M3b 결과: `docs/m3b-baseline.md`, `docs/m3b-tuning.md`, `deploy/*`, 원장 사본 `/private/tmp/claude-501/-Users-payletter-git-root-ReSet/80aff902-238e-4e70-8e26-123252df0fa0/scratchpad/m3b-ledger.md`(Task 5 minor: 실행 조건에 Ollama 버전 없음 → Task 4에서 닫음)

**범위 밖 (현장 또는 사람이 함):**
* 폐쇄망 설치·현장 확인·실제 코드 벤치마크 작성과 실행, KPI 최종 판정(Q6 확정), Visual Studio External Tools 실기 확인 — 이 계획은 그 도구와 절차·양식만 만든다.
* **사람 결정 (반입 전 필요, 이 계획의 작업이 아님):**
  1. SNI 라이선스 잠정 예외 승인 또는 거부(거부하면 관리 코드 SNI로 바꾸는 별도 작업)
  2. pelly 자체 `LICENSE`
  3. S7 Rider 실기 결과
  4. 폐쇄망 정보 Q9~Q13
  5. 반입 신청
* `1.0.0` 승격: 현장 확인 뒤 별도로 한다. 라이선스 게이트는 `1.0.0`에서 잠정 승인을 거부한다(M3b).
* Legacy 작업의 **테스트** 판정: 지금처럼 하지 않는다. Legacy는 Windows에서 빌드 + 문자열 판정이 전체 판정이다(PRD §8.1 "빌드 전체 판정"). `validate`가 Legacy 작업의 동작 판정을 오류로 막는다(조용히 생략되지 않게).

## Global Constraints

* **이어받는 제약:** M1~M3b 계획의 Global Constraints를 그대로 따른다.
  * 빌드 환경: SDK `10.0.302`(`global.json`, rollForward latestFeature), `net10.0`, Nullable·ImplicitUsings·TreatWarningsAsErrors
  * 패키지: CPM, **새 NuGet 패키지를 더하지 않는다**(라이선스 게이트·SBOM이 바뀐다)
  * Core 규칙: `Console.*` 금지, 외부 프로세스는 `IProcessRunner`, DB는 `ISqlExecutor`
  * 종료 코드: pelly는 0~5. 실행기는 지금 규약(0 완료 · 1 불변 조건 위반 · 2 중단 · 3 compare 거절 · 4 설정 오류)을 지키고, 새 하위 명령도 이 안에서 정한다(각 작업에 적음).
  * 언어: 식별자·커밋은 영어, 문서·사용자 메시지는 한국어
  * 커밋 끝에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
  * 정규식은 `\A…\z`와 `[0-9]`로 쓴다.
  * 실기 Windows에서 못 잰 경로는 「Windows 미확인」으로 표시한다.
* **실행 중 외부 통신 없음(PRD §5):**
  * `pelly-eval.exe`도 반입 실행 파일이다. 설정된 Ollama·SQL 서버에만 접속한다.
  * HTTP 클라이언트는 `eval/PellyCoder.Eval/Program.cs`에서만 만든다. 새 하위 명령은 거기서 만든 `OllamaBackend`를 받는다(Task 8이 정책 테스트로 고정한다).
* **비밀·개인 정보:**
  * 연결 문자열·비밀번호·접속 주소·로그인 이름은 커밋 파일, zip, `export` 결과, 버전 기록에 넣지 않는다.
  * 저장소의 기존 테스트용 SQL 비밀번호는 지금처럼 환경 변수로만 넘긴다.
* **회귀 차단(PRD §8.2):**
  * 이 계획은 프롬프트·샘플링·슬라이서를 바꾸지 않는다. 그래서 3회 반복 재측정은 하지 않는다.
  * 실행기 경로가 바뀌므로, Task 2에서 내장 스위트 50건을 1회 실행해 M3b 기준선과 같은 결과인지 확인한다(기준은 Task 2에 적음).
* **버전:** 반입본 버전은 `0.9.1`(Task 10). M3b 뒤 pelly에 들어간 수정(값 없는 옵션 거부, 텔레메트리 끄기)과 이 계획의 doctor 변경이 `0.9.0` 반입본과 다르기 때문이다.
* **zip 이름:**
  * `pelly-<버전>-win-x64.zip`: 지금 그대로
  * `pelly-field-kit-<버전>-win-x64.zip`: 새로 만든다
  * 둘 다 허용 목록에 있는 파일만 담는다.
* **로컬 실행 환경(Mac):**
  * Ollama `http://localhost:11434`, 모델 `qwen3.6:35b-a3b-coding-nvfp4`
  * SQL 컨테이너 `pelly-corpus-sql`(`localhost,14333`)
  * 실제 모델·DB 테스트 변수: `PELLY_TEST_SQL`, `PELLY_TEST_OLLAMA`, `PELLY_TEST_MODEL`
  * 벤치마크 SQL 연결: `PELLY_SQL_CONNECTION`
  * 허용 목록은 저장소 밖 `--config` 파일에 둔다(M3b와 같음).
* **CI:** 작업마다 마치기 전에 push해서 모든 잡이 녹색인지 확인한다. 잡 키 5개(unit은 macOS·Windows 매트릭스)는 그대로이고, Task 9가 `publish-check`·`package` 잡에 단계를 더한다.
* **테스트 게이트:** 마칠 때 다음 세 가지를 확인한다.
  * `dotnet build -warnaserror`: 경고 0
  * 전체 `dotnet test`: 실패 0
  * 실제 모델·DB 변수를 넣은 실행: 실패 0. 단, `Test_ContradictingEdit_FailsWithTestErrors_Restored`는 문서에 남긴 모델 우회라 예외다. 이 테스트가 실패하면 같은 커밋 기준 `main`에서도 같은 비율로 실패하는지 대조해 원장에 적는다.

## Review Focus

현장에서 가장 먼저 부딪힐 입력 다섯 가지와, 기대하는 동작이다.

1. **실제 저장소 루트에 `.git`·`.vs`·거대한 폴더가 있음:**
   * 작업마다 루트를 복사하므로 `.git`·`.vs`는 어느 깊이에서든 빼야 한다. `bin`·`obj`·`.pelly`·`TestResults`는 지금처럼 뺀다.
   * `packages/`(packages.config 복원본)는 빼지 않는다. 폐쇄망에서 다시 받을 수 없다.
   * 그 밖에 뺄 폴더는 `suite.json`의 `exclude`로 지정한다.
   * 테스트 위치: Task 1 `WorkCopyTests`
2. **루트 바깥 조상 폴더에 빌드 입력이 있음:**
   * 대상: `Directory.Build.props`/`.targets`, `Directory.Packages.props`, `global.json`, `NuGet.config`
   * 복사본에는 이 파일이 따라가지 않아 빌드가 조용히 달라진다. `validate`가 경고로 파일 경로를 알려 주고 `supportFiles`에 넣으라고 안내해야 한다.
   * 테스트 위치: Task 3
3. **CP949·BOM·CRLF 원본:**
   * 현장 코드는 CP949일 수 있다. `validate`와 대상 찾기는 pelly와 같은 `TextFileCodec`으로 읽어야 하고, CP949 파일에서도 메서드·SQL 객체를 찾아야 한다.
   * 테스트 위치: Task 3
4. **Windows 절대 경로·공백·한글이 든 루트 경로:**
   * 예: `"D:\\소스\\ERP 본사"`. 루트 해석·복사·작업 파일 경로가 모두 그대로 동작해야 한다.
   * 테스트 위치: Task 1. Mac에서 쓸 수 있는 공백·한글 폴더로 시험하고, 드라이브 문자는 「Windows 미확인」으로 둔다.
5. **반출 결과에 코드·경로·요청문이 섞임:**
   * `export` 결과의 어떤 파일에도 원본 결과의 다음 문자열이 남으면 안 된다.
     * 사유(`reasons`)
     * 검증 상세(`independent.detail`)
     * 생략 사유 상세
     * 출력 폴더 경로
     * 접속 주소
   * 테스트 위치: Task 6. 심어 둔 표식 문자열이 결과 어디에도 없는지 확인한다.

---

## 작업 사이 인터페이스 (요약)

| 만드는 작업 | 이름 | 쓰는 작업 |
| :--- | :--- | :--- |
| 1 | `BenchmarkSuite`, `SuiteRoot`, `SuiteTask`, `SuiteLoad`, `BenchmarkSuite.Load/Resolve`, `EvalPaths.FindSuite`, 바뀐 `WorkCopy.Prepare/CopyJudges`, `RunnerSetup.Suite` | 2, 3, 4, 5, 6 |
| 1 | `EvalArguments.Command`·`Suite` (하위 명령 틀) | 2, 3, 4, 5, 6 |
| 3 | `SuiteValidator.Validate`, `SuiteValidator.SchemaIssues`, `SuiteIssue`, `IssueLevel` | 4, 8 |
| 5 | `LlmServerStatus.ModelDigest/ModelFormat/ModelQuantization`, `RunConditions.OllamaVersion/ModelDigest`, summary `formatVersion` 2 | 6, 10 |
| 8 | `deploy/field-guide.md` 등 문서·템플릿 | 9 |

---

### Task 1: 스위트 — 정의·해석·실행기 연결

**Files:**
- Create: `eval/PellyCoder.Eval/BenchmarkSuite.cs`, `eval/benchmark/suite.json`
- Modify: `eval/PellyCoder.Eval/BenchmarkTask.cs` (`IsLegacy` 삭제), `WorkCopy.cs`, `BenchmarkRunner.cs`, `IndependentVerifier.cs`, `EvalPaths.cs`, `EvalArguments.cs`, `Program.cs`, `Models.cs` (`TaskResult.Skipped/EnvironmentSkipped/Failed`가 `SuiteTask`를 받음)
- Modify: `eval/benchmark/tasks/*.yaml` 50개 — `file`의 앞 `corpus/`만 지운다(`corpus/Modern/...` → `Modern/...`)
- Modify: `eval/benchmark/README.md` (스키마 표의 `file` 행, `suite.json` 절)
- Modify: `BenchmarkTask.IsLegacy`를 쓰는 곳과, `task.File`을 `eval/` 기준 경로로 쓰는 모든 테스트(`tests/PellyCoder.Integration.Tests/Benchmark/*`, `tests/PellyCoder.Eval.Tests/*`). 구현자가 `grep -rn "IsLegacy\|\.File\b" eval tests`로 전부 찾는다.
- Test: `tests/PellyCoder.Eval.Tests/BenchmarkSuiteTests.cs`, `WorkCopyTests.cs`(기존 갱신)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record SuiteRoot(string Name, string Path, IReadOnlyList<string> Exclude); // Path: 절대 경로
  public sealed record SuiteTask(BenchmarkTask Task, SuiteRoot Root, string SourceFile, string RelativeFile, bool IsLegacy);
  // SourceFile: 원본의 절대 경로, RelativeFile: 루트 안 경로('/' 구분)
  public sealed record SuiteLoad(BenchmarkSuite? Suite, IReadOnlyList<string> Errors);
  public sealed class BenchmarkSuite
  {
      public const string FileName = "suite.json";
      public string Dir { get; }                       // suite.json이 있는 폴더 (절대)
      public string Name { get; }
      public IReadOnlyList<SuiteRoot> Roots { get; }
      public IReadOnlyList<string> SupportFiles { get; } // 절대 경로
      public string ResultsDir { get; }                 // 절대 경로
      public string TasksDir { get; }                   // <Dir>/tasks
      public string JudgesDir(SuiteRoot root);          // <Dir>/judges/<root.Name>
      public static SuiteLoad Load(string dir);
      public SuiteTask? Resolve(BenchmarkTask task, IFileSystem fs, out string? error);
  }
  public static class EvalPaths { public static string? FindSuite(string start); public static string? FindSuite(); }
  // WorkCopy
  public static string Prepare(BenchmarkSuite suite, SuiteTask task, string workRoot);  // 복사된 대상 파일
  public static string? CopyJudges(BenchmarkSuite suite, SuiteRoot root, string workRoot);
  // EvalArguments: + string Command ("run" 기본 | "validate" | "record" | "export" | "compare"),
  //                 + string? Suite, + IReadOnlyList<string> Positional (export 입력 폴더, compare 두 파일)
  // 모든 하위 명령의 인자는 EvalArguments.Parse 하나가 읽는다. 첫 인자가 하위 명령 이름이면 Command가 된다.
  // 하위 명령마다 허용 옵션이 다르고, 그 명령에 없는 옵션은 "알 수 없는 인자" 오류다. Task 3·5·6이 옵션을 더한다.
  // compare는 지금의 CompareCommand.Run을 그대로 쓰되 Positional 2개를 Parse가 확인한다.
  ```
  * `RunnerSetup`의 `string EvalRoot`는 `BenchmarkSuite Suite`로 바뀐다.
  * `BenchmarkRunner.RunAsync(SuiteTask task, string outDir, CancellationToken ct)`
  * `IndependentVerifier`의 `string? evalRoot`는 `BenchmarkSuite? suite`로 바뀐다.

- [ ] **Step 1: `suite.json` 형식을 정하고 내장 스위트 파일을 만든다**

  `eval/benchmark/suite.json`(경로는 `suite.json`이 있는 폴더 기준 상대 경로 또는 절대 경로):
  ```json
  {
    "formatVersion": 1,
    "name": "builtin",
    "roots": [
      { "name": "Modern", "path": "../corpus/Modern" },
      { "name": "Legacy", "path": "../corpus/Legacy" },
      { "name": "Sql", "path": "../corpus/Sql" }
    ],
    "supportFiles": ["../corpus/Directory.Build.props", "../corpus/Directory.Packages.props", "../../global.json"],
    "resultsDir": "../results"
  }
  ```

  **읽기 규칙:** `BenchmarkSuite.Load`는 예외를 던지지 않고 `Errors`로 돌려준다.
  * `formatVersion`: 1만 읽는다.
  * `name`: 필수, 빈 문자열이면 안 된다.
  * `roots`: 하나 이상.
    * 이름은 `\A[A-Za-z0-9_-]+\z` 형식이고 서로 달라야 한다(대소문자 무시).
    * 경로는 존재하는 폴더여야 한다.
    * 두 루트가 같은 폴더이거나 한쪽이 다른 쪽 안에 있으면 오류다.
    * `exclude`: 선택. 폴더 **이름** 목록이다(경로가 아님).
  * `supportFiles`: 선택. 존재하는 파일이어야 하고, 파일 이름이 서로 달라야 한다(작업 폴더 한곳에 복사되므로).
  * `resultsDir`: 선택, 기본 `results`.
  * 모르는 키는 오류다(현장 오타가 조용히 무시되지 않게).

- [ ] **Step 2: 실패하는 테스트를 쓴다 (`BenchmarkSuiteTests`)**
  * `Load_BuiltInSuite_HasThreeRootsAndSupportFiles`
    * 루트 이름이 `Modern`·`Legacy`·`Sql` 순서다.
    * `SupportFiles`의 파일 이름이 `Directory.Build.props`·`Directory.Packages.props`·`global.json`이다.
    * `ResultsDir`가 `<repo>/eval/results`로 끝난다.
  * `Load_RejectsUnknownKey_NestedRoots_DuplicateNames_MissingPath`: 임시 폴더 네 개로 확인한다. 각각 오류가 하나 이상 나오고, `Suite`가 null이다.
  * `Load_RootPathWithSpaceAndKorean_Resolves`: 루트 폴더 이름이 `ERP 본사/소스`다(Review Focus 4).
  * `Resolve_FileStartsWithRootName_GivesAbsoluteSourceFile`: `file: "Modern/Shop.Api/Services/OrderService.cs"`이면 다음과 같다.
    * `SourceFile`이 `<repo>/eval/corpus/Modern/Shop.Api/Services/OrderService.cs`다.
    * `RelativeFile`이 `Shop.Api/Services/OrderService.cs`다.
  * `Resolve_UnknownRoot_Or_MissingFile_Or_DotDot_IsError`
    * 알 수 없는 루트 이름(`Nope/x.cs`), 없는 파일, `Modern/../Legacy/x.cs`처럼 루트를 벗어나는 경로는 각각 null과 `error` 메시지를 돌려준다.
    * `error`에는 작업 id가 들어 있어야 한다.
  * `Parse_SubcommandFrame` (`EvalArgumentsTests`, 없으면 만든다)
    * `["validate", "--suite", "x"]` → `Command == "validate"`, `Suite == "x"`
    * `["--suite", "x"]` → `Command == "run"`
    * `["compare", "a", "b"]` → `Positional == [a, b]`, `["compare", "a"]` → 오류
    * `["validate", "--repeat", "3"]` → 오류 `알 수 없는 인자: --repeat` (그 명령에 없는 옵션)
  * `Resolve_BuiltInTasks_LegacyIsTheProjectStyle`
    * 내장 50건을 `Resolve`하면 `IsLegacy`가 true인 것이 18건이고, 모두 id가 `cs-legacy-`로 시작한다.
    * sql 작업은 `IsLegacy`가 false다.

- [ ] **Step 3: 실패를 확인한다**

  Run: `dotnet test tests/PellyCoder.Eval.Tests -- --filter-class "*BenchmarkSuiteTests"`
  Expected: 컴파일 실패(`BenchmarkSuite` 없음)

- [ ] **Step 4: `BenchmarkSuite`를 구현한다**
  * JSON은 `System.Text.Json`으로 읽는다(`JsonSerializerOptions { UnmappedMemberHandling = Disallow }`, camelCase).
  * `Resolve`의 `IsLegacy`
    * `Kind == "cs"`이고 `ProjectFileLocator.FindNearestCsproj`(Core)가 찾은 csproj가 `ProjectStyleDetector.Detect == ProjectStyle.Legacy`일 때만 true다.
    * csproj가 없으면 false다. 오류는 Task 3의 `validate`가 낸다.
  * `file`의 경로 구분자는 `/`만 쓴다. `\`가 있으면 오류다.

- [ ] **Step 5: 50개 YAML의 `file`에서 `corpus/`를 지우고, `BenchmarkTask.IsLegacy`를 삭제한다**

  `BenchmarkTask.File`의 문서 주석을 "`<루트 이름>/<루트 안 경로>`, `/` 구분"으로 바꾼다.

- [ ] **Step 6: 실행기를 스위트로 바꾼다**
  * **`WorkCopy.Prepare(suite, task, workRoot)`**
    * `SupportFiles`를 `workRoot/`에 복사한다(파일 이름 그대로).
    * 루트 폴더를 `workRoot/<root.Name>/`에 복사한다.
    * 빈 `.svn` 마커를 만들고(지금과 같음), 복사된 대상 파일 경로를 돌려준다.
  * **복사 제외** (이름, 대소문자 무시, 어느 깊이에서든): `bin`·`obj`·`.pelly`·`TestResults`·`.git`·`.vs`와 `root.Exclude`
  * **`CopyJudges(suite, root, workRoot)`**: `suite.JudgesDir(root)`를 `workRoot/judges/<root.Name>/`에 복사한다. 판정 프로젝트의 `JudgeTargetRoot` 전달 방식은 그대로다.
  * **`BenchmarkRunner`**
    * `corpusRoot`는 `workRoot/<root.Name>`이다.
    * 복원(`dotnet restore <sln>`)과 결과의 `IsLegacy`는 `SuiteTask.IsLegacy`를 쓴다.
  * **`Program.cs`**
    * `--suite <폴더>`를 받는다. 없으면 `EvalPaths.FindSuite()`를 쓴다. 이 메서드는 현재 폴더, 그다음 실행 파일 폴더에서 위로 올라가며 `eval/benchmark/suite.json`을 찾는다.
    * `--suite` 폴더에 `suite.json`이 없거나 `Load` 오류가 있으면 오류를 모두 출력하고 종료 4다.
    * `Resolve`에 실패한 작업은 실행하지 않고 `TaskResult.Failed(…, "Definition")`으로 기록한다(지금 "원본에서 대상을 찾지 못함"과 같은 취급).
    * 기본 출력 폴더: `<suite.ResultsDir>/<yyyyMMdd-HHmmss>/`
  * **`EvalArguments.Usage`**: `--suite` 줄을 더한다. 문구는 "`--suite <폴더>` 스위트 폴더(suite.json이 있는 곳, 기본: 저장소의 eval/benchmark)"다.

- [ ] **Step 7: `WorkCopyTests`를 갱신·추가한다**
  * `Prepare_BuiltInModernTask_SameLayoutAsBefore`: cs-modern-001의 작업 폴더가 다음을 갖는다. M3b와 같은 배치다.
    * `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.svn/`
    * `Modern/Shop.Api/Services/OrderService.cs`
  * `Prepare_ExcludesGitAndVsAtAnyDepth_KeepsPackages` (Review Focus 1)
    * 임시 루트에 `.git/HEAD`, `sub/.vs/x`, `sub/bin/a.dll`, `packages/Foo.1.0/lib/a.dll`, `extra/big.bin`, `src/A.cs`를 만들고 `exclude: ["extra"]`를 준다.
    * 복사본에 `packages/…/a.dll`과 `src/A.cs`만 남는다.
  * `CopyJudges_UsesSuiteJudgesFolder`: 내장 스위트의 `Modern` 루트에서 `judges/Modern/Shop.Api.JudgeTests`가 복사된다.

- [ ] **Step 8: 전체 테스트를 실행한다**

  Run: `dotnet build -warnaserror && dotnet test`
  Expected: 실패 0. `BenchmarkDefinitionTests`·`JudgeTestDefinitionTests`·`CorpusInvariantTests`가 새 `file` 형식으로 통과한다. 이 테스트들은 `BenchmarkSuite`로 경로를 해석하도록 고친다.

- [ ] **Step 9: Commit** — `feat(eval): benchmark suites — suite.json roots, root-relative task files, project-style Legacy`

---

### Task 2: `--keep-work`와 내장 스위트 회귀 실행

**Files:**
- Modify: `eval/PellyCoder.Eval/EvalArguments.cs`, `Program.cs`, `BenchmarkRunner.cs`
- Test: `tests/PellyCoder.Eval.Tests/BenchmarkRunnerTests.cs`, `EvalArgumentsTests.cs`(없으면 만든다)
- Create: `docs/m4-suite-regression.md`

**Interfaces:**
- Consumes: Task 1의 `BenchmarkRunner.RunAsync(SuiteTask, …)`
- Produces:
  ```csharp
  public enum KeepWork { All, Failed, None }
  // EvalArguments + KeepWork KeepWork = KeepWork.All
  ```

- [ ] **Step 1: 실패하는 테스트**
  * `Parse_KeepWork_AcceptsAllFailedNone_RejectsOther`: `--keep-work failed`는 `KeepWork.Failed`, `--keep-work x`는 오류 "`--keep-work는 all, failed, none 중 하나: x`"다.
  * `Runner_KeepWorkFailed_DeletesPassedTaskFolder_KeepsFailed`
    * 기존 `BenchmarkRunnerTests`의 가짜 백엔드·가짜 검증기로 성공 1건과 실패 1건을 돌린다.
    * `work/<성공 id>`는 없고 `work/<실패 id>`는 남는다.
    * 두 결과 행은 모두 `results.json`에 그대로 있다.
  * `Runner_KeepWorkNone_DeletesBoth_AfterArtifactsAreRead`: 결과의 `Attempts`·`TtftSeconds`가 삭제 전에 읽은 값이다.

- [ ] **Step 2: 실패 확인** — `dotnet test tests/PellyCoder.Eval.Tests`가 컴파일 실패한다.

- [ ] **Step 3: 구현**
  * 작업 하나가 끝나 `TaskResult`가 정해진 **뒤에** 정책대로 `work/<id>`를 지운다.
    * `Failed` 정책: `Status == Passed`인 작업만 지운다.
    * `None` 정책: 모두 지운다.
  * 삭제가 실패하면(파일 잠금) 경고 한 줄만 출력하고 계속한다. 결과는 바꾸지 않는다.
  * Ctrl+C로 중단된 작업의 폴더는 지우지 않는다.
  * `Usage`에 "`--keep-work <all|failed|none>` 작업 복사본 보존 (기본 all; 현장 실제 코드에서는 failed 권장)"을 더한다.

- [ ] **Step 4: 테스트 통과 확인** — `dotnet test tests/PellyCoder.Eval.Tests`: 실패 0

- [ ] **Step 5: 내장 스위트 50건 1회 실제 실행 (Task 1·2 회귀 확인)**

  ```bash
  export PELLY_SQL_CONNECTION="<로컬 컨테이너 연결, 비밀번호는 문서에 쓰지 않음>"
  dotnet run --project eval/PellyCoder.Eval -- --config <저장소 밖 pelly.json> --keep-work failed
  ```

  **기대 결과:**
  * 종료 0
  * 검증 불변 조건 위반 0건
  * cs-legacy 18/18(Mac이라 빌드 판정 생략)
  * sql 20/20
  * cs-modern 8건 이상. M3b 기준선은 9.67이고, 실패는 003·007·008의 경계값 실수뿐이었다.
  * 생략 0건(SQL 연결과 판정 도구 정상)

  **못 미칠 때:** 실패 작업이 003·007·008 밖이거나 실패 유형이 `Definition`·`RunnerError`이면 실행기 회귀다. 고친 뒤 다시 돌린다.

  결과 폴더 이름, KPI 표, 기준선과의 범주별 차이를 `docs/m4-suite-regression.md`에 적는다.

- [ ] **Step 6: Commit** — `feat(eval): --keep-work; record the built-in suite regression run`

---

### Task 3: `validate` — 실행 전 작업 정의 검사

**Files:**
- Create: `eval/PellyCoder.Eval/SuiteValidator.cs`, `eval/PellyCoder.Eval/ValidateCommand.cs`
- Modify: `eval/PellyCoder.Eval/BenchmarkTask.cs` (`LoadAll`이 파일별 YAML 오류를 모아 돌려주는 `TryLoadAll` 추가), `EvalArguments.cs`, `Program.cs`
- Modify: `tests/PellyCoder.Integration.Tests/Benchmark/BenchmarkDefinitionTests.cs` — 검사 로직을 `SuiteValidator`로 옮긴다. 남는 테스트는 둘이다.
  * `BuiltInSuite_HasNoIssues`: 오류 0, 경고 0
  * `Composition_Is30Cs20Sql_With18Legacy`: 그대로
- Test: `tests/PellyCoder.Eval.Tests/SuiteValidatorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `BenchmarkSuite`, `SuiteTask`
- Produces:
  ```csharp
  public enum IssueLevel { Error, Warning }
  public sealed record SuiteIssue(IssueLevel Level, string? TaskId, string Message);
  public static class SuiteValidator
  {
      /// 파일을 보지 않는 정의 검사 (id·kind·judge 규칙). 템플릿 검사(Task 8)도 이것을 쓴다.
      public static IReadOnlyList<SuiteIssue> SchemaIssues(BenchmarkTask task, string? fileName);
      public static IReadOnlyList<SuiteIssue> Validate(BenchmarkSuite suite, IReadOnlyList<BenchmarkTask> tasks,
          IReadOnlyList<string> loadErrors, IFileSystem fs, int compatLevel);
  }
  public static (IReadOnlyList<BenchmarkTask> Tasks, IReadOnlyList<string> Errors) TryLoadAll(string tasksDir); // BenchmarkTask
  ```

- [ ] **Step 1: 실패하는 테스트 (`SuiteValidatorTests`)**

  임시 스위트(루트 `App`, SDK csproj 하나, Legacy csproj 하나, sql 파일 하나)로 작업을 하나씩 바꿔 가며, 기대 메시지(부분 문자열)와 레벨을 확인한다.

  **오류(Error)여야 하는 것:**
  * YAML 구문 오류 → `"<파일 이름>: YAML을 읽을 수 없음"`
  * id 형식(`\A(cs-(modern|legacy)|sql)-[0-9]{3}\z`) 위반, id ≠ 파일 이름, 중복 id
  * 빈 `request`, `judge` 없음
  * kind 규칙
    * `cs`: `method`와 `line` 중 정확히 하나, `object` 없음, `judge.build: true`, `sqlCompile` 없음
    * `sql`: `method` 없음, id가 `sql-`로 시작, `judge.sqlCompile: true`, `build` 없음
    * 그 밖의 kind
  * 해석 실패(Task 1 `Resolve`의 `error`)
  * cs 대상 파일 위에 csproj가 없음 → `"csproj를 찾지 못함"`
  * id 접두사와 프로젝트 형식이 다름 → `"cs-legacy 작업인데 대상 프로젝트가 SDK 형식"`, 반대 경우도 같다.
  * Legacy 작업에 `tests`·`failingTests`가 있음 → `"Legacy 작업에는 동작 판정을 둘 수 없음 (Windows에서도 생략됨)"`
  * `line`이 파일 줄 수 밖
  * 대상을 찾지 못함
    * cs: `TargetText.ForCs`가 null
    * sql: 대상 객체 배치가 정확히 1개가 아니거나 그 배치에 구문 오류가 있음
  * 내용 판정·동작 판정이 하나도 없음
  * `mustContain`이 원본 대상에 이미 있음, `mustNotContain`이 원본 대상에 없음

  **경고(Warning)여야 하는 것:**
  * 루트의 **조상** 폴더에 빌드 입력이 있는데 `supportFiles`에 같은 이름이 없음. 대상은 `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `NuGet.config`(대소문자 무시)다. 메시지는 `"루트 밖 빌드 입력: <절대 경로> — supportFiles에 넣지 않으면 작업 복사본에 없습니다"`다(Review Focus 2). **조상 탐색은 스위트 폴더와 루트 폴더의 공통 조상에서 멈추지 않고 파일 시스템 루트까지 간다.**
  * 루트 안에 `packages.config`가 있는데 그 루트 바로 아래 `packages/` 폴더가 없음 → `"packages.config가 있지만 packages/ 폴더가 없습니다 (폐쇄망에서 복원할 수 없음)"`
  * 범주(`cs-legacy`·`cs-modern`·`sql`) 중 작업이 0건인 것

  **CP949 (Review Focus 3):**
  * `Validate_Cp949SourceWithCrlf_FindsMethod`: 한글 주석이 든 CP949 + CRLF C# 파일에서 `method`를 찾고, 오류가 0이다.
  * 같은 조건의 `.sql` 파일에서 `object`를 찾는다.

- [ ] **Step 2: 실패 확인** — 컴파일 실패(`SuiteValidator` 없음)

- [ ] **Step 3: 구현**
  * 원본은 `TextFileCodec.Read(fs, path).Text`로 읽는다. BOM을 빼고 CRLF를 `\n`으로 바꾼 뒤 `TargetText`와 `SqlBatchSplitter`에 넘긴다(기존 `BenchmarkDefinitionTests`와 같은 정규화).
  * `compatLevel`은 실행 설정(`options.Sql.CompatLevel`)에서 받는다.
  * 검사 순서는 작업 id 순이다. 한 작업에서 해석이 실패하면 그 작업의 파일 검사는 건너뛴다.

- [ ] **Step 4: `validate` 하위 명령**

  `dotnet run --project eval/PellyCoder.Eval -- validate [--suite <폴더>] [--config <pelly.json>]`
  * LLM·SQL에 접속하지 않는다. `--config`는 `sql.compatLevel`을 읽는 데만 쓴다.
  * 출력은 세 부분이다.
    1. 머리 한 줄: `스위트 <name> (<Dir>) · 작업 <N>건 · cs-legacy <a> · cs-modern <b> · sql <c>`
    2. 이슈가 있으면 Markdown 표 `| 레벨 | 작업 | 내용 |`(오류 먼저, 다음 경고, 각각 작업 id 순)
    3. 끝 줄: `오류 <e>건 · 경고 <w>건`
  * 종료 코드: 오류가 없으면 0(경고는 있어도 됨), 오류가 있으면 4. 스위트를 못 읽어도 4다.
  * `Usage`에 `validate` 절을 더한다.

- [ ] **Step 5: 통합 테스트를 옮긴다**
  * `BenchmarkDefinitionTests`의 다음 테스트를 지운다. `SuiteValidatorTests`와 `BuiltInSuite_HasNoIssues`가 대신한다.
    * `AllTasks_ParseAndMatchSchema`
    * `AllTasks_TargetsExist`
    * `TaskIds_AreUnique`
    * `AllTasks_HaveContentCriterion`
    * `SqlTasks_TargetIsAnObjectBatch`
    * `Judges_RejectUnmodifiedTarget`
  * `BuiltInSuite_HasNoIssues`는 이슈 목록 전체를 실패 메시지로 출력한다.

- [ ] **Step 6: 전체 테스트** — `dotnet build -warnaserror && dotnet test`: 실패 0

- [ ] **Step 7: Commit** — `feat(eval): validate — offline task-definition checks for field suites`

---

### Task 4: `validate --deep` — 판정 테스트를 원본에서 실행

**Files:**
- Modify: `eval/PellyCoder.Eval/SuiteValidator.cs`, `ValidateCommand.cs`, `EvalArguments.cs`
- Modify: `tests/PellyCoder.Integration.Tests/Benchmark/JudgeTestDefinitionTests.cs` — 본문을 `SuiteValidator.ValidateJudgesAsync` 호출로 바꾼다(같은 단언: 내장 스위트 Modern 12건에서 이슈 0)
- Test: `tests/PellyCoder.Eval.Tests/SuiteValidatorTests.cs` (가짜 `IIndependentVerifier`로)

**Interfaces:**
- Consumes: Task 1 `WorkCopy.Prepare`, `BenchmarkSuite`; 기존 `IndependentVerifier.RunJudgeTestsAsync(tests, failingTests, corpusRoot, ct)`
- Produces:
  ```csharp
  // SuiteValidator
  public static Task<IReadOnlyList<SuiteIssue>> ValidateJudgesAsync(BenchmarkSuite suite, IReadOnlyList<SuiteTask> tasks,
      Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>, string, CancellationToken, Task<JudgeTestRun>> runJudges,
      IProcessRunner processes, string scratchDir, CancellationToken ct);
  // EvalArguments + bool Deep
  ```

- [ ] **Step 1: 실패하는 테스트**
  * `Deep_JudgeTestPassingOnOriginal_IsError`: `"판정 테스트가 원본에서 이미 통과: <이름> (빈 판정)"`
  * `Deep_FailingTestFailingOnOriginal_IsError`: `"수정 전 동작 테스트가 원본에서 실패: <이름>"`
  * `Deep_NameMissingFromResults_IsError`: `"결과에 없음: <이름>"`
  * `Deep_ToolingError_IsWarning_NotError`: `JudgeTestRun.ToolingError == true`이면 경고 `"판정 도구 오류로 확인하지 못함: <Error>"`다(현장 NuGet 미러 문제를 정의 오류와 구분).
  * `Deep_SkipsTasksWithoutJudgeTests_AndLegacy`: `runJudges`를 부르지 않는다.
  * `Deep_DeletesScratchCopies`: 끝난 뒤 `scratchDir` 아래에 작업 폴더가 남지 않는다.

- [ ] **Step 2: 실패 확인**

- [ ] **Step 3: 구현**
  * `tests`나 `failingTests`가 있는 `cs` 비Legacy 작업마다 다음을 한다.
    1. `scratchDir/<id>`에 `WorkCopy.Prepare`로 원본을 복사한다.
    2. 루트에 `*.sln`이 있으면 `dotnet restore <sln> --nologo --disable-parallel`(5분)을 실행한다. 실패하면 경고를 내고 다음 작업으로 간다.
    3. `runJudges(tests, failingTests, <copy>/<root.Name>, ct)`를 부른다.
  * `tests`는 모두 Failed여야 한다. Passed면 오류, 결과에 없으면 오류다.
  * `failingTests`는 모두 Passed여야 한다.
  * 작업이 끝날 때마다 복사본을 지운다.
  * **`validate --deep`**
    * 정적 검사(Task 3)에 오류가 있으면 깊은 검사를 하지 않고 종료 4다.
    * `scratchDir` 기본값은 `<suite.ResultsDir>/validate-<yyyyMMdd-HHmmss>`다.
    * 실제 `IndependentVerifier`를 `Program.cs`에서 조립한다(LLM·SQL 접속 없음).

- [ ] **Step 4: 테스트 통과 확인** — `dotnet test tests/PellyCoder.Eval.Tests` 실패 0. 이어서 `dotnet run --project eval/PellyCoder.Eval -- validate --deep`을 실행하면 내장 스위트에서 종료 0, `오류 0건 · 경고 0건`이다.

- [ ] **Step 5: Commit** — `feat(eval): validate --deep — judge tests must fail (and pinned tests pass) on the original`

---

### Task 5: 모델 동일성 — 실행 조건·`compare`·`record`

**Files:**
- Modify: `src/PellyCoder.Core/Llm/OllamaBackend.cs`, `LlmServerStatus` 정의 파일(구현자가 찾음), `src/PellyCoder.Core/Doctor/DoctorService.cs`(모델 행에 digest 앞 12자)
- Modify: `eval/PellyCoder.Eval/RepeatSummary.cs`, `Acceptance.cs`, `Program.cs`, `EvalArguments.cs`
- Create: `eval/PellyCoder.Eval/RecordCommand.cs`
- Delete: `deploy/record-versions.sh`
- Modify: `deploy/version-record.json`(다시 기록), `deploy/build-package.ps1`(오류 문구의 `record-versions.sh` → `record` 명령), `README.md:162`, `tests/PellyCoder.Integration.Tests/Package/PackageTests.cs`(`VersionRecordTests`)
- Test: `tests/PellyCoder.Core.Tests/Llm/OllamaBackendTests.cs`, `tests/PellyCoder.Eval.Tests/RepeatSummaryTests.cs`, `AcceptanceTests.cs`, `RecordCommandTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // LlmServerStatus: 끝에 선택 인자 셋 추가 (기본 null)
  string? ModelDigest = null, string? ModelFormat = null, string? ModelQuantization = null
  // RunConditions: 끝에 추가
  string? OllamaVersion = null, string? ModelDigest = null
  // RepeatSummaryData.CurrentFormatVersion = 2; 읽기는 1과 2 (1이면 두 값 null)
  public static class RecordCommand
  {
      public static Task<int> RunAsync(EvalArguments args, ILlmBackend backend, PellyOptions options, TextWriter output, CancellationToken ct);
      // EvalArguments + string? GgufSha256 (record의 --gguf-sha256); record는 --out 필수
  }
  ```
  * 백엔드 인터페이스 이름은 구현자가 `OllamaBackend`가 구현하는 것을 쓴다.

- [ ] **Step 1: 실패하는 테스트**
  * `Status_ReadsDigestFormatQuantization_FromTagsRow`
    * `/api/tags` 응답의 해당 모델 행에서 `digest`·`details.format`·`details.quantization_level`을 읽는다.
    * 행이 없으면 셋 다 null이다.
  * `Summary_V1File_ReadsWithNullModelIdentity`: 저장소의 `docs/m3b-baseline.summary.json`(v1)을 읽으면 `OllamaVersion`·`ModelDigest`가 null이다.
  * `Summary_WritesFormatVersion2_WithModelIdentity`
  * `Comparability_DifferentModelDigest_IsRefused`: 둘 다 값이 있고 다르면 문제 `"모델 digest가 다릅니다 (<앞 12자> ≠ <앞 12자>)"`, compare 종료 4다.
  * `Comparability_DifferentOllamaVersion_IsRefused`: 같은 방식이다.
  * `Comparability_OneSideNullIdentity_IsNotRefused_ButShownInConditionDifferences`: v1 기준선과 v2 후보는 비교할 수 있다.
  * **`RecordCommandTests`** (가짜 백엔드)
    * 키 순서: `pellyVersion, ollamaVersion, model, modelDigest, modelFormat, modelQuantization, modelGgufSha256, recordedAt`
    * `recordedAt`은 UTC `yyyy-MM-ddTHH:mm:ssZ`다.
    * `--gguf-sha256`을 주지 않으면 `modelGgufSha256`은 null이다. 주면 소문자로 바꿔 쓴다.
    * 64자리 16진수가 아니면 종료 4이고 파일을 쓰지 않는다.
    * 모델이 서버에 없으면 종료 3이고 파일을 쓰지 않는다.
    * 서버에 접속할 수 없으면 종료 4다.
    * 쓴 파일에 엔드포인트 문자열(`http`)이 없다.
  * `VersionRecord_HasModelIdentityAndNoAddress`: 키 목록에 `modelGgufSha256`을 더한다(null 허용, 값이 있으면 64자리 16진수).

- [ ] **Step 2: 실패 확인**

- [ ] **Step 3: 구현**
  * **`OllamaBackend.GetStatusAsync`**: 이미 받는 `/api/tags` 응답에서 세 값을 읽는다. 새 요청은 없다.
  * **`Program.cs`**: `RunConditions`에 `status.Version`과 `status.ModelDigest`를 넣는다.
  * **`Acceptance.Comparability`**
    * 두 값이 모두 있고 다르면 문제로 넣는다.
    * 그 밖의 조건 차이는 지금처럼 표로만 보여 준다.
  * **`record` 하위 명령:** `record --out <파일> [--gguf-sha256 <hex>] [--config <pelly.json>]`
    * `Program.cs`에서 만든 `OllamaBackend`를 쓴다(새 HttpClient 금지).
    * `pellyVersion`은 `PellyInfo.Version`이다.
  * **doctor 모델 행:** 상세에 `digest <앞 12자>`를 덧붙인다. `--sanitize` 출력에 그대로 나와도 된다(모델 식별자는 비밀이 아님).

- [ ] **Step 4: 버전 기록을 다시 만든다**

  Run: `dotnet run --project eval/PellyCoder.Eval -- record --out deploy/version-record.json --config <저장소 밖 pelly.json>`
  Expected: `modelGgufSha256: null`, digest가 `cd2692a833e6…`로 M3b와 같음

  **다른 곳 갱신:**
  * `build-package.ps1`과 `README.md`의 안내를 `dotnet run --project eval/PellyCoder.Eval -- record --out deploy/version-record.json`으로 바꾼다.
  * `grep -rn "record-versions" . --include='*.md' --include='*.ps1' --include='*.yml' --include='*.cs'`의 결과가 `docs/superpowers` 밖에서 0줄이어야 한다.

- [ ] **Step 5: 전체 테스트** — 실패 0

- [ ] **Step 6: Commit** — `feat(eval): model identity in run conditions and compare; record command replaces record-versions.sh`

---

### Task 6: `export` — 반출용 비식별 결과

**Files:**
- Create: `eval/PellyCoder.Eval/ExportCommand.cs`
- Modify: `EvalArguments.cs`, `Program.cs`
- Test: `tests/PellyCoder.Eval.Tests/ExportCommandTests.cs`

**Interfaces:**
- Consumes: `TaskResult`, `RepeatSummary` 읽기·쓰기, `KpiReport.Render`, `KpiReport.RenderRepeat`
- Produces: `public static int Run(EvalArguments args, TextWriter output)` — `export <결과 폴더> --out <폴더>` (입력 폴더는 `args.Positional[0]`, Parse가 정확히 1개와 `--out`을 요구)

- [ ] **Step 1: 실패하는 테스트**

  임시 결과 폴더 두 종류(단일 실행 `results.json`, 반복 실행 `summary.json` + `run-1..3/results.json`)를 만든다. 다음 위치에 표식 문자열 `SECRET-교환대금`, `/Users/dev/erp`, `http://10.1.2.3:11434`를 심는다.
  * 사유
  * 검증 상세
  * 생략 사유(`환경 오류(복원 실패): …SECRET…` 포함)
  * `report.md` 머리

  **확인할 것:**
  * `Export_NoMarkerInAnyOutputFile`: `--out` 아래 **모든 파일**의 텍스트에 표식이 없다(Review Focus 5).
  * `Export_KeepsKpiFields`: 행마다 다음이 원본과 같다.
    * `id`, `kind`, `status`, `exitCode`, `attempts`, `failureType`
    * `buildCheck`, `invariantViolated`, `wallSeconds`, `ttftSeconds`
    * `thinkTokens`, `thinkTruncated`, `editOutcomes`, `isLegacy`, `formatErrors`
    * `independent.status`, `independent.build`
  * `Export_SanitizesEachDroppedField`: 지운 필드는 다음 값이 된다.
    * `reasons` → `[]`
    * `independent.detail` → null
    * `skipReason` → `환경 오류(<kind>)`처럼 앞부분만, 그 밖의 생략 사유는 `생략`
  * `Export_RepeatSummary_IsReadByCompare`: 내보낸 `summary.json`을 `RepeatSummary` 읽기와 `compare`가 받아들인다(같은 파일 둘을 비교하면 종료 3 — 개선 없음 — 이고 4가 아님).
  * `Export_WritesExportReadme`: `EXPORT.md`에 지운 필드 목록과 원본 폴더 **이름**(경로 아님)이 있다.
  * `Export_RefusesNonEmptyOut_And_MissingInput`: 둘 다 종료 4다.

- [ ] **Step 2: 실패 확인**

- [ ] **Step 3: 구현**
  * **입력 판별:** 입력 폴더에 `summary.json`이 있으면 반복 실행이고 `run-<k>/results.json`을 모두 처리한다. 없으면 `results.json` 하나를 처리한다.
  * **출력 구성** (같은 상대 배치)
    * 비식별 `results.json`
    * 그 결과로 다시 그린 `report.md`. 머리는 `* 반출본: <원본 폴더 이름> · pelly <ver> · 모델 <model>`이고 엔드포인트는 넣지 않는다.
    * 반복 실행이면 `summary.json`은 그대로 복사한다. 이 파일은 id·결과·조건만 담고 경로·주소가 없다. 이 사실을 테스트가 표식으로 확인한다.
    * `summary.md`는 `RenderRepeat`로 다시 그린다.
  * `EXPORT.md`를 쓴다.
  * 작업 복사본(`work/`)과 `.pelly/runs`는 복사하지 않는다. pelly 실행 기록 반출은 기존 `pelly report --sanitize`가 맡는다.
  * `Usage`에 `export` 절을 더한다.

- [ ] **Step 4: 통과 확인** — 실패 0

- [ ] **Step 5: Commit** — `feat(eval): export — sanitized benchmark results for carry-out`

---

### Task 7: doctor — SQL 인증 방식과 접속 계정

**Files:**
- Modify: `src/PellyCoder.Core/Abstractions/ISqlExecutor.cs` (`SqlServerProbe`), `src/PellyCoder.Core/Sql/SqlServerExecutor.cs`(Probe 쿼리), `src/PellyCoder.Core/Sql/SqlConnectionText.cs`, `src/PellyCoder.Core/Doctor/DoctorService.cs`, `DoctorFacts.cs`, `DoctorSanitizer`(구현자가 찾음)
- Test: `tests/PellyCoder.Core.Tests/Doctor/DoctorServiceTests.cs`, `DoctorSanitizerTests.cs`, `tests/PellyCoder.Core.Tests/Sql/SqlConnectionTextTests.cs`, `tests/PellyCoder.Integration.Tests/Sql/SqlServerExecutorTests.cs`(로그인 이름이 `sa`)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record SqlServerProbe(string Version, string Database, bool CanViewDefinition, string? LoginName = null);
  public static bool UsesIntegratedSecurity(string connectionString); // SqlConnectionText
  // DoctorFacts: public const string AuthMode = "authMode"; 값 "windows" | "sql"
  ```

- [ ] **Step 1: 실패하는 테스트**
  * `UsesIntegratedSecurity`: 다음 연결 문자열을 판정한다.
    * `Integrated Security=true`, `Integrated Security=SSPI`, `Trusted_Connection=yes` → true
    * `User Id=sa;Password=x` → false
    * 키 대소문자와 공백 변형도 같다.
  * `Sql_WindowsAuth_DetailNamesAuthAndLogin`
    * 가짜 probe의 `LoginName = "CORP\\kim"`, 연결 문자열은 Integrated Security다.
    * 상세가 `"<ds>/<db>, Windows 인증 (CORP\\kim), <버전 첫 줄>"`이고, fact `authMode=windows`다.
  * `Sql_SqlLogin_DetailSaysSqlLogin`: `"…, SQL 로그인 (sa), …"`, fact `authMode=sql`
  * `Sanitize_DropsLoginName_KeepsAuthMode`: `doctor --sanitize` 결과에 `CORP\\kim`이 없고 `authMode`는 남는다.
  * `Probe_ReturnsLoginName` (SqlServer 통합 테스트): `LoginName == "sa"`

- [ ] **Step 2: 실패 확인**

- [ ] **Step 3: 구현**
  * Probe 쿼리에 `SUSER_SNAME()`을 더한다. `VIEW SERVER STATE`는 요구하지 않는다. `sys.dm_exec_connections`는 쓰지 않는다.
  * doctor 상세는 위 형식이고, `LoginName`이 null이면 괄호를 뺀다.
  * Windows 인증의 실제 도메인 동작은 「Windows 미확인」이다. `deploy/install.md`의 SQL 절에 이 사실과, doctor가 접속 계정을 보여 준다는 한 줄을 더한다.

- [ ] **Step 4: 통과 확인** — 전체 테스트 실패 0(SQL 변수 포함)

- [ ] **Step 5: Commit** — `feat(doctor): show SQL auth mode and login; sanitize drops the login`

---

### Task 8: 현장 문서·템플릿·결과 양식

**Files:**
- Create:
  * `deploy/field-guide.md`: 현장 절차 전체
  * `deploy/field-benchmark.md`: 스위트·작업 작성 안내
  * `deploy/field-results.md`: 현장 결과 양식(빈칸)
  * 템플릿 네 개: `deploy/field-kit/templates/suite.json`, `deploy/field-kit/templates/tasks/cs-modern-001.yaml`, `cs-legacy-001.yaml`, `sql-001.yaml`
- Modify:
  * `deploy/ide-external-tools.md`: Visual Studio 실기 확인 결과 표(빈칸). 항목은 `docs/m3b-s7-rider.md`의 0~8과 같은 번호다.
  * `deploy/server-checklist.md`: GGUF SHA-256 확인 방법
  * `deploy/install.md`: 현장 키트 한 단락과 링크
- Test: `tests/PellyCoder.Integration.Tests/Package/FieldKitDocTests.cs`

**Interfaces:**
- Consumes: Task 1~7의 명령과 옵션 이름, `SuiteValidator.SchemaIssues`, `EvalArguments.Parse`

- [ ] **Step 1: 실패하는 테스트 (`FieldKitDocTests`)**
  * `Templates_PassSchemaChecks`: 템플릿 YAML 셋이 `BenchmarkTask.Load`로 읽히고, `SuiteValidator.SchemaIssues(task, 파일 이름)`가 이슈 0이다.
  * `TemplateSuiteJson_HasOnlyKnownKeys`: 템플릿 `suite.json`의 키가 Task 1 스키마의 키 집합 안에 있다. 경로 존재 검사는 하지 않는다. 경로 자리 표시는 `D:\\src\\<저장소>` 형식이다.
  * `FieldGuide_EvalCommandLines_Parse`
    * `deploy/field-guide.md`의 코드 블록에서 `pelly-eval`로 시작하는 줄을 모두 모은다.
    * 실행 파일 이름을 뗀 인자를 `EvalArguments.Parse`에 넣으면 `Error`가 null이다. `validate`·`record`·`export`·`compare` 줄도 같은 파서로 확인한다.
    * 모은 줄이 8줄 이상이다.
  * `FieldDocs_HaveNoSecretsOrDevPaths`: 새 문서 셋과 템플릿에 대해, 기존 `TemplateRules.Violations`와 개발자 경로 검사(`/Users/`, `C:\\Users\\`, `localhost`, 비밀번호 키)가 0건이다.

- [ ] **Step 2: 실패 확인**

- [ ] **Step 3: 문서를 쓴다** — 내용 요구 사항

  **`field-guide.md`:** 번호 붙은 절차. 각 단계에 정확한 명령과 기대 결과를 둔다.
  1. 두 zip의 SHA-256 확인(`install.md` 2절 방식)
  2. 설치와 `pelly doctor` 확인. 볼 행은 Ollama 서버, 모델 digest, MSBuild, 타겟팅 팩, SQL Windows 인증과 접속 계정이다.
  3. 서버 관리자에게 GGUF SHA-256을 받아 `pelly-eval record --out <현장 폴더>\version-record.json --gguf-sha256 <hex> --config <pelly.json>`을 실행한다.
  4. 스위트 작성(`field-benchmark.md`)
  5. `pelly-eval validate --suite <폴더>` → `pelly-eval validate --suite <폴더> --deep`
  6. 기준선: `pelly-eval --suite <폴더> --config <pelly.json> --repeat 3 --keep-work failed`
  7. KPI 판정: `field-results.md`에 옮겨 적는다. 목표는 PRD §2.2이고, Q6 목표 확정은 사람이 한다.
  8. 설정 튜닝 후보(샘플링·예산 등 `pelly.json` 값만)를 같은 명령으로 측정한 뒤 `pelly-eval compare <기준선>\summary.json <후보>\summary.json`을 실행한다. 종료 0만 채택한다.
  9. 반출: `pelly-eval export <결과 폴더> --out <반출 폴더>`. pelly 실행 기록은 `pelly report --sanitize --out <폴더>`, 환경은 `pelly doctor --sanitize --out <폴더>`로 반출한다.
  10. Visual Studio External Tools 확인표(`ide-external-tools.md`)

  모든 명령은 Windows `cmd`/PowerShell 형식(`\` 경로)으로 쓴다.

  **`field-benchmark.md`:**
  * `suite.json`의 키별 설명과 규칙. Task 1의 오류 조건을 그대로 적는다.
  * 작업 YAML 규칙. `eval/benchmark/README.md` 스키마를 현장용으로 다시 적는다. `file`은 `<루트 이름>/…`이다.
  * **구성 권장:** 외부와 같은 규모(PRD §2.1)인 C# 30(Legacy 18 · Modern 12) + SQL 20. 실제 비율이 다르면 범주별 건수를 `field-results.md`에 적는다.
  * **판정 선택 지침**
    * Modern은 동작 판정(현장 테스트 프로젝트 또는 `judges/<루트>/`)을 우선한다.
    * Legacy·SQL은 문자열 판정이다.
    * 다른 올바른 수정을 떨어뜨리는 문자열 판정은 쓰지 않는다(M3b 사례 두 개).
  * **id 규칙:** 업무명을 넣지 않는다. 형식이 강제되므로 반출본에 업무 정보가 남지 않는다.
  * `--deep`이 무엇을 확인하는지
  * `supportFiles` 경고 대응

  **`field-results.md`:** 빈칸 양식
  * 버전 기록 값(pelly, Ollama, 모델, digest, GGUF SHA-256)
  * 외부 기준(`docs/m3b-baseline.md`) 대비 표
  * KPI 6행(PRD §2.2) × 목표·측정·판정
  * 범주별 성공
  * 실패 작업 목록(id·실패 유형만)
  * Q6 확정 값과 결정한 사람·날짜 칸
  * 튜닝 후보와 `compare` 결과
  * 반출한 파일 목록

  **기타 문서:**
  * `server-checklist.md`의 모델 행에 GGUF SHA-256 확인 명령을 더한다.
    * Linux 서버: `sha256sum <ollama 모델 저장소의 blob 파일>`
    * Windows 서버: `Get-FileHash -Algorithm SHA256 <파일>`
    * Ollama blob 파일 이름이 이미 `sha256-<hex>`라는 점과, 모델 digest(매니페스트)와 GGUF 해시는 다른 값이라는 점을 적는다.
  * 이 체크리스트는 pelly에 내장되어 있다(`doctor`가 출력). 바꾼 뒤 내장 출력 테스트가 통과하는지 확인한다.

- [ ] **Step 4: 통과 확인** — 전체 테스트 실패 0

- [ ] **Step 5: Commit** — `docs(deploy): field guide, suite authoring guide, results form, templates, VS check table`

---

### Task 9: 현장 키트 패키지 — `pelly-eval.exe`·두 번째 zip·CI·정책 테스트

**Files:**
- Modify: `eval/PellyCoder.Eval/PellyCoder.Eval.csproj` — `AssemblyName`을 `pelly-eval`로 하고, CLI csproj와 같은 `win-x64` 조건부 PropertyGroup을 둔다(SelfContained·PublishSingleFile·IncludeNativeLibrariesForSelfExtract·트리밍 없음·DebugType none).
- Modify: `deploy/build-package.ps1`
  * `-Package <pelly|field-kit>`(기본 `pelly`)를 받는다.
  * `field-kit`이면 다음을 바꾼다.
    * 대상 프로젝트: `eval/PellyCoder.Eval/PellyCoder.Eval.csproj`
    * 허용 목록: `deploy/field-kit-files.txt`
    * 이름: `pelly-field-kit-<ver>-win-x64`
    * SBOM 이름: `pelly-eval`
  * `version-record.json` 검사는 `pelly`일 때만 한다.
  * 라이선스 게이트·예외 파일·잠정 승인 1.0 거부는 두 패키지에 같게 적용한다.
- Create: `deploy/field-kit-files.txt`
- Modify: `.github/workflows/ci.yml` (`publish-check`·`package` 잡)
- Modify: `tests/PellyCoder.Integration.Tests/Package/PackageTests.cs` — zip 하나를 받는 검사를 매개변수화한다. 환경 변수 `PELLY_TEST_FIELD_KIT`, 허용 목록 `field-kit-files.txt`.
- Modify: `tests/PellyCoder.Core.Tests/Policy/NetworkPolicyTests.cs` — 검사 범위에 `eval/PellyCoder.Eval/`을 더한다. 허용 위치는 `eval/PellyCoder.Eval/Program.cs` 하나(HttpClient)다. 클래스 요약 주석의 "eval은 반입하지 않는다" 문장을 고친다.
- Modify: `deploy/no-network.md` — `pelly-eval.exe`의 접속(Ollama·SQL만)과 디스크 쓰기(스위트 결과 폴더, `validate --deep` 임시 폴더)를 표에 더한다.

**Interfaces:**
- Consumes: Task 8 문서·템플릿 경로, Task 1~6 명령

- [ ] **Step 1: 허용 목록을 쓴다 (`deploy/field-kit-files.txt`)**
  ```text
  pelly-eval.exe <- publish:pelly-eval.exe
  docs/field-guide.md <- deploy/field-guide.md
  docs/field-benchmark.md <- deploy/field-benchmark.md
  docs/field-results.md <- deploy/field-results.md
  templates/suite.json <- deploy/field-kit/templates/suite.json
  templates/tasks/cs-modern-001.yaml <- deploy/field-kit/templates/tasks/cs-modern-001.yaml
  templates/tasks/cs-legacy-001.yaml <- deploy/field-kit/templates/tasks/cs-legacy-001.yaml
  templates/tasks/sql-001.yaml <- deploy/field-kit/templates/tasks/sql-001.yaml
  sbom.cdx.json <- generated
  THIRD-PARTY-NOTICES.md <- generated
  licenses/Microsoft.Data.SqlClient.SNI.runtime.LICENSE.txt <- generated
  SHA256SUMS.txt <- generated
  ```
  머리 주석은 `package-files.txt`와 같은 형식이다.

- [ ] **Step 2: 실패하는 테스트**
  * `PackageSourceTests.FieldKitFileList_SourcesExist`: 저장소 출처 파일이 모두 있다(Windows 아님에서도 돈다).
  * `NetworkPolicyTests`: 범위에 eval이 들어간 뒤 `eval/PellyCoder.Eval/Program.cs` 말고 다른 곳에 `new HttpClient`가 있으면 실패한다. 임시로 심어 확인하고 되돌린다.
  * 매개변수화한 `PackageTests`(WindowsFact, CI에서 돎): 두 zip 각각에 대해 다음을 확인한다.
    * 허용 목록과 정확히 같다.
    * 체크섬이 맞다.
    * 개발자 경로가 없다.
    * pdb가 없다.
    * 자체 점검: pelly는 `--offline-selftest`, pelly-eval은 `--help` 종료 0과 `validate --suite <압축 푼 templates 폴더>` 종료 4. 템플릿 경로는 자리 표시라 해석 오류가 나는 것이 정상이며, 단언은 출력에 `"스위트"`와 `suite.json` 오류 줄이 있는 것이다.

- [ ] **Step 3: 구현** — csproj, 스크립트, CI

  **`publish-check` 잡에 더할 단계:**
  1. `dotnet publish eval/PellyCoder.Eval -c Release -r win-x64 -o artifacts/publish-eval/win-x64`
  2. `artifacts/publish-eval/win-x64/pelly-eval.exe --help` 종료 0
  3. `pelly-eval.exe validate --suite eval/benchmark` 종료 0: 내장 스위트가 Windows에서 Legacy를 바르게 판정하는지 본다.
  4. 아티팩트 `pelly-eval-publish-win-x64` 업로드

  **`package` 잡에 더할 단계:**
  1. 그 아티팩트를 받는다.
  2. `build-package.ps1 -Package field-kit -PublishDir artifacts/publish-eval/win-x64 -OutDir artifacts/field-kit`
  3. 체크섬 확인 단계를 두 zip에 대해 돈다(현장 키트는 확인할 파일 11개).
  4. `PELLY_TEST_FIELD_KIT`를 설정한다.
  5. 패키지 테스트의 `--minimum-expected-tests`를 실제 수로 올린다.
  6. 아티팩트 `pelly-field-kit`과 `pelly-field-kit-sha256`을 업로드한다.

- [ ] **Step 4: 로컬 확인과 CI**
  * 로컬: `dotnet build -warnaserror && dotnet test` 실패 0
  * push 뒤 CI 잡 5개(실행 6회)가 모두 녹색이다.
  * `package` 잡 로그에 두 zip의 SHA-256 줄이 있다.

- [ ] **Step 5: Commit** — `feat(package): pelly-eval single-file exe and the field-kit zip, with license gate, SBOM and policy scope`

---

### Task 10: 버전 `0.9.1`과 문서 동기화

**Files:**
- Modify: `Directory.Build.props`(`Version` 0.9.1), `deploy/version-record.json`(`record`로 다시 기록), `deploy/benchmark-summary.md`(측정은 0.9.0 이전 빌드라는 사실과, 0.9.1의 pelly 변경은 인자 검사·텔레메트리 변수·doctor 표시뿐이라 편집 경로가 같다는 한 줄)
- Modify: `docs/PRD.md` (v1.11 변경 이력, §2.1, §8.1, §9 M4, §11.1 두 zip, §11.2)
- Modify: 설계 문서 §10.6(스위트·validate·export·record), §11 표에 **M4 준비** 행
  * 내용: 이 계획의 산출물
  * 완료 기준: 현장 키트 zip이 CI에서 생성되고 내장 스위트 회귀 결과가 `docs/m4-suite-regression.md`에 있음
- Modify: `README.md`, `eval/benchmark/README.md`(실행 절의 명령, 하위 명령 목록)

- [ ] **Step 1: 버전을 올리고 기록을 다시 만든다**
  * `record`를 다시 실행한다.
  * `VersionRecordTests`와 패키지 스크립트의 버전 일치 검사가 통과한다.
  * `pelly --version`이 `0.9.1`이다.

- [ ] **Step 2: 문서 갱신**

  **낡은 서술을 스윕한다:**
  * `grep -rn "0\.9\.0" docs/PRD.md README.md deploy`의 결과 중, M3b 결과를 말하는 곳만 남기고 현재 버전을 말하는 곳은 바꾼다.
  * `grep -rn "corpus/Modern/\|corpus/Legacy/" eval/benchmark/README.md`: 작업 `file` 예시를 새 형식으로 바꾼다.
  * "eval은 반입하지 않는다" 취지의 서술을 찾아 고친다. 이름이 아니라 산문으로 쓴 곳도 2차로 스윕한다. 단서는 "개발자 도구", "반입하지 않".

  **PRD §9 Milestone 4에 더할 것:**
  * 현장 키트로 하는 절차 요약
  * 반입 전 사람 결정 다섯 가지(위 "범위 밖"의 목록)

- [ ] **Step 3: 전체 확인**
  * `dotnet build -warnaserror && dotnet test`(실제 모델·DB 변수 포함): 실패 0. 예외 규칙은 Global Constraints의 테스트 게이트를 따른다.
  * push 뒤 CI가 녹색이다.

- [ ] **Step 4: Commit** — `chore(release): 0.9.1 — field kit, docs synced (PRD v1.11)`
