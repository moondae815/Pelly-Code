# V2-1c 영향 탐색·배포·완료율·E2E 벤치마크 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 다음 넷을 만들어 V2-1을 마무리한다.
- `pelly spec check`·`pelly apply`의 「빠진 영향 대상」 경고: Roslyn 심볼로 찾고, 안 되면 이름으로 찾되 그 사실을 표시한다.
- 의미 분석 보조 프로세스(BuildHost)를 반입 묶음에 넣고, `pelly doctor`가 의미 분석 가능 여부를 보고한다.
- `pelly report`가 명세 완료율(PRD §12.6)과 그룹 빌드 통과율을 센다.
- S6 정답 12건으로 `pelly apply` E2E 벤치마크(`pelly-eval apply`)를 돌린다.

**Architecture:**
- 영향 탐색은 `PellyCoder.Core/Impact/`에 둔다. 진입점은 `IImpactFinder` 하나다.
  - 실제 구현 `SemanticImpactFinder`는 다음 순서로 일한다.
    1. 시드 파일의 프로젝트에서 위쪽(참조하는 프로젝트)과 아래쪽(참조되는 프로젝트) 범위를 정한다.
    2. 그 범위를 `MSBuildWorkspace`로 연다.
    3. S8 판정 기준(해석률 99%·컴파일 오류 0·빌드 도구)으로 건강을 본다.
    4. 건강하면 심볼로, 아니면 `NameImpactFinder`(S6 `CallerFinder` 이식)로 찾는다.
  - 결과는 `SpecIssue` 경고가 되어 `spec check`·`apply` 시작 화면·`apply.json`에 남는다.
- 배포는 S8 형태 3을 쓴다: 단일 파일 exe와 그 옆의 `BuildHost-net472/`·`BuildHost-netcore/` 폴더. 반입 zip 목록이 폴더 항목(`/**`)을 받도록 넓힌다.
- 완료율은 `apply.json`(V2-1a)에 필드를 더하고 `pelly report`가 읽어 센다.
- 벤치마크는 `eval/PellyCoder.Eval`에 `apply` 명령으로 더한다. `eval/corpus` 전체를 한 작업 공간으로 복사해 명세를 실행한다.

**Tech Stack:** .NET 10, Roslyn 5.9.0(`Microsoft.CodeAnalysis.CSharp`, 새로 `Microsoft.CodeAnalysis.CSharp.Workspaces`·`Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.9.0), System.IO.Abstractions, xunit v3, PowerShell 7(배포 스크립트).

**Spec:** `docs/superpowers/specs/2026-10-24-v2-1-apply-design.md`. 이 계획이 다루는 범위는 다음과 같다.
- §5의 5(빠진 영향 대상)
- §3의 BuildHost 배포 제약
- §6 「기록」의 `pelly report` 명세 완료율
- §8의 E2E 벤치마크와 의미 분석 배포 확인

근거: `docs/s8-semantic-load.md`(판정 기준·배포 형태·a3), `spikes/S8-semantic-load/S8SemanticLoad/`(SemanticCallerFinder·SemanticMetrics·DiagnosticClassifier), `spikes/S6-planning/S6Planning/CallerFinder.cs`, `spikes/S6-planning/cases/`(승인된 정답 12건).

## Global Constraints

- **보존:** v1 `pelly cs`·`pelly sql`, V2-1a·V2-1b apply의 동작·프롬프트·종료 코드·기존 테스트는 바뀌지 않는다. 영향 탐색은 경고만 만든다. 오류(종료 4)를 새로 만들지 않는다.
- **게이트:** `dotnet build -warnaserror` 경고 0, `dotnet test` 종료 코드 0(실패 0).
  - 새 패키지가 끌어오는 전이 패키지에 취약 경고(NU190x)가 나면 `Directory.Packages.props`에 고정 버전을 더한다. S8에서는 `System.Security.Cryptography.Pkcs` 10.0.13이었고 이미 고정되어 있다.
- **영향 탐색을 돌리는 때(설계 §5의 5):**
  - `spec check`는 명세에 그룹이 있으면 그룹 `cs` 작업에 대해, `--impact`면 모든 `cs` 작업에 대해 돌린다.
  - `apply` 시작 검사는 그룹 `cs` 작업에 대해서만 돌린다.
  - 완료(`[x]`) 작업은 시드에서 뺀다. 다만 「명세에 있음」 대조에는 모든 작업을 쓴다.
- **찾는 것:** 시드 멤버(같은 이름의 오버로드 전부)에서 시작한다.
  - 아래쪽: 구현·재정의, 그리고 시드·구현·재정의를 부르거나 참조하는 멤버
  - 위쪽(Claude 결정): 시드가 구현하거나 재정의한 인터페이스·기반 멤버, 그 멤버의 다른 구현·재정의와 그 호출부
  - 메서드·속성 시드만 다룬다. 그 밖의 시드는 경고 `ImpactUnsupported`를 남기고 건너뛴다.
- **참조한 곳의 멤버 이름:** 메서드·속성·생성자는 `MemberLocator`와 같은 이름을 쓴다(생성자는 타입 이름). 그 밖(필드 초기화·인덱서·연산자 등)은 「멤버 밖」으로 감싼 타입 이름을 쓴다. 찾은 곳을 버리지 않는다.
- **명세에 있음 판정:** 찾은 곳 `(경로, 멤버)`는 명세의 어떤 작업과 다음이 둘 다 같으면 「있음」이다.
  - 경로: `PathComparers.ForOs`, `\`·`/` 정규화
  - 멤버: `SpecSymbol.Split(symbol).Member`
- **경고 코드와 문구(사용자 문구는 한국어):**
  - `ImpactMissing`(작업 줄): `작업 {시드 작업 ID}의 영향 대상이 명세에 없습니다: {경로}#{멤버} ({이유})`. 이유는 `구현`·`재정의`·`기반 멤버`·`호출`·`멤버 밖` 중 하나다. 찾은 곳 하나에 경고 하나를 남긴다.
  - `ImpactNameBased`(줄 0): `영향 탐색: 이름 기준(부정확): 의미 분석 실패 — {유형}`. 이름 기준으로 대신했으면 반드시 남긴다(설계 §2 실패 조건).
  - `ImpactUnsupported`(작업 줄): `작업 {ID}: 영향 탐색은 메서드·속성만 봅니다.`
- **의미 분석 실패 유형(`SemanticFailureKind` → 표시):** 모두 이름 기준으로 대신한다.
  - `MsBuildNotFound` → `빌드 도구 없음`
  - `NotRestored` → `복원 안 됨 (dotnet restore 필요)`
  - `LoadFailed` → `불러오기 실패`
  - `LowResolution` → `해석률 {0.0%}`
  - `CompileErrors` → `컴파일 오류 {n}건`
  - `Timeout` → `시간 초과`
  - `ScopeUnknown` → `영향 범위를 확정할 수 없음`
  - `NoProject` → `프로젝트 없음`
- **판정 수치(S8 고정값):**
  - 해석률 하한은 0.99다. 분모에서 `nameof`·늦은 바인딩·`dynamic` 호출을 뺀다. 호출이 0개면 1.0으로 본다.
  - 컴파일 오류는 0이어야 한다.
  - 의미 분석 불러오기 제한 시간은 120초다(Claude 결정).
- **배포(설계 §3):**
  - `win-x64` 단일 파일 publish에서 `BuildHost-*` 파일은 묶음에서 빼고 exe 옆 폴더로 둔다. `pelly`와 `pelly-eval`(현장 키트)이 모두 해당한다.
  - 반입 zip과 현장 키트 zip에 두 폴더가 통째로 들어간다.
- **종료 코드:** v1과 같다. `pelly-eval apply`는 `run`과 같은 규칙이다(0 끝남, 1 검증 불변 조건 위반, 2 취소, 4 준비 오류).
- **문구·커밋:** 사용자 문구는 한국어, 코드 주석은 영어다. 커밋 꼬리는 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`다. 테스트가 심볼릭 링크를 만들면 권한이 없을 때 건너뛴다. 경로 단언에 `/`를 박지 않는다(Windows CI).

## Review Focus

1. **macOS에서 레거시를 의미 분석할 때:** 예외 없이 열리지만 해석률이 2.8%다(S8). 그래도 결과가 「심볼 기준」으로 나가면 안 된다. `LowResolution`으로 이름 기준으로 대신하고 `ImpactNameBased`를 남겨야 한다(Task 2·3 테스트).
2. **단일 파일 exe 옆에 BuildHost 폴더가 없을 때:** 「build host not found」가 `MsBuildNotFound`로 분류되어야 한다. 그러면 이름 기준으로 대신하고 doctor는 Fail이다. 예외로 apply가 죽으면 안 된다(Task 2·6 테스트).
3. **시드 파일이 어떤 `.csproj`에도 속하지 않거나 작업 공간에 프로젝트가 없을 때:** `NoProject`로 이름 기준 탐색을 하고 경고를 남긴다. 범위 탐색이 `ProjectScopeException`을 던지면 `ScopeUnknown`이다(Task 3 테스트).
4. **partial 클래스·오버로드:** 시드 멤버가 여러 파일·여러 오버로드에 걸쳐 있으면 모든 위치와 그 호출부가 나와야 한다. 명세가 한 파일만 적었으면 다른 partial 파일은 `ImpactMissing`이다(Task 3 테스트).
5. **같은 이름의 다른 인터페이스(S8 a3 모양):** 심볼 탐색은 `IOrderService.GetOrder`와 그 호출부를 잡지 않고, 이름 탐색은 잡는다. 이름 탐색이 잡는 것은 알려진 한계로 고정한다(Task 1·3 테스트, SDK 픽스처라 macOS에서도 돈다).

---

## File Structure

- `src/PellyCoder.Core/Impact/` (새 폴더)
  - `ImpactModel.cs`: `ImpactSeed`, `ImpactTarget`, `ImpactResult`, `ImpactMode`, `SemanticFailureKind`, `IImpactFinder`, `SemanticFailures.Display`
  - `ImpactScope.cs`: 불러올 프로젝트 범위
  - `NameImpactFinder.cs`: 구문·이름 기준 탐색(S6 이식)
  - `SemanticLoader.cs`: `ISemanticLoader`, `SemanticLoad`, `MsBuildSemanticLoader`
  - `SemanticHealth.cs`: S8 판정(해석률·컴파일 오류·진단 분류) 이식
  - `SemanticImpactFinder.cs`: 심볼 탐색과 대체 경로
  - `ImpactCheck.cs`: 명세 작업 → 시드, 결과 → `SpecIssue`
- `src/PellyCoder.Core/Specs/`
  - `SpecChecker.cs`: `CheckAsync` 추가
  - `SpecRunner.cs`: `IImpactFinder?` 주입, `apply.json` 필드
  - `ApplySummary.cs`: 필드 추가
  - `SpecRunnerFactory.cs`(새): Cli·Eval이 같이 쓰는 조립
- `src/PellyCoder.Core/Reporting/ApplyHistory.cs`(새), `ApplyReport.cs`(새), `ReportSanitizer.cs`
- `src/PellyCoder.Core/Doctor/SemanticCheck.cs`(새), `OfflineSelfTest.cs`, `DoctorFacts.cs`, `DoctorSanitizer.cs`, `DoctorService.cs`
- `src/PellyCoder.Cli/Commands/SpecCheckCommand.cs`, `ApplyCommand.cs`, `ReportCommand.cs`
- `build/BuildHostOutside.targets`(새), `src/PellyCoder.Cli/PellyCoder.Cli.csproj`, `eval/PellyCoder.Eval/PellyCoder.Eval.csproj`
- `deploy/package-files.txt`, `deploy/field-kit-files.txt`, `deploy/build-package.ps1`, `.github/workflows/ci.yml`
- `eval/benchmark/apply/*.md`(새, 12건), `eval/PellyCoder.Eval/ApplyBenchmark*.cs`(새)
- 테스트
  - `tests/PellyCoder.Core.Tests/Impact/`(새)
  - `tests/PellyCoder.Integration.Tests/Impact/`(새, 실제 MSBuildWorkspace)
  - `tests/fixtures/impact/SameName/`(새, a3 모양 SDK 픽스처)

---

### Task 1: 영향 탐색 모델, 범위, 이름 기준 탐색

**Files:**
- Create: `src/PellyCoder.Core/Impact/ImpactModel.cs`, `src/PellyCoder.Core/Impact/ImpactScope.cs`, `src/PellyCoder.Core/Impact/NameImpactFinder.cs`
- Create: `tests/fixtures/impact/SameName/` — SDK 프로젝트 셋, `SameName.sln`. a3 모양이다.
  - `Data/IOrderDao.cs`(`Order GetOrder(int id)`), `Data/OrderDao.cs`(구현)
  - `Web/IOrderService.cs`(`Order GetOrder(int id)`, 같은 이름의 다른 인터페이스), `Web/OrderService.cs`(`IOrderService` 구현, `_dao.GetOrder` 호출하는 `GetOrder`·`Cancel`)
  - `Web/OrderController.cs`(`Detail`이 `_service.GetOrder` 호출)
  - `Web.Tests/FakeOrderDao.cs`(`IOrderDao` 구현)
  - 패키지 참조는 없다(오프라인 복원 가능). `Web`은 `Data`를, `Web.Tests`는 `Web`·`Data`를 참조한다.
- Test: `tests/PellyCoder.Core.Tests/Impact/ImpactScopeTests.cs`, `tests/PellyCoder.Core.Tests/Impact/NameImpactFinderTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace PellyCoder.Core.Impact;
  public enum ImpactMode { Semantic, Name }
  public enum SemanticFailureKind { MsBuildNotFound, NotRestored, LoadFailed, LowResolution, CompileErrors, Timeout, ScopeUnknown, NoProject }
  public sealed record SemanticFailure(SemanticFailureKind Kind, double? Rate = null, int? Count = null) { public string Display { get; } } // 표시는 Global Constraints 표대로
  public sealed record ImpactSeed(string TaskId, int Line, string Path /* 작업 공간 상대, '/' */, string? ClassName, string Member);
  public sealed record ImpactTarget(string SeedTaskId, string Path /* 작업 공간 상대, '/' */, string Member, string Reason /* 구현|재정의|기반 멤버|호출|멤버 밖 */);
  public sealed record ImpactResult(ImpactMode Mode, SemanticFailure? Fallback, IReadOnlyList<ImpactTarget> Targets, IReadOnlyList<string> UnsupportedSeeds /* TaskId */);
  public interface IImpactFinder { Task<ImpactResult> FindAsync(Workspace ws, IReadOnlyList<ImpactSeed> seeds, CancellationToken ct); }
  public static class ImpactScope { public static IReadOnlyList<string> Projects(IFileSystem fs, Workspace ws, IReadOnlyCollection<string> seedFiles /* 전체 경로 */); }
  public static class NameImpactFinder { public static ImpactResult Find(IFileSystem fs, Workspace ws, IReadOnlyList<ImpactSeed> seeds, IReadOnlyList<string> projects, SemanticFailure fallback); }
  ```
- Consumes: `ProjectFileLocator.FindNearestCsproj`, `ProjectReferenceGraph.References`/`AffectedProjects`(V2-1b, `ProjectScopeException`), `BoundedWalker.Files`, `MemberLocator` 이름 규칙.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `ImpactScopeTests.Projects_are_seed_projects_their_references_and_every_dependent_of_those`: 픽스처에서 시드 `Web/OrderService.cs`를 넣으면 결과가 다음 셋을 모두 담는다.
    - `Web.csproj`(시드)
    - `Data.csproj`(위쪽 참조)
    - `Web.Tests.csproj`(`Data`·`Web`의 의존자)
  - `ImpactScopeTests.Seed_file_outside_any_project_gives_no_project`: 시드가 `.csproj` 없는 폴더의 파일이면 빈 목록이다.
  - `ImpactScopeTests.Scope_exception_propagates`: `$(` 참조를 가진 프로젝트가 있으면 `ProjectScopeException`이 그대로 나온다.
  - `NameImpactFinderTests.Same_name_interface_is_a_known_false_hit` (Review Focus 5): 시드 `Data/IOrderDao.cs#GetOrder`로 찾으면 대상에 다음이 모두 들어 있다.
    - `Data/OrderDao.cs#GetOrder`(구현)
    - `Web.Tests/FakeOrderDao.cs#GetOrder`(구현)
    - `Web/OrderService.cs#GetOrder`·`#Cancel`(호출)
    - `Web/OrderController.cs#Detail`(이름이 같아 잡히는 오탐)
    - 결과 `Mode`는 `Name`, `Fallback`은 넘겨준 값 그대로다.
  - `NameImpactFinderTests.Caller_in_constructor_and_field_initializer_is_reported`: 생성자 안 호출은 `#<타입 이름>`(`호출`)이고, 필드 초기화 안 호출은 `#<타입 이름>`(`멤버 밖`)이다.
  - `NameImpactFinderTests.Seed_itself_is_never_a_target`: 시드 위치 자체는 결과에 없다.
  - `NameImpactFinderTests.Non_method_seed_is_unsupported`: 필드 시드는 `UnsupportedSeeds`에 들어간다.
- [ ] **Step 2: 실행해 실패를 본다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~PellyCoder.Core.Tests.Impact"` → 컴파일 실패(형식 없음)
- [ ] **Step 3: 구현한다**
  - `ImpactScope.Projects`:
    1. 시드 파일마다 가장 가까운 `.csproj`를 찾는다. 없는 파일은 빼고, 모두 없으면 빈 목록을 돌려준다.
    2. `References`로 위쪽 닫힘을 구한다.
    3. `AffectedProjects(fs, ws.Root, 시드 ∪ 위쪽)`로 의존자를 더한다. 순서는 `AffectedProjects`를 따르고, 위쪽 프로젝트는 입력 쪽에 둔다.
  - `NameImpactFinder.Find`:
    - 파일 집합: `projects`가 비어 있지 않으면 각 프로젝트 폴더의 `*.cs`, 비어 있으면 `ws.Root` 전체의 `*.cs`다. `BoundedWalker.Files`로 걷고 상한은 2000이다.
    - 규칙은 S6 `CallerFinder`를 따른다.
      - 시드 타입의 기반 목록에 있는 같은 이름 멤버를 `기반 멤버`로 넣는다.
      - 시드 타입이나 그 기반 타입을 기반 목록에 가진 타입의 같은 이름 멤버를 `구현`으로 넣는다.
      - 같은 이름을 부르는 멤버 접근·식별자 호출을 감싼 멤버를 `호출`로 넣는다.
    - 정렬은 경로(서수) 다음 위치이고 중복은 없다.
- [ ] **Step 4: 실행해 통과를 본다** — 같은 명령 → PASS
- [ ] **Step 5: 커밋** — `feat(impact): impact model, project scope and the name-based finder`

### Task 2: 의미 분석 불러오기와 건강 판정

**Files:**
- Modify: `Directory.Packages.props`(`Microsoft.CodeAnalysis.CSharp.Workspaces`·`Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.9.0), `src/PellyCoder.Core/PellyCoder.Core.csproj`(두 PackageReference)
- Create: `src/PellyCoder.Core/Impact/SemanticLoader.cs`, `src/PellyCoder.Core/Impact/SemanticHealth.cs`
- Test: `tests/PellyCoder.Core.Tests/Impact/SemanticHealthTests.cs`(진단 문자열 분류·판정, 순수), `tests/PellyCoder.Integration.Tests/Impact/MsBuildSemanticLoaderTests.cs`(실제 불러오기)

**Interfaces:**
- Produces:
  ```csharp
  public sealed class SemanticLoad(Solution? solution, SemanticFailure? failure, IDisposable? owner) : IDisposable { public Solution? Solution; public SemanticFailure? Failure; }
  public interface ISemanticLoader { Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct); }
  public sealed class MsBuildSemanticLoader(IFileSystem fs, TimeSpan? timeout = null) : ISemanticLoader; // 기본 120초
  public static class SemanticHealth
  {
      public const double MinCallRate = 0.99;
      public static SemanticFailureKind? Classify(string workspaceDiagnosticMessage); // S8 DiagnosticClassifier 이식: MsBuildNotFound | NotRestored | LoadFailed(지원 안 함·참조 해석) | null(그 밖)
      public static Task<SemanticFailure?> JudgeAsync(Solution s, IReadOnlyList<WorkspaceDiagnostic> diagnostics, IReadOnlyCollection<string> requested, CancellationToken ct);
  }
  ```
- Consumes: Task 1 `SemanticFailure`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `SemanticHealthTests.Classify_*` (S8 `DiagnosticClassifierTests`의 사례를 옮긴다):
    - `"Msbuild failed when processing the file '…' with message: The SDK 'Microsoft.NET.Sdk' specified could not be found."` → `MsBuildNotFound`
    - `"build host not found"` → `MsBuildNotFound`(Review Focus 2)
    - `project.assets.json … not found` → `NotRestored`
    - 감싼 말 `Msbuild failed … with message: <다른 오류>`의 `msbuild`로는 `MsBuildNotFound`가 되지 않는다.
  - `MsBuildSemanticLoaderTests` (실제 불러오기, Integration):
    - 테스트 준비: 픽스처 `SameName`을 임시 폴더에 복사하고 `dotnet restore`를 한 번 한다.
    - `Loads_restored_sdk_projects_healthily`: 세 프로젝트를 불러오면 `Failure`가 null이고 `Solution`에 세 C# 프로젝트가 있다.
    - `Unrestored_project_is_NotRestored`: 복원 전 복사본은 `NotRestored`다.
    - `Timeout_is_reported`: 제한 시간 1ms면 `Timeout`이다. 예외로 나오면 안 된다. 취소 토큰 자체의 취소는 `OperationCanceledException`으로 나온다.
    - `Legacy_on_non_windows_is_low_resolution_or_load_failure` (Review Focus 1, macOS 전용 — `[Fact(Skip…)]` 대신 런타임 조건 건너뜀):
      - 대상은 `eval/corpus/Legacy`의 한 csproj다.
      - `Failure`는 null이 아니고 종류는 `LowResolution`·`CompileErrors`·`LoadFailed`·`MsBuildNotFound` 중 하나다.
- [ ] **Step 2: 실행해 실패를 본다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~SemanticHealth"` → 컴파일 실패
- [ ] **Step 3: 구현한다**
  - `MsBuildSemanticLoader.LoadAsync`:
    - `MSBuildWorkspace.Create()`로 만들고 `RegisterWorkspaceFailedHandler`로 진단을 모은다.
    - 프로젝트마다 `OpenProjectAsync`를 부른다. 이미 열린 경로는 건너뛴다.
    - 모든 C# 프로젝트를 `GetCompilationAsync`한 뒤 `JudgeAsync`로 판정한다.
    - 제한 시간은 연결된 `CancellationTokenSource`로 건다. 제한 시간으로 취소됐으면 `Timeout`, 호출자 취소는 그대로 던진다.
    - 불러오기 중 그 밖의 예외는 `LoadFailed`다.
  - `JudgeAsync` 판정 순서:
    1. 분류가 `MsBuildNotFound`인 진단이 있으면 → `MsBuildNotFound`
    2. 요청한 프로젝트 중 `obj/project.assets.json`이 없는 SDK 프로젝트가 있거나 `NotRestored` 진단이 있으면 → `NotRestored`
    3. 요청한 프로젝트가 다 열리지 않았거나 `Failure` 종류 진단이 있으면 → `LoadFailed`
    4. 해석률 < 0.99이면 → `LowResolution(rate)`. 분모 제외 규칙은 S8 `SemanticMetrics.IsExcluded`다.
    5. 컴파일 오류 > 0이면 → `CompileErrors(n)`
    6. 그 밖은 null
- [ ] **Step 4: 실행해 통과를 본다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~SemanticHealth"`, `dotnet test tests/PellyCoder.Integration.Tests --filter "FullyQualifiedName~MsBuildSemanticLoader"` → PASS. `dotnet build -warnaserror` 경고 0(전이 패키지 취약 경고가 나면 고정 버전 추가).
- [ ] **Step 5: 커밋** — `feat(impact): load projects with MSBuildWorkspace and judge them by the S8 criteria`

### Task 3: 심볼 기준 영향 탐색과 대체 경로

**Files:**
- Create: `src/PellyCoder.Core/Impact/SemanticImpactFinder.cs`
- Test: `tests/PellyCoder.Core.Tests/Impact/SemanticImpactFinderFallbackTests.cs`(가짜 `ISemanticLoader`), `tests/PellyCoder.Integration.Tests/Impact/SemanticImpactFinderTests.cs`(실제 불러오기, 픽스처 복원)

**Interfaces:**
- Produces: `public sealed class SemanticImpactFinder(IFileSystem fs, ISemanticLoader loader) : IImpactFinder`, `public static class ImpactFinders { public static IImpactFinder Default(IFileSystem fs); }`(= `new SemanticImpactFinder(fs, new MsBuildSemanticLoader(fs))`)
- Consumes: Task 1 `ImpactScope`·`NameImpactFinder`, Task 2 `ISemanticLoader`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - Fallback(가짜 로더):
    - `Load_failure_falls_back_to_names_with_its_kind`: 로더가 `LowResolution(0.028)`을 돌려주면 결과는 `Mode=Name`, `Fallback.Kind=LowResolution`이고 대상은 이름 탐색 결과다.
    - `No_project_falls_back_without_loading` (Review Focus 3): 시드가 프로젝트 밖이면 로더를 부르지 않고 `NoProject`다.
    - `Scope_exception_falls_back_as_ScopeUnknown`: 범위 탐색이 예외면 로더를 부르지 않고 `ScopeUnknown`이다. 이름 탐색은 작업 공간 전체로 한다.
    - `Cancellation_propagates`: 취소는 대체 경로로 바꾸지 않고 그대로 던진다.
  - Integration(픽스처 `SameName` 복원본):
    - `Interface_seed_finds_implementations_and_callers_but_not_the_same_named_interface` (Review Focus 5, S8 a3 회귀):
      - 시드 `Data/IOrderDao.cs#GetOrder`의 대상은 정확히 다음 넷이다.
        - `Data/OrderDao.cs#GetOrder`(구현)
        - `Web.Tests/FakeOrderDao.cs#GetOrder`(구현)
        - `Web/OrderService.cs#GetOrder`(호출)
        - `Web/OrderService.cs#Cancel`(호출)
      - `Web/OrderController.cs#Detail`과 `Web/IOrderService.cs#GetOrder`는 없다. `Mode=Semantic`이다.
    - `Class_seed_goes_up_to_its_interface` (위쪽): 시드 `Data/OrderDao.cs#GetOrder`의 대상에 다음이 있다.
      - `Data/IOrderDao.cs#GetOrder`(기반 멤버)
      - `Web.Tests/FakeOrderDao.cs#GetOrder`(구현)
      - 위 호출부들
    - `Partial_class_and_overloads_report_every_location` (Review Focus 4): 픽스처에 `Data/OrderDao.Part2.cs`(partial)와 `GetOrder(string code)` 오버로드를 둔다. 시드 `Data/OrderDao.cs#GetOrder`의 대상에 `Data/OrderDao.Part2.cs#GetOrder`가 있다.
- [ ] **Step 2: 실행해 실패를 본다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~SemanticImpactFinder"` → 컴파일 실패
- [ ] **Step 3: 구현한다**
  - 범위: `ImpactScope.Projects`의 예외는 `ScopeUnknown`, 빈 목록은 `NoProject`다. 둘 다 이름 탐색으로 간다.
  - `loader.LoadAsync`의 `Failure`가 있으면 이름 탐색으로 간다(같은 범위 목록).
  - 성공하면 시드 심볼을 찾는다. 시드 파일 문서에서 `ClassName`(없으면 그 멤버를 선언한 첫 타입)의 `GetMembers(Member)` 중 메서드·속성을 고른다.
    - 위쪽: `ExplicitOrImplicitInterfaceImplementations`(메서드·속성)와 재정의 사슬을 따른다. 찾은 것은 `기반 멤버`로 넣고 탐색 가족에 더한다.
    - 아래쪽: `SymbolFinder.FindImplementationsAsync`·`FindOverridesAsync`로 찾는다(가족의 각 멤버). 찾은 것은 `구현`·`재정의`다.
    - 호출: `SymbolFinder.FindReferencesAsync`의 위치마다 감싼 멤버를 찾는다. 이름 규칙은 Global Constraints대로다.
  - 시드 자신(시드 심볼의 모든 위치)은 대상에서 뺀다. 정렬·중복 규칙은 Task 1과 같다.
  - S8 `SemanticCallerFinder`가 근거다. 이식하면서 위쪽 탐색과 생성자·멤버 밖 처리를 더한다.
- [ ] **Step 4: 실행해 통과를 본다** — Core·Integration 필터 둘 다 PASS
- [ ] **Step 5: 커밋** — `feat(impact): symbol-based impact search with a labelled name fallback`

### Task 4: `spec check`·`apply`에 연결

**Files:**
- Create: `src/PellyCoder.Core/Impact/ImpactCheck.cs`, `src/PellyCoder.Core/Specs/SpecRunnerFactory.cs`
- Modify: `src/PellyCoder.Core/Specs/SpecChecker.cs`, `src/PellyCoder.Core/Specs/SpecRunner.cs`, `src/PellyCoder.Core/Specs/ApplySummary.cs`, `src/PellyCoder.Cli/Commands/SpecCheckCommand.cs`(`--impact`, `AsyncCommand`로), `src/PellyCoder.Cli/Commands/ApplyCommand.cs`, `README.md`·`deploy/usage.md`(`spec check`·`apply` 절, 「아직 지원하지 않는 것: 영향 탐색(V2-1c)」 줄 삭제)
- Test: `tests/PellyCoder.Core.Tests/Impact/ImpactCheckTests.cs`, `tests/PellyCoder.Core.Tests/Specs/SpecRunnerImpactTests.cs`, `tests/PellyCoder.Cli.Tests/SpecCheckCommandTests.cs`(있으면 그 파일에 추가)

**Interfaces:**
- Produces:
  ```csharp
  public static class ImpactCheck
  {
      public static IReadOnlyList<ImpactSeed> Seeds(ChangeSpec spec, bool allCsTasks); // 그룹 cs 작업(또는 모든 cs), [x] 제외
      public static IReadOnlyList<SpecIssue> Issues(ChangeSpec spec, IReadOnlyList<ImpactSeed> seeds, ImpactResult result);
  }
  // SpecCheckResult에 ImpactResult? Impact = null 매개변수 추가
  public static Task<SpecCheckResult> SpecChecker.CheckAsync(IFileSystem fs, string specPath, int sqlCompatLevel, IImpactFinder? impact, bool impactAllCsTasks, CancellationToken ct);
  // SpecRunner 생성자 끝에 IImpactFinder? impact = null
  public static class SpecRunnerFactory
  {
      public static SpecRunner Create(IFileSystem fs, PellyOptions options, IUserInteraction ui, TimeProvider time, IProcessRunner processes,
          Func<RunRequest, CancellationToken, Task<RunResult>> run, IImpactFinder? impact, Func<bool>? isWindows = null);
  }
  // ApplySummary에 string? Impact("semantic"|"name"|null), string? ImpactFallback(SemanticFailureKind 이름) 추가 — Task 7이 다른 필드를 더한다
  ```
- Consumes: Task 3 `ImpactFinders.Default`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `ImpactCheckTests.Seeds_are_group_cs_tasks_not_done`: 그룹 `cs` 둘, 비그룹 `cs` 하나, `sql` 하나, 완료 그룹 작업 하나로 `allCsTasks=false`를 주면 시드는 미완료 그룹 둘이다. `true`면 미완료 `cs` 셋이다.
  - `ImpactCheckTests.Target_named_by_any_task_is_not_missing`: 대상이 `[x]` 작업을 포함한 어떤 작업과 경로(대소문자는 `PathComparers.ForOs`, `\` 정규화)와 멤버로 같으면 경고가 없다. 다르면 `ImpactMissing` 경고가 시드 작업 줄에 정확한 문구로 하나 생긴다.
  - `ImpactCheckTests.Name_mode_always_warns_ImpactNameBased`: `Mode=Name`, `Fallback=LowResolution(0.028)`이면 줄 0에 `영향 탐색: 이름 기준(부정확): 의미 분석 실패 — 해석률 2.8%`가 생긴다.
  - `ImpactCheckTests.Unsupported_seed_warns`
  - `SpecRunnerImpactTests.Apply_start_runs_impact_on_group_tasks_and_records_it`: 가짜 `IImpactFinder`가 대상 하나를 돌려주면 다음이 모두 맞다.
    - `--yes` apply가 경고를 기록하고 계속한다.
    - `apply.json`에 `ImpactMissing` 경고와 `impact: "name"|"semantic"`이 들어 있다.
  - `SpecRunnerImpactTests.No_group_no_impact_call`: 그룹 없는 명세는 finder를 부르지 않는다. `impact`는 null이다.
  - `SpecRunnerImpactTests.Impact_never_blocks_apply`: finder가 예외(취소 외)를 던지면 경고 `ImpactNameBased`가 아니라 `ImpactFailed`(`영향 탐색을 할 수 없습니다: {메시지}`)를 남기고 계속한다. 종료 코드는 바뀌지 않는다.
  - CLI: `spec check --impact`가 finder를 모든 `cs` 작업에 부르고 경고만 있으면 종료 0이다.
- [ ] **Step 2: 실행해 실패를 본다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~ImpactCheck|FullyQualifiedName~SpecRunnerImpact"` → 컴파일 실패
- [ ] **Step 3: 구현한다**
  - `CheckAsync` = `Check` 결과에 오류가 없고 시드가 있을 때만 finder를 부른다. 이슈를 더한 새 `SpecCheckResult`를 돌려준다.
  - `SpecRunner.RunAsync`는 `CheckAsync(…, impact, false, ct)`를 쓴다. `ApplyRun.Finish`는 `Impact`·`ImpactFallback`을 쓴다.
  - `ApplyCommand`와 앞으로의 eval은 `SpecRunnerFactory.Create`로 조립한다. `ApplyCommand`의 지금 조립 코드를 그대로 옮긴다. finder는 `ImpactFinders.Default(fs)`다.
  - `SpecCheckCommand`는 `AsyncCommand`로 바꾸고 `--impact`(설명 「모든 cs 작업의 빠진 영향 대상을 찾음」)를 더한다.
  - 경고 이슈 코드 `ImpactFailed`를 새로 둔다. finder 예외(취소 제외)일 때 쓴다(Claude 결정: 경고 기능이 apply를 막지 않는다).
- [ ] **Step 4: 실행해 통과를 본다** — 같은 필터와 `dotnet test tests/PellyCoder.Cli.Tests` → PASS. 문서 두 곳(README·usage)은 같은 문장으로 맞춘다.
- [ ] **Step 5: 커밋** — `feat(spec): missing-impact warnings in spec check (--impact) and at apply start`

### Task 5: BuildHost 배포

**Files:**
- Create: `build/BuildHostOutside.targets`. S8 `S8KeepBuildHostOutside` 타깃을 조건 `'$(PublishSingleFile)' == 'true'`로 옮긴다.
- Modify: `src/PellyCoder.Cli/PellyCoder.Cli.csproj`, `eval/PellyCoder.Eval/PellyCoder.Eval.csproj`(둘 다 `<Import Project="$(MSBuildThisFileDirectory)../../build/BuildHostOutside.targets" />` 꼴), `deploy/package-files.txt`, `deploy/field-kit-files.txt`, `deploy/build-package.ps1`, `.github/workflows/ci.yml`(publish-check·package 잡), `src/PellyCoder.Core/Doctor/OfflineSelfTest.cs`, `tests/PellyCoder.Integration.Tests/Package/PackageTests.cs`, `deploy/install.md`(zip 안 폴더 둘 설명)
- Test: `tests/PellyCoder.Core.Tests/Doctor/OfflineSelfTestTests.cs`(있으면 그 파일), `PackageTests`

**Interfaces:**
- Produces:
  - 목록 문법 확장: `BuildHost-netcore/** <- publish:BuildHost-netcore/**`(그 폴더 아래 모든 파일을 같은 상대 경로로 넣는다)
  - `OfflineSelfTest` 새 행 `의미 분석 보조 프로세스`: `AppContext.BaseDirectory` 옆에 두 폴더가 있고 각 폴더에 파일이 하나 이상 있으면 Ok, 아니면 Fail(조치 「반입 zip을 다시 풀어 pelly.exe 옆에 BuildHost-net472, BuildHost-netcore 폴더를 두세요」).
- Consumes: 없음(Task 2의 패키지 참조가 BuildHost 폴더를 출력에 만든다).

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `OfflineSelfTestTests.BuildHost_row_fails_when_a_folder_is_missing`: 행 검사를 `baseDir` 매개변수로 부를 수 있게 한다(`internal static string CheckBuildHost(string baseDir)`). 빈 임시 폴더 → 예외(Fail), 두 폴더에 파일 하나씩 → 성공 문자열.
  - `PackageTests.Package_ContainsExactlyAllowedFiles`(기존)는 목록의 `/**` 항목을 publish 폴더의 실제 파일로 펼쳐 대조하도록 고친다. `Package_HasBuildHostFolders`를 새로 둔다. zip 안 `BuildHost-net472/`·`BuildHost-netcore/`에 각각 `.exe` 또는 `.dll`이 하나 이상 있어야 한다.
- [ ] **Step 2: 실행해 실패를 본다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~OfflineSelfTest"` → FAIL
- [ ] **Step 3: 구현한다**
  - targets 파일로 두 csproj의 단일 파일 publish가 폴더 둘을 exe 옆에 낸다.
  - `build-package.ps1`: 줄 패턴에 `\A(\S+)/\*\* <- publish:(\S+)/\*\*\z`를 더한다. 펼친 항목이 `$entries`에 들어가 기존 「staged == expected」 대조와 SHA256SUMS를 그대로 탄다. 출처 폴더가 없거나 비었으면 throw한다.
  - CI:
    - publish-check: exe와 두 폴더를 함께 아티팩트로 올린다. `doctor --offline-selftest`가 새 행까지 통과해야 한다.
    - 같은 잡에 폴더를 지운 사본으로 `--offline-selftest`가 종료 4인지 보는 단계를 더한다(Review Focus 2의 배포 쪽).
    - package 잡은 폴더째 내려받는다. eval publish도 같다.
  - `package-files.txt`와 `field-kit-files.txt`에 `BuildHost-net472/**`, `BuildHost-netcore/**` 두 줄씩 더한다.
- [ ] **Step 4: 실행해 통과를 본다** — 로컬 `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~OfflineSelfTest"` PASS. PackageTests는 CI package 잡에서 돈다. 로컬에서는 `dotnet publish src/PellyCoder.Cli -c Release -r win-x64 -o <임시>`로 폴더 둘과 exe 하나가 나오는지 목록으로 확인한다.
- [ ] **Step 5: 커밋** — `build: ship the Roslyn build hosts next to the single-file exe (pelly and the field kit)`

### Task 6: `pelly doctor`의 의미 분석 행

**Files:**
- Create: `src/PellyCoder.Core/Doctor/SemanticCheck.cs`
- Modify: `src/PellyCoder.Core/Doctor/DoctorService.cs`(생성자 끝에 `ISemanticLoader? semantic = null`, 결과에 행 추가), `DoctorFacts.cs`, `DoctorSanitizer.cs`(허용 목록), `src/PellyCoder.Cli/Commands/DoctorCommand.cs`(조립), `README.md`·`deploy/usage.md`(doctor 절)
- Test: `tests/PellyCoder.Core.Tests/Doctor/SemanticCheckTests.cs`, `DoctorSanitizerTests`(있는 파일)

**Interfaces:**
- Produces: `public static class SemanticCheck { public const string Name = "의미 분석"; public static Task<DoctorCheck> RunAsync(IFileSystem fs, ISemanticLoader loader, string baseDir, string? projectPath, CancellationToken ct); }`
  - Facts 키: `buildHost`(`present`|`missing`), `semantic`(`ok`|`fallback`|`notChecked`), `failure`(`SemanticFailureKind` 이름, fallback일 때만)
- Consumes: Task 2 `ISemanticLoader`·`SemanticFailure`, Task 5 `CheckBuildHost` 규칙(같은 함수를 쓴다).

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `SemanticCheckTests.Missing_build_host_is_Fail`: 행은 Fail이고 조치 문구는 Task 5와 같다. 로더는 부르지 않는다.
  - `SemanticCheckTests.No_project_is_Ok_not_checked`: Ok, 상세 `보조 프로세스 있음 (--project를 주면 불러오기까지 확인합니다)`, `semantic=notChecked`
  - `SemanticCheckTests.Healthy_project_is_Ok`: 가짜 로더 실패 없음 → Ok, `semantic=ok`
  - `SemanticCheckTests.Failure_is_Warn_with_fallback_message`: `CompileErrors(3)` → Warn, 상세 `이름 기준으로 대신합니다: 컴파일 오류 3건`, `failure=CompileErrors`
  - `DoctorSanitizerTests.Semantic_row_keeps_only_whitelisted_facts`: 경로·메시지는 비식별 출력에 나오지 않는다.
- [ ] **Step 2: 실행해 실패를 본다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~SemanticCheck|FullyQualifiedName~DoctorSanitizer"` → FAIL
- [ ] **Step 3: 구현한다** — `DoctorService`는 `semantic`이 주어졌을 때만 행을 더한다(기존 테스트 보존). `DoctorCommand`는 `new MsBuildSemanticLoader(fs)`와 `AppContext.BaseDirectory`, `--project`를 넘긴다. Warn은 doctor 종료 코드를 바꾸지 않는다(기존 Warn 규칙).
- [ ] **Step 4: 실행해 통과를 본다** — 같은 필터와 `dotnet test tests/PellyCoder.Cli.Tests` → PASS
- [ ] **Step 5: 커밋** — `feat(doctor): report whether semantic analysis works`

### Task 7: 명세 완료율과 그룹 빌드 통과율(`pelly report`)

**Files:**
- Modify: `src/PellyCoder.Core/Specs/ApplySummary.cs`, `src/PellyCoder.Core/Specs/SpecRunner.cs`(새 필드 채우기), `src/PellyCoder.Core/Reporting/ReportSanitizer.cs`, `src/PellyCoder.Cli/Commands/ReportCommand.cs`, `README.md`·`deploy/usage.md`(report 절)
- Create: `src/PellyCoder.Core/Reporting/ApplyHistory.cs`, `src/PellyCoder.Core/Reporting/ApplyReport.cs`
- Test: `tests/PellyCoder.Core.Tests/Reporting/ApplyHistoryTests.cs`, `ApplyReportTests.cs`, `ReportSanitizerTests`(있는 파일), `tests/PellyCoder.Cli.Tests/ReportCommandTests.cs`(있는 파일)

**Interfaces:**
- Produces:
  ```csharp
  // ApplyTaskResult에 string? Group = null 추가(그룹 작업만), ApplySummary에 int TaskCount = 0, bool Resumed = false 추가(옛 apply.json도 읽힌다)
  public sealed record ApplyRecord(ApplySummary Summary, DateTimeOffset StartedUtc);
  public static class ApplyHistory { public static (IReadOnlyList<ApplyRecord> Applies, int Unreadable) Load(IFileSystem fs, Workspace ws, DateOnly? since); } // .pelly/runs/apply-*/apply.json, 시각은 "apply-" 뒤 RunId 형식
  public sealed record Ratio(int Ok, int Total) { public double? Rate { get; } }
  public sealed record ApplyReportData(Ratio Completion, Ratio Completion3To8, Ratio GroupBuild, int GroupsUnverifiable, int Resumed);
  public static class ApplyReport { public static ApplyReportData Build(IReadOnlyList<ApplyRecord> applies); }
  ```
- Consumes: V2-1a `ApplySummary`, Task 4의 `Impact` 필드(이미 있음).

- [ ] **Step 1: 실패하는 테스트를 쓴다** (판정 규칙 = Claude 결정, 테스트로 고정)
  - `ApplyReportTests.Completion_counts_fresh_applies_that_archived_with_exit_0`: 다음 입력으로 `Completion`은 1/2다. `--resume` apply는 분모에서 빼고 `Resumed=1`로 따로 센다.
    - `Resumed=false, ExitCode=0, Archived=true`
    - `Resumed=false, ExitCode=1`
    - `Resumed=true, ExitCode=0, Archived=true`
  - `ApplyReportTests.Completion_3_to_8_uses_TaskCount`: `TaskCount` 2·3·8·9 중 3과 8만 분모다.
  - `ApplyReportTests.Group_build_rate_counts_groups_that_reached_the_build`: 그룹(ApplyId, Group)마다 다음과 같이 센다. 결과 `GroupBuild` 1/3, `GroupsUnverifiable` 1이다.
    - 모든 작업 종료 0 → 통과
    - 작업 하나라도 `GroupBuildFailed`·`GroupMissingTarget` → 실패
    - `GroupUnverifiable` → 따로 센다
    - 그 밖(빌드 전 FSM 실패, `GroupRejected`) → 제외
    - 입력: 통과 1, `GroupBuildFailed` 1, `GroupMissingTarget` 1, `GroupUnverifiable` 1, `GroupRejected` 1
  - `ApplyHistoryTests.Reads_apply_json_and_skips_summary_dirs`, `Old_apply_json_without_new_fields_reads_with_defaults`, `Unreadable_is_counted`
  - `ReportCommandTests.Shows_spec_completion_even_without_task_runs`: `summary.json` 없이 `apply.json`만 있어도 「명세 완료율」 표가 나온다.
  - `ReportSanitizerTests.Apply_aggregate_has_counts_only`: 비식별 출력에 명세 ID·경로가 없고 숫자 여섯만 있다. 숫자는 완료·분모·3~8 완료·3~8 분모·그룹 통과·그룹 분모다.
  - `SpecRunner` 쪽(기존 `SpecRunnerGroupTests` 옆 새 테스트): 그룹 작업 결과의 `Group`, `TaskCount`(명세의 모든 작업 수), `Resumed`(`--resume`)가 `apply.json`에 써진다.
- [ ] **Step 2: 실행해 실패를 본다** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~ApplyReport|FullyQualifiedName~ApplyHistory"` → 컴파일 실패
- [ ] **Step 3: 구현한다**
  - `ReportCommand`는 다음 순서로 동작한다.
    1. 작업 실행 기록이 없어도 apply 기록이 있으면 계속한다.
    2. 지금 표 뒤에 표 「명세」를 낸다. 행은 다음과 같다.
       - `명세 완료율` `{rate:P1} ({ok}/{total}건, 이어서 하기 {n}건 제외)`
       - `명세 완료율(작업 3~8개)` `{rate:P1} ({ok}/{total}건)`
       - `그룹 빌드 통과율` `{rate:P1} ({ok}/{total}그룹, 검증 불가 {n}그룹 제외)`
       - 분모가 0이면 `-`다.
  - `--since`는 apply에도 같은 규칙으로 적용한다.
  - 비식별 보고는 `SanitizedAggregate`에 `ApplyCounts` 레코드(int 여섯)를 더한다.
- [ ] **Step 4: 실행해 통과를 본다** — 같은 필터와 `dotnet test tests/PellyCoder.Cli.Tests` → PASS
- [ ] **Step 5: 커밋** — `feat(report): spec completion rate and group build pass rate from apply records`

### Task 8: `pelly-eval apply` — S6 정답 E2E 벤치마크

**Files:**
- Create: `eval/benchmark/apply/<id>.md` 12건. S6 `cases/<id>/gold.md`에서 만든다.
  - 머리말은 `id`, `status: draft`, `scope`(그대로)다. 파서는 모르는 키를 무시한다.
  - 본문은 `# <request>`, `## 작업`, 작업 줄 그대로다.
- Create: `eval/benchmark/apply/README.md`(출처·변환 규칙), `eval/PellyCoder.Eval/ApplyBenchmark.cs`(실행), `eval/PellyCoder.Eval/ApplyBenchmarkReport.cs`(결과·요약)
- Modify: `eval/PellyCoder.Eval/EvalArguments.cs`(명령 `apply`: `--cases <glob>`, `--repeat <n>`, `--out <dir>`, `--config <file>`), `eval/PellyCoder.Eval/Program.cs`, `eval/benchmark/README.md`(apply 절), `docs/PRD.md`(§12.3 상태 줄, 변경 이력 v1.16 「V2-1c 구현: 영향 탐색(심볼·이름 대체 표시), BuildHost 배포, doctor 의미 분석 행, 명세 완료율 report, apply E2E 벤치마크」), 설계서 머리 상태 줄
- Test: `tests/PellyCoder.Eval.Tests/ApplyCasesTests.cs`, `tests/PellyCoder.Eval.Tests/ApplyBenchmarkTests.cs`(가짜 실행기), `EvalArgumentsTests`(있는 파일)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record ApplyCase(string Id, IReadOnlyList<string> Scope, string SpecText, int TaskCount, bool HasSql, bool HasGroup);
  public sealed record ApplyCaseResult(string Id, int Repeat, string Outcome /* completed|failed|skipped|error */, string? SkipReason,
      int ExitCode, bool Archived, int TaskCount, IReadOnlyList<ApplyTaskResult> Tasks, bool? IndependentBuildOk, double Seconds);
  public static class ApplyBenchmark
  {
      public static IReadOnlyList<ApplyCase> LoadCases(string dir, string? glob);
      public static Task<ApplyCaseResult> RunCaseAsync(ApplyCase c, int repeat, ApplyBenchSetup setup, string outDir, CancellationToken ct);
  }
  ```
  `ApplyBenchSetup`은 `fs`·`options`·`processes`·`time`·`isWindows`·`sqlAvailable`·`Func<PellyOptions, Func<RunRequest, CancellationToken, Task<RunResult>>> runFactory`·`IGroupVerifier`(독립 검증용)를 담는다.
- Consumes: Task 4 `SpecRunnerFactory.Create`, Task 7 `ApplySummary` 필드, 기존 `WorkCopy.CopyDirectory`·`SolutionRestore.Spec`·`EvalUserInteraction`, V2-1b `ProjectReferenceGraph.AffectedProjects`·`GroupVerifier`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `ApplyCasesTests.Cases_match_the_approved_S6_gold`: `eval/benchmark/apply/*.md` 12건이 각각 다음을 만족한다.
    - S6 `spikes/S6-planning/cases/<id>/gold.md`와 작업 줄이 글자 그대로 같다.
    - S6 `APPROVED.json`이 있다.
    - `ChangeSpecParser.Parse` 이슈가 0이다.
  - `ApplyBenchmarkTests.Legacy_case_is_skipped_off_windows`: `isWindows=false`이고 범위에 Legacy가 있으면 `skipped`, 사유는 `Windows 전용`이다.
  - `ApplyBenchmarkTests.Sql_case_is_skipped_without_connection`: 사유는 `SQL 연결 없음`이다.
  - `ApplyBenchmarkTests.Work_copy_is_one_workspace_with_a_git_marker`: 가짜 실행기로 돌리면 다음이 맞다.
    - 작업 복사본 `<out>/work/<id>-<n>/corpus/`에 빈 `.git` 폴더와 `changes/<id>.md`가 있다.
    - `WorkspaceLocator.Resolve(명세 경로).Root`가 그 `corpus` 폴더다.
  - `ApplyBenchmarkTests.Exit_0_with_independent_build_errors_is_an_invariant_violation`: 가짜 실행기가 종료 0을 주는데 독립 빌드에 컴파일러 오류가 있으면 `IndependentBuildOk=false`이고 명령 종료 코드는 1이다.
  - `ApplyBenchmarkTests.Summary_splits_3_to_8_tasks_and_reports_skips`: 요약 md에 다음이 모두 있다.
    - 줄 `명세 완료율`, `명세 완료율(작업 3~8개)`, `그룹 빌드 통과율`
    - 건너뛴 사례 목록
    - 계산은 Task 7 `ApplyReport.Build`를 그대로 쓴다. 각 사례의 `apply.json`을 `ApplyRecord`로 읽는다.
  - `EvalArgumentsTests.Apply_command_parses_its_options`
- [ ] **Step 2: 실행해 실패를 본다** — `dotnet test tests/PellyCoder.Eval.Tests --filter "FullyQualifiedName~Apply"` → 컴파일 실패
- [ ] **Step 3: 구현한다** — 사례마다·반복마다 다음 순서로 돈다.
  1. 건너뛰기 판정(Windows 전용, SQL 연결 없음)
  2. `eval/corpus` 복사(`WorkCopy.CopyDirectory`, 기본 제외)와 `supportFiles`(`global.json` 등, 스위트와 같음) 복사, 빈 `.git` 폴더 만들기, `changes/<id>.md` 쓰기
  3. 범위에 Modern이 있으면 `SolutionRestore.Spec(Modern.sln)`으로 복원한다. Legacy는 기존 run과 같은 전제(Windows, 패키지 폴더 준비)를 쓴다.
  4. `SpecRunnerFactory.Create(..., impact: ImpactFinders.Default(fs))`와 `ApplyOptions(Continue: false, Resume: false, ReviewAtEnd: false, Yes: true, DryRun: false, MaxRetries: null, Verbose: false)`로 실행한다.
  5. 종료 0이면 바뀐 `.cs` 파일의 프로젝트와 그 의존자(`AffectedProjects`)를 `IGroupVerifier.BuildAsync`로 빌드한다.
     - 오류 0이면 `IndependentBuildOk=true`, 오류가 있으면 false(불변 조건 위반)다.
     - 검증 불가면 null이고 요약에 「독립 검증 불가」로 적는다.
  6. `<out>/apply-results.json`(모든 결과)과 `<out>/apply-summary.md`를 쓴다.
  - 실행 전에 LLM 서버 확인과 `--out` 충돌 확인을 `run`과 같은 코드로 한다.
- [ ] **Step 4: 실행해 통과를 본다** — `dotnet test tests/PellyCoder.Eval.Tests` → PASS, `dotnet build -warnaserror`, 전체 `dotnet test` 종료 0
- [ ] **Step 5: 커밋** — `feat(eval): pelly-eval apply runs the S6 gold specs end to end and reports spec completion`

---

## 이 계획 밖(설계 §8, 사람·현장 일)

- 현장 모델(qwen3.6:35b GGUF)로 `pelly-eval apply --repeat N` 실측, 레거시 그룹 사례(a3)의 Windows 실측, 반입본 의미 분석 현장 확인(S8 현장 측정과 함께)은 사람이 한다. 결과는 `deploy/field-results.md`에 옮긴다.
