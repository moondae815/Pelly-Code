# S8 의미 분석 불러오기 측정 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `MSBuildWorkspace`로 솔루션을 불러와 「열림·타입 정보·시간·배포 형태」를 숫자로 보고하는 스파이크 도구 `s8`을 만들고, macOS와 GitHub Windows 러너에서 코퍼스를 재며, 현장에서 쓸 안내를 만든다.

**Architecture:** 측정은 `Solution`을 받는 순수 함수(인구 조사, 진단 분류, 호출 해석률, 컴파일 상태, 참조 탐침, a3 대조)와 `MSBuildWorkspace`로 그 `Solution`을 만드는 불러오기 실행기로 나눈다. 순수 함수는 `AdhocWorkspace` 픽스처로 빠르게 시험하고, 불러오기는 코퍼스로 통합 시험한다. `--repeat N`은 자기 자신을 `--once`로 N번 새 프로세스로 띄운다.

**Tech Stack:** .NET 10, `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.9.0, `Microsoft.CodeAnalysis.CSharp.Workspaces` 5.9.0, xunit.v3.mtp-v2 4.0.2, System.Text.Json, GitHub Actions(windows-latest).

**Spec:** `docs/superpowers/specs/2026-10-24-s8-semantic-load-design.md`

## Global Constraints

- 위치 `spikes/S8-semantic-load/`, 프로젝트 `S8SemanticLoad`(Exe, net10.0, RootNamespace `S8SemanticLoad`)와 `S8SemanticLoad.Tests`. `spikes/Directory.Build.props`·`Directory.Packages.props`(중앙 관리 끔)를 따른다.
- 패키지 버전은 제품과 같은 5.9.0. `System.Security.Cryptography.Pkcs` 10.0.13 고정(S6과 같은 이유 — 전이 9.0.18 취약 경고).
- 제품 코드(`src/`)를 바꾸지 않는다. a3 정답은 S6 스파이크의 `GoldLoader`/`TargetKey`를 프로젝트 참조로 재사용한다(`../../S6-planning/S6Planning/S6Planning.csproj`).
- 현장 모드(기본)의 요약과 JSON에는 숫자와 고정 유형 이름만 담는다. 파일 경로·프로젝트 이름·심볼 이름·진단 메시지는 `--detail`일 때만 나온다.
- 판정 기준 값(실행 전 고정): 콜드 ≤ 15초, 웜 ≤ 10초(프로젝트 4개 이하), 참조 찾기 메서드당 ≤ 2초, 호출 해석률 ≥ 99%, C# 프로젝트 작업 공간 실패 0, 컴파일 오류 0(복원·빌드된 프로젝트), 해석 못 한 메타데이터 참조 0, a3 「정답과 같음」.
- 종료 코드: 0 측정 완료, 2 사용법 오류, 3 「MSBuild를 찾을 수 없음」 유형 진단이 있음, 1 그 밖의 오류.
- 참조 탐침 시드 20261024, 최대 20개 메서드.
- 테스트 명령: `cd spikes/S8-semantic-load/S8SemanticLoad.Tests && dotnet test`. 실패 0·건너뜀 0.
- 커밋 꼬리: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **`.slnx` 솔루션** — VS 2025는 XML 솔루션 형식을 쓸 수 있다. 인구 조사와 열기가 `.sln`과 같게 동작해야 한다. → Task 1 `Census_reads_slnx`.
2. **C#이 아닌 프로젝트가 섞인 솔루션**(`.sqlproj`) — 분모에서 빠지고, 그 프로젝트에서 난 작업 공간 진단은 C# 실패로 세지 않아야 한다. → Task 1 `Census_counts_non_csharp_separately`, Task 5 `Verdict_ignores_failures_from_non_csharp_projects`.
3. **공백·한글이 든 경로** — 현장 경로에 흔하다. → Task 1 `Census_handles_spaces_and_korean_in_paths`.
4. **복원 안 된 SDK 형식 프로젝트** — 그 컴파일 오류가 실패로 세지면 정상 솔루션이 떨어진다. → Task 5 `Verdict_excludes_errors_of_unrestored_projects`.
5. **현장 모드 출력에 이름이 새는 것** — 반출 불가가 된다. → Task 5 `Field_mode_json_has_no_names`.

---

### Task 1: 스캐폴드와 솔루션 인구 조사

**Files:**
- Create: `spikes/S8-semantic-load/S8SemanticLoad/S8SemanticLoad.csproj`, `ProjectCensus.cs`, `Program.cs`(이 작업에서는 빈 `Main`이 2를 반환)
- Create: `spikes/S8-semantic-load/S8SemanticLoad.Tests/S8SemanticLoad.Tests.csproj`(S6 테스트 csproj와 같은 모양, `Fixtures/**` 컴파일 제외), `TestPaths.cs`(S6 것과 같은 방식, `Spike => spikes/S8-semantic-load`), `ProjectCensusTests.cs`
- Create: `spikes/S8-semantic-load/.gitignore`(`bin/`, `obj/`, `out/`)
- Fixtures: `Fixtures/census/` 아래 작은 솔루션들(아래 테스트가 정함)

**Interfaces:**
- Produces: `enum ProjectKind { SdkCSharp, LegacyCSharp, NonCSharp }`; `record CensusEntry(string Path, ProjectKind Kind, bool Restored)`; `record ProjectCensus(IReadOnlyList<CensusEntry> Entries)` with `int SdkCs`, `int LegacyCs`, `int NonCs`, `int UnrestoredSdk`, `int CSharpTotal`; `static ProjectCensus ProjectCensusReader.Read(string slnOrProjectPath)`.

규칙: `.sln`은 `Project("{…}") = "이름", "경로", "{…}"` 줄을 읽고 솔루션 폴더 GUID `{2150E333-8FDC-42A3-9474-1A3956D46DE8}`는 건너뛴다. `.slnx`는 모든 `<Project Path="…">`를 읽는다. 단일 `.csproj` 경로면 그 하나. `.csproj`의 루트 `<Project>`에 `Sdk` 속성이 있으면 `SdkCSharp`, 없으면 `LegacyCSharp`. 그 밖의 확장자는 `NonCSharp`. `Restored`는 SDK 형식에만 의미가 있고 `obj/project.assets.json` 존재 여부다(레거시는 항상 true). 경로 구분자 `\`는 현재 OS 구분자로 바꾼다.

- [ ] **Step 1: 실패하는 테스트 작성** — `ProjectCensusTests`:
  - `Census_reads_legacy_corpus_sln`: `Read(Corpus/Legacy/Legacy.sln)` → `LegacyCs == 4`, `SdkCs == 0`, `NonCs == 0`.
  - `Census_reads_modern_corpus_sln`: `Modern.sln` → `SdkCs == 3`, `LegacyCs == 0`.
  - `Census_reads_slnx`: 픽스처 `census/slnx/App.slnx`(SDK csproj 1개 + 레거시 csproj 1개 + 솔루션 폴더) → `SdkCs == 1`, `LegacyCs == 1`.
  - `Census_counts_non_csharp_separately`: 픽스처 `census/mixed/Mixed.sln`(SDK csproj 1, `Db/Db.sqlproj` 1, 솔루션 폴더 1) → `SdkCs == 1`, `NonCs == 1`, `CSharpTotal == 1`.
  - `Census_handles_spaces_and_korean_in_paths`: 픽스처 `census/한글 경로/App One/App One.csproj`(SDK)를 가리키는 `census/한글 경로/Space.sln` → `SdkCs == 1`, 항목 경로가 실제로 존재.
  - `Census_marks_unrestored_sdk`: `census/mixed`의 SDK csproj는 `obj/`가 없다 → `UnrestoredSdk == 1`. 같은 SDK csproj에 `obj/project.assets.json`(내용 `{}`)을 둔 `census/restored` → `UnrestoredSdk == 0`. (픽스처 `obj/`가 `.gitignore`에 걸리지 않게 `Fixtures/**/obj/`를 `!` 예외로 둔다.)
  - `Census_accepts_single_csproj`: `Modern/Shop.Api/Shop.Api.csproj` → `Entries.Count == 1`, `SdkCs == 1`.
- [ ] **Step 2: 실패 확인** — `dotnet test` → 컴파일 실패(`ProjectCensusReader` 없음).
- [ ] **Step 3: 구현** — csproj에 `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.9.0, `Microsoft.CodeAnalysis.CSharp.Workspaces` 5.9.0, `System.Security.Cryptography.Pkcs` 10.0.13, S6Planning 프로젝트 참조. `ProjectCensusReader.Read`.
- [ ] **Step 4: 통과 확인** — `dotnet test` → 7 passed. `dotnet build` 경고 0.
- [ ] **Step 5: 커밋** — `spike(S8): 스캐폴드와 솔루션 인구 조사`

### Task 2: 작업 공간 진단 분류

**Files:** Create `DiagnosticClassifier.cs`, Test `DiagnosticClassifierTests.cs`

**Interfaces:**
- Produces: `enum WorkspaceIssueKind { MsBuildNotFound, UnsupportedProject, ReferenceResolution, Other }`; `static WorkspaceIssueKind DiagnosticClassifier.Classify(string message)`.

분류 규칙(대소문자 무시, 위에서부터 첫 일치): 메시지에 `msbuild` 그리고 (`not found` 또는 `could not be found` 또는 `cannot find` 또는 `no msbuild`)가 있으면 `MsBuildNotFound`. `build host not found`도 `MsBuildNotFound`. `not supported` 또는 `unsupported` 또는 `cannot open project` 또는 `project file type`이면 `UnsupportedProject`. `reference` 그리고 (`could not be resolved` 또는 `not found` 또는 `cannot resolve`)이면 `ReferenceResolution`. 나머지 `Other`. 규칙은 이 파일 한 곳에만 둔다.

- [ ] **Step 1: 실패하는 테스트** — `[Theory]` `Classify_maps_message_to_kind` 행:
  - `"Mono MSBuild could not be found"` → `MsBuildNotFound`
  - `"Msbuild failed when processing the file '/x/a.csproj' with message: The SDK 'Microsoft.NET.Sdk' specified could not be found."` → `MsBuildNotFound`(규칙상 그렇게 분류된다. 실제로는 SDK를 못 찾은 경우라 오분류다. 알고 고정하며 Task 7 보고서 한계에 적는다)
  - `"Build host not found at /x/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll"` → `MsBuildNotFound`
  - `"Cannot open project '/x/Db.sqlproj' because the file extension '.sqlproj' is not associated with a language."` → `UnsupportedProject`
  - `"The reference assemblies for .NETFramework,Version=v4.5 were not found."` → `ReferenceResolution`(`reference`+`not found`)
  - `"Something else"` → `Other`
- [ ] **Step 2: 실패 확인** — 컴파일 실패.
- [ ] **Step 3: 구현** `DiagnosticClassifier.Classify`.
- [ ] **Step 4: 통과 확인** — 6 rows passed.
- [ ] **Step 5: 커밋** — `spike(S8): 작업 공간 진단 분류`

> 두 번째 행은 규칙의 약점을 일부러 고정한다. 「SDK를 못 찾음」이 「MSBuild를 못 찾음」으로 세지는 것을 알고 쓰며, 보고서에 적는다.

### Task 3: 호출 해석률과 컴파일 상태

**Files:** Create `SemanticMetrics.cs`, Test `SemanticMetricsTests.cs`, `AdhocFixture.cs`(테스트 쪽 도우미)

**Interfaces:**
- Produces: `record CallResolution(int Total, int Resolved, int Ambiguous, int Unresolved)` with `double Rate => Total == 0 ? 1.0 : (double)Resolved / Total`; `static Task<CallResolution> SemanticMetrics.MeasureCallsAsync(Solution s, Func<Project,bool>? include = null, CancellationToken ct = default)`.
- Produces: `record ProjectHealth(string Name, ProjectKind Kind, bool Restored, int CompileErrors, int MissingMetadataRefs)`; `static Task<IReadOnlyList<ProjectHealth>> SemanticMetrics.MeasureHealthAsync(Solution s, ProjectCensus census, CancellationToken ct = default)`.
- 테스트 도우미: `static Solution AdhocFixture.Build(params (string Project, string[] Refs, (string Path, string Code)[] Docs)[] projects)` — `AdhocWorkspace`, 메타데이터 참조는 `TRUSTED_PLATFORM_ASSEMBLIES`의 `System.Runtime`·`System.Private.CoreLib`·`System.Console`·`System.Collections`·`System.Linq`.

규칙: 모든 `InvocationExpressionSyntax`를 센다. 대상이 `nameof(...)`인 것, 수신자 타입이 `dynamic`인 것은 분모에서 뺀다. `GetSymbolInfo(...).Symbol != null` → Resolved, `Symbol == null && CandidateSymbols.Length > 0` → Ambiguous, 그 밖 → Unresolved. `include`는 C# 프로젝트 필터(Task 5가 복원 안 된 프로젝트를 뺄 때 씀). `MissingMetadataRefs`는 `PortableExecutableReference.FilePath`가 존재하지 않는 수. `ProjectHealth.Kind/Restored`는 census에서 프로젝트 파일 경로로 찾는다(없으면 `SdkCSharp`, true).

- [ ] **Step 1: 실패하는 테스트**
  - `All_calls_resolve_in_clean_code`: 한 프로젝트, `class A { void M(){ N(); System.Console.WriteLine(1); } void N(){} }` → `Total == 2, Resolved == 2, Rate == 1.0`.
  - `Unknown_method_is_unresolved`: `void M(){ Missing(); }` → `Unresolved == 1`.
  - `Overload_mismatch_is_ambiguous_or_unresolved_not_resolved`: `void F(int a){} void F(string a){} void M(){ F(1.5); }` → `Resolved == 0` 그리고 `Ambiguous + Unresolved == 1`.
  - `Nameof_and_dynamic_are_excluded`: `void M(dynamic d){ var s = nameof(M); d.Foo(); N(); } void N(){}` → `Total == 1`.
  - `Health_counts_errors_and_missing_refs`: 프로젝트에 `int x = "s";` 한 줄 + 존재하지 않는 경로의 `MetadataReference.CreateFromFile`은 만들 수 없으므로 `AdhocFixture`에 `missingRef: "/nope/x.dll"` 옵션을 두어 `PortableExecutableReference`를 실제 DLL로 만들고 `FilePath`만 `/nope/x.dll`로 준다(`MetadataReference.CreateFromImage(bytes, filePath: "/nope/x.dll")`) → `CompileErrors >= 1`, `MissingMetadataRefs == 1`.
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인** — 5 passed.
- [ ] **Step 5: 커밋** — `spike(S8): 호출 해석률과 컴파일 상태`

### Task 4: 참조 탐침과 a3 정답 대조

**Files:** Create `ReferenceProbe.cs`, `CorpusCheck.cs`, Test `ReferenceProbeTests.cs`, `CorpusCheckTests.cs`

**Interfaces:**
- Produces: `record ProbeResult(int Methods, double MaxSeconds, double MeanSeconds, double ZeroReferenceRatio)`; `static Task<ProbeResult> ReferenceProbe.RunAsync(Solution s, int max = 20, int seed = 20261024, CancellationToken ct = default)`. 후보는 소스에 선언된 `public` 메서드(생성자·연산자·접근자 제외)를 `(문서 경로, 위치)` 순으로 정렬한 뒤 `new Random(seed)`로 `max`개. 메서드마다 `SymbolFinder.FindReferencesAsync` + `FindImplementationsAsync` 시간을 합쳐 잰다. 위치 0 = 참조 위치 0 그리고 구현 0.
- Produces: `record A3Result(IReadOnlyList<string> Missing, IReadOnlyList<string> ForbiddenHit, IReadOnlyList<string> Extra)` with `bool Matches => Missing.Count == 0 && ForbiddenHit.Count == 0`; `static Task<A3Result> CorpusCheck.CheckA3Async(Solution s, string corpusRoot, string casesDir, CancellationToken ct = default)`.

a3 규칙: S6 `GoldLoader.Load(Path.Combine(casesDir, "a3-rename-getorder"))`로 필수 대상 키와 금지 키를 읽는다. 솔루션에서 `Shop.Data.IOrderDao.GetOrder` 메서드 심볼을 찾는다. 찾은 위치를 S6 `TargetKey.Of(경로(corpusRoot 기준 상대), 그 위치를 감싸는 멤버 이름)`으로 바꾼다.
- 구현 찾기 결과 → 그 구현 메서드 선언 위치
- 참조 찾기 결과 → 참조를 감싸는 멤버
- 대상 심볼 자신의 선언 → `Legacy/Shop.Data/IOrderDao.cs#GetOrder`

이렇게 모은 집합과 정답을 비교한다. `Missing` = 정답에 있고 결과에 없음, `ForbiddenHit` = 결과에 있는 금지 키, `Extra` = 그 밖에 결과에만 있는 것. 대상 이름 변경 사례라 a3의 정답 T1~T5(`IOrderDao#GetOrder`, `OrderDao#GetOrder`, 테스트 가짜 `#GetOrder`, `OrderService#GetOrder`, `OrderService#Cancel`)가 모두 결과에 들어야 한다.

- [ ] **Step 1: 실패하는 테스트**
  - `Probe_picks_deterministic_methods_and_times_them`: 메서드 25개짜리 픽스처 → `Methods == 20`; 같은 시드로 두 번 돌리면 같은 `ZeroReferenceRatio`.
  - `Probe_counts_zero_reference_methods`: `public void Used(){} public void Unused(){} void M(){ Used(); }`(M은 private) → `Methods == 2`, `ZeroReferenceRatio == 0.5`.
  - `A3_matches_gold_on_legacy_sources`: `CorpusFixture.LegacyAsAdhoc()`(테스트 도우미 — `eval/corpus/Legacy`의 4개 csproj에서 `<Compile Include>`와 `<ProjectReference>`를 읽어 AdhocWorkspace 솔루션을 만든다. 메타데이터는 Task 3과 같은 .NET 참조. `System.Web.Mvc` 등은 없어도 된다 — `_service.GetOrder` 바인딩에는 필드 타입만 필요하다) → `Matches == true`, `ForbiddenHit` 비어 있음.
  - `A3_reports_forbidden_when_name_matching_is_used`: 같은 솔루션에서 이름만으로 `GetOrder` 호출을 모으는 대조 함수 `CorpusCheck.NameOnlyKeys(Solution)` 결과를 비교기에 넣으면 `ForbiddenHit`에 `legacy/shop.web/controllers/ordercontroller.cs#detail`이 들어 있다. (비교기가 금지를 실제로 잡는지 확인하는 음성 대조.)
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.** 비교 부분은 `static A3Result CorpusCheck.Compare(IEnumerable<string> found, GoldCase gold)`로 떼어 두 테스트가 같이 쓴다.
- [ ] **Step 4: 통과 확인** — 4 passed. 이 시점의 전체 테스트 수를 원장에 적는다.
- [ ] **Step 5: 커밋** — `spike(S8): 참조 탐침과 a3 정답 대조`

### Task 5: 불러오기 실행기, 판정, 보고, CLI

**Files:** Create `LoadRunner.cs`, `Verdict.cs`, `LoadReport.cs`, 수정 `Program.cs`; Test `VerdictTests.cs`, `LoadReportTests.cs`, `LoadRunnerTests.cs`, `ProgramTests.cs`

**Interfaces:**
- Consumes: Task 1~4 전부.
- Produces:
  - `record IssueCount(WorkspaceIssueKind Kind, bool FromCSharpProject, int Count)`
  - `record LoadReport(string Mode /*"field"|"detail"*/, string Os, string ToolForm /*"folder"|"singlefile"|"singlefile+buildhost"|"dev"*/, string RoslynVersion, string? MsBuildGuess, CensusCounts Census, IReadOnlyList<IssueCount> Issues, double OpenSeconds, double CompileSeconds, double TotalSeconds, long PeakWorkingSetBytes, CallResolution Calls, HealthTotals Health, ProbeResult Probe, A3Result? A3, IReadOnlyList<string>? DetailMessages, IReadOnlyList<ProjectHealth>? DetailProjects)` — `CensusCounts(int SdkCs, int LegacyCs, int NonCs, int UnrestoredSdk, int Loaded)`, `HealthTotals(int CompileErrorsCounted, int CompileErrorsExcluded, int MissingMetadataRefs)`. `ToJson()`/`FromJson()` (Web 기본, 들여쓰기), `ToSummary()`(사람이 읽는 한 화면).
  - `static Task<LoadReport> LoadRunner.RunOnceAsync(string path, LoadOptions o, CancellationToken ct)`; `record LoadOptions(bool Detail, bool CheckA3, string ToolForm)`.
  - `record Judgement(string Item, bool? Pass, string Value)`; `static IReadOnlyList<Judgement> Verdict.Judge(IReadOnlyList<LoadReport> runs)` — 첫 회 = 콜드, 나머지 = 웜(웜이 없으면 웜 판정 `Pass = null`).
  - CLI: `s8 load <path> [--repeat N] [--out file] [--detail] [--check-corpus a3] [--form <이름>] [--once]`.

실행기 규칙: `MSBuildWorkspace.Create()` 뒤 `.sln`/`.slnx`면 `OpenSolutionAsync`, `.csproj`면 `OpenProjectAsync`의 솔루션. 보조 프로세스가 MSBuild를 직접 찾으므로 설계의 「MSBuild 찾기·등록」 단계는 `OpenSeconds`에 포함된다(별도 계측 없음). `CompileSeconds` = 모든 C# 프로젝트 `GetCompilationAsync` 합. 작업 공간 진단은 `workspace.Diagnostics`(5.x에서 `RegisterWorkspaceFailedHandler`로 받는 것도 합친다)를 `DiagnosticClassifier`로 나누고, 메시지에 C#이 아닌 프로젝트 파일 이름이 있으면 `FromCSharpProject = false`. 호출 해석률은 복원된 C# 프로젝트만(`include`), 컴파일 오류는 복원 안 된 프로젝트 몫을 `CompileErrorsExcluded`로 따로. `MsBuildGuess`: Windows면 `vswhere -latest -property installationVersion` 결과, 아니면 null — 「추정」 표시는 요약에서 붙인다. `PeakWorkingSetBytes` = `Process.GetCurrentProcess().PeakWorkingSet64`.

판정 항목(Global Constraints 값): `불러오기`(Loaded == CSharpTotal 그리고 FromCSharpProject 실패 유형 수 0 — `Other`·`UnsupportedProject`·`ReferenceResolution`·`MsBuildNotFound` 모두 실패로 센다), `타입 정보`(CompileErrorsCounted == 0, MissingMetadataRefs == 0, Calls.Rate ≥ 0.99), `콜드 시간`(CSharpTotal ≤ 4일 때만 OpenSeconds+CompileSeconds ≤ 15, 아니면 null), `웜 시간`(웜 평균 ≤ 10, 같은 조건), `참조 찾기`(Probe.MaxSeconds ≤ 2), `코퍼스 대조`(A3 있을 때 Matches).

`--repeat N`(기본 3): 자기 exe를 `--once`와 같은 인자로 N번 순서대로 띄우고, 각 자식이 표준 출력에 쓴 JSON 한 덩어리를 모아 `--out`에 `{ "runs": [...], "verdict": [...] }`로 쓰고 요약을 출력한다. 자기 exe 경로는 `Environment.ProcessPath`; `dotnet s8.dll`로 띄운 경우(개발)는 `dotnet` + `Assembly.Location`. 자식 중 하나라도 종료 3이면 부모도 3.

- [ ] **Step 1: 실패하는 테스트**
  - `VerdictTests`:
    - `Verdict_passes_clean_small_solution`: 깨끗한 보고 2개(콜드 12초, 웜 8초, Rate 1.0, 오류 0) → 모든 항목 `Pass == true`(A3 없음 → 코퍼스 항목 없음).
    - `Verdict_fails_on_low_call_rate`: Rate 0.98 → `타입 정보` false.
    - `Verdict_ignores_failures_from_non_csharp_projects`: Issues에 `(UnsupportedProject, FromCSharpProject:false, 1)`만 → `불러오기` true.
    - `Verdict_excludes_errors_of_unrestored_projects`: `CompileErrorsCounted 0, CompileErrorsExcluded 120` → `타입 정보` true.
    - `Verdict_time_not_judged_above_four_projects`: CSharpTotal 6 → 시간 두 항목 `Pass == null`.
  - `LoadReportTests`:
    - `Field_mode_json_has_no_names`: 픽스처 이름(`Shop.`, `.cs`, `/`, `\\`, `GetOrder`)이 든 `DetailMessages`/`DetailProjects`를 넣고 `Mode = "field"`로 만든 뒤 `ToJson()` → 그 문자열들이 없다(현장 모드에서는 Detail 목록을 null로 직렬화). `Mode = "detail"`이면 있다.
    - `Json_round_trips`: `FromJson(ToJson(r))`의 핵심 수치가 같다.
  - `LoadRunnerTests`(통합, 코퍼스):
    - `Modern_corpus_loads_clean_after_restore`: 테스트 시작 시 `dotnet restore eval/corpus/Modern/Modern.sln`을 프로세스로 돌린 뒤 `RunOnceAsync(Modern.sln)` → `Census.Loaded == 3`, `Health.CompileErrorsCounted == 0`, `Calls.Rate >= 0.99`, `Issues`에 FromCSharpProject 실패 없음.
    - `Legacy_corpus_on_non_windows_reports_msbuild_not_found`: `OperatingSystem.IsWindows()`면 `Assert.Skip` 대신 Windows 기대(실패 유형 0)로 단언한다. 아니면 `Issues`에 `MsBuildNotFound`가 있다(설계 검토에서 관측한 「Mono MSBuild not found」).
  - `ProgramTests`:
    - `Usage_error_exits_2`: 인자 없음 → 2.
    - `Missing_path_exits_2`: 없는 경로 → 2.
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인** — 전체 테스트 실패 0. 수동 확인: `dotnet run --project S8SemanticLoad -- load ../../eval/corpus/Legacy/Legacy.sln --repeat 2 --check-corpus a3` → macOS에서 종료 3, 요약에 「MSBuild를 찾을 수 없음」.
- [ ] **Step 5: 커밋** — `spike(S8): 불러오기 실행기·판정·보고·CLI`

### Task 6: 배포 형태 셋과 Windows 워크플로

**Files:** Modify `S8SemanticLoad.csproj`; Create `spikes/S8-semantic-load/publish.ps1`, `.github/workflows/s8-semantic-load.yml`; Test `PublishLayoutTests.cs`

**Interfaces:** Consumes Task 5 CLI(`--form`).

- csproj: 속성 `S8BuildHostOutside`가 `true`면 `BuildHost-net472/**`·`BuildHost-netcore/**` 출력 파일에 `ExcludeFromSingleFile="true"`를 준다(Content/None 항목 `Update`로). 방법은 맡기되 결과는 테스트가 정한다.
- `publish.ps1 -Rid <rid> -Out <dir>`: 세 형태를 `<dir>/folder`, `<dir>/singlefile`, `<dir>/singlefile-buildhost`에 만든다. 모두 `-c Release --self-contained`; 둘째·셋째는 `-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`; 셋째에 `-p:S8BuildHostOutside=true`. pwsh가 macOS에도 있으면 그대로, 없으면 같은 명령을 테스트가 직접 부른다.
- 워크플로 `s8-semantic-load.yml`: 트리거 `workflow_dispatch`와 `push: branches: [docs/s8-semantic-load]`, `paths: [spikes/S8-semantic-load/**, .github/workflows/s8-semantic-load.yml]`. 잡 하나 `windows-latest`:
  1. checkout, setup-dotnet(global.json)
  2. `./eval/corpus/Legacy/build.ps1`, `dotnet restore eval/corpus/Modern/Modern.sln`
  3. 스파이크 테스트 `dotnet test`(Windows에서 Legacy 기대가 뒤집히는지 확인)
  4. `publish.ps1 -Rid win-x64 -Out out`
  5. 세 형태 × {Modern.sln, Legacy.sln} 각각 `s8.exe load <sln> --repeat 3 --form <형태> --out out/<형태>-<대상>.json` (Legacy에는 `--check-corpus a3`), `continue-on-error`로 형태 하나의 실패가 다른 측정을 막지 않게 한다
  6. MSBuild 없음 흉내: `MSBUILD_EXE_PATH=C:\nope\MSBuild.exe`로 folder 형태 Legacy 1회(`--repeat 1`) → 종료 코드를 출력에 남긴다
  7. `vswhere -latest -property installationVersion` 출력
  8. `out/*.json`을 아티팩트 `s8-results`로 업로드
- [ ] **Step 1: 실패하는 테스트** — `PublishLayoutTests.Singlefile_buildhost_form_keeps_buildhost_folders_outside`: 테스트가 `dotnet publish S8SemanticLoad -c Release -r <현재 RID> --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:S8BuildHostOutside=true -o <임시>`를 돌린 뒤 `<임시>/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll`과 `<임시>/BuildHost-net472/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe`가 존재한다. 같은 명령에서 `S8BuildHostOutside`를 빼면 `BuildHost-netcore/...BuildHost.dll`이 **없다**(설계 검토 관측의 재현).
- [ ] **Step 2: 실패 확인** — 첫 단언 실패.
- [ ] **Step 3: 구현** csproj 조건, `publish.ps1`, 워크플로 파일.
- [ ] **Step 4: 통과 확인** — 테스트 통과. macOS에서 `singlefile-buildhost` 형태로 `load eval/corpus/Modern/Modern.sln --repeat 1`이 종료 0, `singlefile` 형태가 「build host not found」(`MsBuildNotFound`, 종료 3)인지 손으로 확인해 원장에 적는다. 워크플로는 `actionlint`가 있으면 돌리고, 없으면 YAML 문법만 `python3 -c "import yaml,sys;yaml.safe_load(open(sys.argv[1]))"`로 확인.
- [ ] **Step 5: 커밋** — `spike(S8): 배포 형태 셋과 Windows 워크플로`

### Task 7: 측정 실행, 보고서, 현장 안내 (사람 단계 포함)

이 작업의 6.2 부분은 브랜치 push가 필요하다. **push 전에 사람에게 묻는다.**

- [ ] **Step 1: macOS 측정** — `out/mac/`에 다음을 만든다.
  - `folder` 형태: Modern `--repeat 3`, Legacy `--repeat 3 --check-corpus a3`
  - Modern만 `singlefile`, `singlefile-buildhost` 형태 각 `--repeat 1`
- [ ] **Step 2: 사람 확인 후 push** — 사람이 승인하면 `git push -u origin docs/s8-semantic-load`. 워크플로가 끝나면 `gh run download -R moondae815/Pelly-Code -n s8-results -D out/win`.
- [ ] **Step 3: 보고서** `docs/s8-semantic-load.md`.
  - 환경별(macOS, Windows 러너) × 형태별 × 대상별 판정 표를 싣는다. 판정 기준은 Global Constraints 값 그대로다.
  - Roslyn 버전과 MSBuild 추정 버전, a3 대조 결과, 5.3(MSBuild 없음) 결과를 싣는다.
  - 한계를 싣는다:
    - 콜드에서 OS 파일 캐시를 통제하지 않았다.
    - 분류 규칙이 「SDK를 못 찾음」을 「MSBuild를 못 찾음」으로 셀 수 있다(Task 2).
    - 러너의 Visual Studio 버전이 현장(2025)과 다를 수 있다.
  - 현장 결과 칸은 비워 둔다. 설계 §7의 「다음 행동」 표에서 지금 결과로 어느 줄에 해당하는지를 「현장 전 잠정」으로 적는다.
- [ ] **Step 4: 현장 안내** `artifacts/field-docs/s8-field-guide.html`(gitignore 대상). 다음을 담는다.
  - 들고 갈 것: 되는 배포 형태의 폴더 zip
  - 전제: Visual Studio에서 솔루션을 한 번 빌드한 직후에 잰다
  - 명령 `s8.exe load <솔루션> --repeat 3 --out <파일>`
  - 결과를 옮겨 적을 표: 판정 항목별 값과 통과 여부. 숫자만이다.
  - 금지: `--detail`을 쓰지 않는다
  - 화면 폭 390px에서 가로로 넘치지 않는지 확인한다.
- [ ] **Step 5: 커밋** — `git add docs/s8-semantic-load.md && git commit -m "docs: S8 의미 분석 불러오기 측정 결과(현장 전)"`. `out/`은 gitignore다.
