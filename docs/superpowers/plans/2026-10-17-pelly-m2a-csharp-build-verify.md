# Pelly-Coder M2a (C# 빌드 검증·대상 탐색) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `pelly cs`가 SDK·레거시 프로젝트를 실제로 빌드해 "새로 생긴 오류"만으로 검증하고(Baseline은 모델 생성과 병렬), 시그니처 변경을 구조적으로 막고, `--line`·요청문만으로 대상 멤버를 찾게 하며, M1 최종 리뷰가 권한 이월 항목을 닫는다.

**Architecture:** 외부 프로세스는 새 `IProcessRunner` 뒤로 숨기고, `BuildToolResolver`(SDK→`dotnet`, 레거시→`MSBuild.exe`/vswhere) → `BuildVerifier`(빌드·파싱·Baseline 차감)를 만든다. VERIFY는 `IVerifierStep` 파이프라인(구문 → 빌드)이 되고, Baseline 빌드는 LOCATE 끝에서 시작해 APPLY가 기다린다. APPLY는 쓰기 전에 `MemberShapeGuard`로 멤버 모양을 검사한다. LOCATE는 `--line`·식별자 추출·모델 선택을 지원한다. SQL 경로와 벤치마크 실행기는 M2b.

**Tech Stack:** .NET 10, Roslyn 5.9, System.Diagnostics.Process, MSBuild/vswhere(Windows CI), xUnit v3(MTP), Spectre.Console.Cli, Ollama 0.40.0

**Spec:** `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md` — D7, D8, D14, D18(제외), D19, §7.1, §7.3, §8.4, §8.5(언어 버전), §8.7.1, §8.9, §8.13, §8.15, §8.16, §11 M2 행. PRD v1.6 FR-00·FR-01·FR-06. M1 이월 근거: M1 원장 사본 `/private/tmp/claude-501/-Users-payletter-git-root-ReSet/80aff902-238e-4e70-8e26-123252df0fa0/scratchpad/m1-ledger.md`의 `Final:` 줄.

**범위 밖 (이 계획에서 하지 않음):** SQL 경로 전체(`pelly sql`, SqlSafetyGuard, SqlSyntaxVerifier, SqlSchemaSlicer) · 벤치마크 실행기와 정의 30건 → **M2b**. `--test`(D18), DTO 슬라이스, `report --sanitize`, `--pause-on-exit`, 형상 관리 경고 → **M3**.

## Global Constraints

* M1 계획(`docs/superpowers/plans/2026-10-17-pelly-m1-csharp-e2e.md`)의 Global Constraints를 그대로 이어받는다: SDK `10.0.302`, `net10.0`, Nullable·ImplicitUsings·TreatWarningsAsErrors, CPM(간접 고정 켜짐, Pkcs 10.0.13), MIT/Apache-2.0만, Core에서 `Console.*` 금지(D20), D21~D23, 예산·샘플링 기본값, 종료 코드 0~5, 환경 테스트는 Integration.Tests의 `[WindowsFact]`/`[SqlServerFact]`/`[OllamaFact]`, 식별자·커밋 영어 / 문서 한국어, 커밋 끝 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
* **네임스페이스 규칙:** 네임스페이스 이름을 그 트리 안의 타입 이름과 같게 짓지 않는다(M1에서 `Workspace`·`Feedback` 충돌 두 번). 새 네임스페이스: `PellyCoder.Core.Processes`, `PellyCoder.Core.Builds`, `PellyCoder.Core.Verification`.
* **외부 프로세스:** `Process`는 `PellyCoder.Core.Processes.SystemProcessRunner` 안에서만 쓴다. 나머지는 `IProcessRunner`. 인자는 항상 `ArgumentList`. 취소·시간 초과 시 프로세스 트리를 종료한다.
* **빌드 명령(설계 §8.9, 그대로):**
  * SDK: `dotnet build <csproj> --no-restore --nologo -v q -clp:NoSummary;ErrorsOnly` (`warnAsError`면 `ErrorsOnly` 대신 `NoSummary`만)
  * 레거시: `MSBuild.exe <csproj> /t:Build /nologo /v:q /clp:NoSummary;ErrorsOnly /nr:true /m`
  * 환경 변수 `DOTNET_CLI_UI_LANGUAGE=en`, `VSLANG=1033`
  * vswhere: `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe` 결과 첫 줄
  * 빌드 오류 정규식(설계 §8.9): `^\s*(?<file>.+?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Z]{2,}\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$`
* **새 설정 기본값:** `build.msbuildPath = null`, `build.timeoutSeconds = 120`, `build.warnAsError = false`, `build.strictBaseline = false`.
* **Windows에서만 도는 경로**(vswhere·MSBuild·레거시 빌드)는 `[WindowsFact]` 통합 테스트로 CI `windows-integration` 잡에서 검증한다. 실기 Windows에서 확인하지 못한 동작은 코드 주석과 문서에 「Windows 미확인」으로 남긴다.
* 코퍼스(`eval/corpus`)는 읽기 전용. 빌드 테스트는 임시 폴더 복사본에서만.
* 각 작업 완료 전 브랜치를 push해 CI 4잡 녹색을 확인한다(M1에서 Windows 전용 실패를 늦게 발견함).

## Review Focus

1. **원래부터 빌드가 깨져 있는 프로젝트** — 수정은 여전히 가능해야 하고, 원래 있던 오류는 세지 않으며, 사용자에게 한 번 알린다(`--strict-baseline`이면 종료 4). → Task 6 `BaselineHasErrors_EditStillSucceeds_WarnsOnce`.
2. **빌드가 멈추거나 매우 느림, 또는 dotnet/MSBuild가 없음** — 종료 4, 원본 복원, 남은 빌드 프로세스 없음. → Task 1 `Timeout_KillsProcessTree`, Task 6 `BuildTimeout_Exit4_Restores_NoOrphan`.
3. **Baseline 빌드 도중(파일을 쓰기 전) Ctrl+C** — 빌드 프로세스 종료, 종료 2, 파일 바이트 그대로. → Task 6 `CancelDuringBaseline_KillsBuild_FileUntouched`.
4. **모델이 빌드를 통과시키려고 메서드 시그니처를 바꾸거나 다른 멤버를 지움** — 파일에 쓰지 않고 SHAPE REJECTED 피드백으로 재시도. → Task 7 `SignatureChange_IsShapeRejected_NeverWritten`.
5. **`--line`이 필드·빈 줄·클래스 선언 줄을 가리키거나, 같은 이름 오버로드가 붙어 있음** — 종료 3과 "메서드 안에 커서를 두고 실행하세요", 오버로드는 줄이 속한 바로 그 멤버. → Task 8 `Line_OnField_IsNotFound_WithCursorHint`, `Line_PicksOverloadContainingLine`.

---

## 파일 구조 (신규·변경)

```text
src/PellyCoder.Core/
├── Abstractions/IProcessRunner.cs          # 신규: ProcessSpec, ProcessResult, IProcessRunner
├── Processes/SystemProcessRunner.cs        # 신규
├── Builds/
│   ├── ProjectStyleDetector.cs             # 신규 (LocatePhase.StyleOf 대체)
│   ├── BuildToolResolver.cs                # 신규
│   ├── BuildErrorParser.cs                 # 신규
│   ├── BaselineSnapshot.cs                 # 신규
│   └── BuildVerifier.cs                    # 신규
├── Verification/
│   ├── IVerifierStep.cs                    # 신규
│   ├── SyntaxStep.cs                       # 신규 (VerifyPhase의 현재 로직 이동)
│   └── BuildStep.cs                        # 신규
├── CSharp/
│   ├── LanguageVersionDetector.cs          # 변경: Directory.Build.props, Condition
│   ├── MemberLocator.cs                    # 변경: FindAtLine, AllMembers
│   ├── IdentifierExtractor.cs              # 신규
│   └── MemberShapeGuard.cs                 # 신규
├── Llm/
│   ├── ModelMemberPicker.cs                # 신규
│   ├── SpecialTokenGuard.cs                # 신규
│   └── Resources/prompts/locate.txt        # 신규; system.cs.txt·fewshot.cs.txt 변경(version 2)
├── Config/PellyOptions.cs                  # 변경: BuildOptions
├── Fsm/…                                   # 변경: RunRequest, RunContext, Phases, Factory, Orchestrator
├── Feedback/FeedbackBuilder.cs             # 변경: ForBuild, ForShapeRejection, 긴 줄 자르기
├── Files/FileGuard.cs, FileSnapshot.cs     # 변경: 심볼릭 링크·파일 모드, MixedNewLines
└── Doctor/DoctorService.cs                 # 변경: 빌드 도구 점검
src/PellyCoder.Cli/                          # 변경: CsCommand, CliApp, SpectreUserInteraction, Composition
tests/…                                      # 각 구성 요소 테스트, Integration.Tests/Builds/*, E2E 확장
.github/workflows/ci.yml                     # 변경: Legacy 복원을 Windows 테스트 앞으로
```

---

### Task 1: 프로세스 실행기

**Files:**
- Create: `src/PellyCoder.Core/Abstractions/IProcessRunner.cs`, `src/PellyCoder.Core/Processes/SystemProcessRunner.cs`
- Test: `tests/PellyCoder.Core.Tests/Processes/SystemProcessRunnerTests.cs`, `tests/PellyCoder.Core.Tests/Fakes/ScriptedProcessRunner.cs`

**Interfaces:**
- Produces (설계 §5 + 환경 변수·원본 바이트):
  * `sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout, IReadOnlyDictionary<string, string>? Environment = null)`
  * `sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, byte[] StdOutBytes, TimeSpan Duration, bool TimedOut)` — `StdOut`은 UTF-8 디코딩본, `StdOutBytes`는 원본 바이트(CP949 재디코딩용). 시간 초과면 `ExitCode = -1`, `TimedOut = true`.
  * `interface IProcessRunner { Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct); }` — 취소 시 프로세스 트리를 종료한 뒤 `OperationCanceledException`. 실행 파일을 찾지 못하면 `ProcessStartException(string fileName, Exception inner)`(Abstractions 네임스페이스).
  * `sealed class SystemProcessRunner(TimeProvider time) : IProcessRunner` — `UseShellExecute=false`, stdout/stderr 비동기 동시 읽기(교착 방지), `Kill(entireProcessTree: true)`.
  * 테스트용 `ScriptedProcessRunner`: 받은 `ProcessSpec`을 `Calls`에 기록, `Enqueue(ProcessResult)` / `EnqueueDelay(TimeSpan, ProcessResult)`(취소 토큰을 존중) / `EnqueueThrow(Exception)`. 비어 있으면 `InvalidOperationException`. 이후 작업이 재사용한다.

- [ ] **Step 1: 실패하는 테스트 작성** (실제 프로세스. Unix는 `/bin/sh -c`, Windows는 `cmd /c` — 테스트 안에서 `OperatingSystem.IsWindows()`로 분기)
  ```csharp
  [Fact] RunsDotnetVersion_CapturesStdOut()              // FileName "dotnet", ["--version"] → ExitCode 0, StdOut matches ^\d+\.\d+
  [Fact] NonZeroExit_IsReported()                        // sh -c "exit 3" / cmd /c "exit 3" → ExitCode 3, TimedOut false
  [Fact] Timeout_KillsProcessTree()                      // 부모가 자식을 띄우고 30초 대기하는 셸 명령, Timeout 1초 → TimedOut, Duration < 10초, 이후 자식 PID도 종료됨(Unix: kill -0 실패 / Windows: Process.GetProcessById 예외)
  [Fact] Cancellation_KillsAndThrows()                   // 30초 대기 명령, 300ms 뒤 취소 → OperationCanceledException, Duration < 10초
  [Fact] ArgumentsWithSpacesAndKorean_RoundTrip()        // 인자 "공백 있는 인자 [x]"를 그대로 출력 → StdOut.Trim() 일치 (Unix: sh -c 'printf "%s" "$1"' _ <arg>, Windows: powershell -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; [Console]::Out.Write($args[0])" <arg>)
  [Fact] EnvironmentVariables_ArePassed()                // PELLY_PROBE=값 → 출력에 값
  [Fact] MissingExecutable_ThrowsProcessStartException()
  [Fact] LargeOutputOnBothStreams_DoesNotDeadlock()      // stdout·stderr 각 1MB → 10초 안에 완료
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Processes*"` / Expected: 컴파일 실패
- [ ] **Step 3: 구현** — 시간 초과는 주입한 `TimeProvider`로 만든 `CancellationTokenSource`와 호출자 토큰을 연결. 호출자 취소와 시간 초과를 구분해 결과/예외로 나눈다.
- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 8개 통과. push 후 CI `unit (windows-latest)` 녹색
- [ ] **Step 5: Commit** — `git commit -m "feat(core): add process runner with tree kill and timeouts"`

---

### Task 2: 프로젝트 형식·빌드 도구 선택

**Files:**
- Create: `src/PellyCoder.Core/Builds/ProjectStyleDetector.cs`, `src/PellyCoder.Core/Builds/BuildToolResolver.cs`
- Modify: `src/PellyCoder.Core/Config/PellyOptions.cs` (BuildOptions 추가), `src/PellyCoder.Core/Fsm/Phases/LocatePhase.cs` (`StyleOf` 제거 → `ProjectStyleDetector`)
- Test: `tests/PellyCoder.Core.Tests/Builds/ProjectStyleDetectorTests.cs`, `BuildToolResolverTests.cs`, `tests/PellyCoder.Integration.Tests/Builds/BuildToolResolverWindowsTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `ScriptedProcessRunner` (Task 1)
- Produces:
  * `sealed class BuildOptions { string? MsbuildPath; int TimeoutSeconds = 120; bool WarnAsError; bool StrictBaseline; }`; `PellyOptions.Build` 속성. 설정 키 `build:msbuildPath`, `build:timeoutSeconds`, `build:warnAsError`, `build:strictBaseline`.
  * `enum ProjectStyle { None, Sdk, Legacy }`; `static class ProjectStyleDetector { ProjectStyle Detect(IFileSystem fs, string? csprojPath); }` — 루트에 `Sdk` 속성 또는 `<Sdk>` 자식 → Sdk, 아니면 Legacy, csproj 없음 → None. XML 오류는 호출자에게 그대로 던진다(LocatePhase가 이미 EnvError로 처리).
  * `enum BuildToolKind { Dotnet, MsBuild }`; `sealed record BuildTool(BuildToolKind Kind, string Executable)`; `sealed record BuildToolResolution(BuildTool? Tool, string? Error)`
  * `sealed class BuildToolResolver(IFileSystem fs, IProcessRunner processes, BuildOptions options, Func<bool>? isWindows = null)`: `Task<BuildToolResolution> ResolveAsync(ProjectStyle style, CancellationToken ct)` — Sdk → `BuildTool(Dotnet, "dotnet")`; Legacy: Windows가 아니면 Error `"레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증할 수 있습니다"`; `MsbuildPath`가 있고 파일이 있으면 그것; 없으면 vswhere(Global Constraints 명령, 시간 초과 10초) 첫 줄이 존재하는 파일이면 그것; 모두 실패 → Error `"MSBuild.exe를 찾을 수 없습니다. Visual Studio Build Tools를 설치하거나 build.msbuildPath를 설정하세요 (pelly doctor)"`. None → `ArgumentException`.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // ProjectStyleDetectorTests (실제 코퍼스 + MockFileSystem)
  [Fact] ModernCorpus_IsSdk()  [Fact] LegacyCorpus_IsLegacy()  [Fact] SdkElementChild_IsSdk()  [Fact] NoCsproj_IsNone()
  // BuildToolResolverTests (MockFileSystem + ScriptedProcessRunner, isWindows 주입)
  [Fact] Sdk_UsesDotnet_WithoutRunningAnything()           // Calls.Count == 0
  [Fact] Legacy_OnNonWindows_IsErrorWithWindowsMessage()
  [Fact] Legacy_ConfiguredMsbuildPath_Wins()               // vswhere 호출 없음
  [Fact] Legacy_VswhereFirstLine_IsUsed()                  // Calls[0].Arguments == ["-latest","-products","*","-requires","Microsoft.Component.MSBuild","-find",@"MSBuild\**\Bin\MSBuild.exe"]
  [Fact] Legacy_VswhereMissingOrEmpty_IsErrorWithDoctorHint()
  [Fact] Legacy_ConfiguredPathMissing_FallsBackToVswhere()
  // BuildToolResolverWindowsTests
  [WindowsFact] RealVswhere_FindsMsBuild()                 // windows-latest에는 VS가 있음 → Tool.Kind == MsBuild, File.Exists
  ```
  옵션 로더 테스트(`PellyOptionsLoaderTests.Defaults_MatchDesign`)에 Build 기본값 단언 추가.
- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Builds*"` / Expected: 컴파일 실패
- [ ] **Step 3: 구현** — `LocatePhase`는 `ctx.ProjectStyle = ProjectStyleDetector.Detect(...).ToString()`로 바꾼다(문자열 값 `Sdk`/`Legacy`/`None` 유지 — RunSummary 호환).
- [ ] **Step 4: 통과 확인** — 같은 명령 + `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Config*" --filter-namespace "*Fsm*"` / Expected: 전부 통과. push 후 `windows-integration`에서 `RealVswhere_FindsMsBuild` 통과(Skip 아님)
- [ ] **Step 5: Commit** — `git commit -m "feat(builds): add project style detector and build tool resolver"`

---

### Task 3: 언어 버전 — Directory.Build.props와 조건부 속성

**Files:**
- Modify: `src/PellyCoder.Core/CSharp/LanguageVersionDetector.cs`
- Test: `tests/PellyCoder.Core.Tests/CSharp/LanguageVersionDetectorTests.cs`

**Interfaces:**
- Consumes: 기존 `LanguageVersionDetector.Detect(IFileSystem fs, string? csprojPath) → LanguageTarget` (시그니처 유지)
- 규칙: csproj 폴더에서 위로 올라가며 **처음 만나는** `Directory.Build.props` 하나만 읽는다(MSBuild 의미와 같음). 그 파일의 `LangVersion`·`TargetFramework(s)`를 먼저 적용하고 csproj 값이 덮어쓴다. `Condition` 속성이 있는 `PropertyGroup`과 속성은 무시한다. `PropertyGroup` 밖의 같은 이름 요소도 무시한다. props XML이 깨져 있으면 `XmlException`을 던진다(호출자가 EnvError).

- [ ] **Step 1: 실패하는 테스트 작성** (MockFileSystem)
  ```csharp
  [Fact] DirectoryBuildProps_LangVersion_Applies()         // props <LangVersion>10</LangVersion>, csproj net48 → CSharp10, "C# 10, .NET Framework 4.8"
  [Fact] Csproj_Overrides_DirectoryBuildProps()            // props 10, csproj 9 → 9
  [Fact] NearestPropsOnly()                                // /r/Directory.Build.props(11), /r/src/Directory.Build.props(10) → 10
  [Fact] ConditionedPropertyGroup_IsIgnored()              // <PropertyGroup Condition="'$(Configuration)'=='Debug'"><LangVersion>12</LangVersion> → TFM 기본값
  [Fact] ConditionedProperty_IsIgnored()
  [Fact] PropertyOutsidePropertyGroup_IsIgnored()
  [Fact] LegacyCsproj_WithPropsLangVersion_Applies()        // 레거시 csproj + props LangVersion 8 → C# 8
  [Fact] MalformedProps_Throws()
  ```
- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*LanguageVersionDetectorTests"` / Expected: 새 테스트 실패
- [ ] **Step 3: 구현** — `LocatePhase`의 기존 `catch (XmlException or IOException ...)`가 props 오류도 잡는지 확인(메시지에 props 경로가 나오게 `Detect`가 예외 메시지에 경로를 담는다).
- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 기존 + 8개 통과
- [ ] **Step 5: Commit** — `git commit -m "feat(csharp): read Directory.Build.props and ignore conditioned properties"`

---

### Task 4: 빌드 출력 파서

**Files:**
- Create: `src/PellyCoder.Core/Builds/BuildErrorParser.cs`
- Create: `tests/fixtures/build/dotnet-sdk-errors.txt` (녹화), `tests/fixtures/build/msbuild-legacy-errors.txt` (형식 표본)
- Test: `tests/PellyCoder.Core.Tests/Builds/BuildErrorParserTests.cs`

**Interfaces:**
- Consumes: `Diagnostic` (Models)
- Produces: `static class BuildErrorParser { IReadOnlyList<Diagnostic> Parse(string output, string workspaceRoot, bool caseInsensitivePaths); }` — 오류와 경고 모두(`Severity` = `"error"`/`"warning"`). 파일 경로는 워크스페이스 루트 기준 상대 경로, 구분자 `/`. 루트 밖 경로는 절대 경로 그대로(구분자만 `/`). 파일 없는 줄(`MSBUILD : error MSB1009: …`, `CSC : error CS2001: …`)은 `File = ""`, `Line = 0`, `Column = 0`. 같은 (File, Line, Column, Code, Message)는 한 번만(대소문자 무시 옵션은 경로에만). 입력 순서 유지.

- [ ] **Step 1: 녹화 픽스처 만들기** — `eval/corpus/Modern`을 임시 폴더로 복사 → `dotnet restore` → `Shop.Api/Services/OrderService.cs`의 `CancelAsync` 본문에 `undefinedVar++;`와 `int x = "s";` 추가, 경로에 한글 폴더 하나를 끼워 넣은 사본도 만든다 → `DOTNET_CLI_UI_LANGUAGE=en dotnet build Shop.Api/Shop.Api.csproj --no-restore --nologo -v q -clp:NoSummary;ErrorsOnly`의 stdout을 `dotnet-sdk-errors.txt`로 저장(절대 경로 부분은 그대로 두고 테스트가 그 루트를 쓴다 — 녹화 시 루트를 파일 첫 줄 주석 `# root: <path>`로 남김). `msbuild-legacy-errors.txt`는 MSBuild 형식(설계 §8.9 정규식과 같음)으로 Windows 경로·`[project.csproj]` 접미사·중복 줄·`MSB` 코드·파일 없는 줄을 포함해 손으로 작성하고, 실제 MSBuild 출력 검증은 Task 5의 `[WindowsFact]`가 맡는다.
- [ ] **Step 2: 실패하는 테스트 작성**
  ```csharp
  [Fact] DotnetRecorded_ParsesCS0103AndCS0029_RelativePaths()   // File == "Shop.Api/Services/OrderService.cs", Line > 0
  [Fact] KoreanPath_Parses()
  [Fact] ProjectSuffix_IsStripped_FromMessage()
  [Fact] Duplicates_AreRemoved_OrderKept()
  [Fact] FilelessLines_HaveEmptyFileAndLineZero()
  [Fact] Warnings_HaveWarningSeverity()
  [Fact] WindowsPaths_CaseInsensitive_DedupeAndRelativize()      // "C:\W\Src\A.cs" 와 "c:\w\src\a.cs" → 한 개, root "C:\w" → "Src/A.cs"
  [Fact] PathOutsideRoot_StaysAbsolute()
  [Fact] UnrelatedLines_AreIgnored()
  ```
- [ ] **Step 3: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*BuildErrorParserTests"` / Expected: 컴파일 실패
- [ ] **Step 4: 구현** — Global Constraints의 정규식 그대로(`RegexOptions.Multiline` 대신 줄 단위 처리).
- [ ] **Step 5: 통과 확인** — 같은 명령 / Expected: 9개 통과
- [ ] **Step 6: Commit** — `git commit -m "feat(builds): add build output parser with recorded fixtures"`

---

### Task 5: Baseline과 빌드 검증기

**Files:**
- Create: `src/PellyCoder.Core/Builds/BaselineSnapshot.cs`, `src/PellyCoder.Core/Builds/BuildVerifier.cs`
- Test: `tests/PellyCoder.Core.Tests/Builds/BuildVerifierTests.cs`, `BaselineSnapshotTests.cs`
- Test: `tests/PellyCoder.Integration.Tests/Builds/SdkBuildTests.cs`, `LegacyBuildWindowsTests.cs`
- Modify: `.github/workflows/ci.yml` (`windows-integration`: "Legacy corpus build and tests" 단계를 "Windows tests" 앞으로 옮겨 `eval/corpus/Legacy/packages`가 테스트 전에 복원되게)

**Interfaces:**
- Consumes: `IProcessRunner`, `ProcessSpec/Result` (Task 1), `BuildTool`, `BuildToolKind`, `BuildOptions` (Task 2), `BuildErrorParser` (Task 4)
- Produces:
  * `sealed record BuildRun(IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> Warnings, int ExitCode, bool TimedOut, TimeSpan Duration)`
  * `sealed record BaselineSnapshot(BuildRun Run)`; `static class BaselineDiff { IReadOnlyList<Diagnostic> New(IReadOnlyList<Diagnostic> baseline, IReadOnlyList<Diagnostic> current); }` — 다중집합 차감, 키 (File 대소문자 무시, Code, Message), 줄 번호 무시, 결과는 `current` 순서.
  * `sealed class BuildVerifier(IProcessRunner processes, BuildOptions options)`: `Task<BuildRun> BuildAsync(BuildTool tool, string csprojPath, string workspaceRoot, CancellationToken ct)` — Global Constraints 명령·환경 변수, 작업 폴더 = csproj 폴더, 시간 초과 `options.TimeoutSeconds`. stdout+stderr를 합쳐 파싱. 디코딩: `StdOut`에 U+FFFD가 있고 `StdOutBytes`를 CP949(949)로 디코딩한 결과에 U+FFFD가 없으면 CP949 결과 사용. 경로 대소문자 무시 = `OperatingSystem.IsWindows()`.
  * 빌드가 끝났는데 오류 0개인데 종료 코드 ≠ 0이면 `Errors`에 `Diagnostic("", 0, 0, "error", "BUILD", "build failed with exit code N")` 하나를 넣는다(파싱 못 한 실패를 통과로 보지 않기 위함).

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // BuildVerifierTests (ScriptedProcessRunner)
  [Fact] Sdk_RunsDesignCommandWithEnglishEnv()       // Calls[0]: FileName "dotnet", Arguments == ["build", csproj, "--no-restore","--nologo","-v","q","-clp:NoSummary;ErrorsOnly"], Env DOTNET_CLI_UI_LANGUAGE=en, VSLANG=1033, WorkingDirectory = csproj 폴더
  [Fact] Sdk_WarnAsError_DropsErrorsOnly()           // "-clp:NoSummary"
  [Fact] Legacy_RunsMsBuildCommand()                 // [csproj,"/t:Build","/nologo","/v:q","/clp:NoSummary;ErrorsOnly","/nr:true","/m"]
  [Fact] Timeout_ReturnsTimedOut()                   // Timeout == options.TimeoutSeconds
  [Fact] Cp949Output_IsRedecoded()                   // StdOut에 U+FFFD, StdOutBytes는 CP949 한글 메시지 → 메시지에 한글
  [Fact] NonZeroExitWithoutParsedErrors_IsAFailure()
  // BaselineSnapshotTests
  [Fact] New_IsMultisetDifference_IgnoringLines()    // 원본 CS0103×1(줄 10), 현재 CS0103×2(줄 12,30) → 새 오류 1개
  [Fact] New_FileCaseInsensitive()
  // SdkBuildTests (환경 표시 없음 — dotnet은 모든 CI에 있음; 클래스 픽스처에서 Modern 코퍼스 복사 + dotnet restore 1회)
  [Fact] CleanBaseline_HasNoErrors()
  [Fact] InjectedUndefinedIdentifier_IsOneNewError_InTargetFile()   // File == "Shop.Api/Services/OrderService.cs", Code "CS0103"
  // LegacyBuildWindowsTests
  [WindowsFact] LegacyShopWeb_Baseline_Clean_ThenInjectedError_IsNew()  // Legacy 코퍼스(packages 포함) 복사 → BuildToolResolver로 MSBuild → CP949로 OrderService.cs에 오류 주입 → CS0103, 상대 경로
  ```
- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Builds*"` / Expected: 새 테스트 컴파일 실패
- [ ] **Step 3: 구현**
- [ ] **Step 4: 통과 확인** — `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Builds*"`와 `dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*Builds*"` / Expected: 전부 통과(Windows 테스트는 Mac에서 Skip). push 후 `windows-integration`에서 `LegacyShopWeb_…` 통과·Skip 아님, `unit` 두 잡에서 `SdkBuildTests` 통과
- [ ] **Step 5: Commit** — `git commit -m "feat(builds): add build verifier and baseline diff"`

---

### Task 6: 검증 파이프라인과 Baseline 병렬화 (FSM 통합)

**Files:**
- Create: `src/PellyCoder.Core/Verification/IVerifierStep.cs`, `SyntaxStep.cs`, `BuildStep.cs`
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs` (RunContext), `Phases/LocatePhase.cs`, `Phases/ApplyPhase.cs`, `Phases/VerifyPhase.cs`, `FsmOrchestrator.cs`, `FsmOrchestratorFactory.cs`, `src/PellyCoder.Core/Feedback/FeedbackBuilder.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/BuildVerificationFlowTests.cs`, `tests/PellyCoder.Core.Tests/Feedback/FeedbackBuilderBuildTests.cs`

**Interfaces:**
- Consumes: Task 1~5 전부; 기존 `SyntaxVerifier`, `SyntaxBaseline`, `FeedbackBuilder.ForDiagnostics`
- Produces:
  * `interface IVerifierStep { string Name { get; } Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct); }`; `sealed record StepResult(bool Passed, Feedback? Feedback, IReadOnlyList<Diagnostic> NewErrors, IReadOnlyList<Diagnostic> NewWarnings, bool EnvironmentFailure, string? Message)` — `EnvironmentFailure`면 VERIFY는 EnvError.
  * `SyntaxStep(IFileSystem fs, FeedbackBuilder feedback)` — 현재 `VerifyPhase` 로직을 옮긴 것.
  * `BuildStep(BuildVerifier builds, FeedbackBuilder feedback, BuildOptions options)` — `ctx.BuildTool`이 null이면 통과(빌드 대상 없음). 빌드 → `BaselineDiff.New(baseline.Errors, run.Errors)`. `WarnAsError`면 경고도 같은 방식으로 새 경고를 오류로 취급. 새 경고(WarnAsError가 아닐 때)는 `ctx.Messages`에 `"새 경고 N개: CSxxxx …"` 한 줄. `TimedOut` → `EnvironmentFailure`, 메시지 `"빌드가 {N}초 안에 끝나지 않았습니다 (build.timeoutSeconds)"`.
  * `VerifyPhase(IReadOnlyList<IVerifierStep> steps)` — 순서대로 실행, 첫 실패에서 멈춤. 실패 시 `ctx.Feedback = step.Feedback`, 코드들을 `ctx.ErrorCodes`에 추가 → NewErrors; 환경 실패 → EnvError.
  * `RunContext` 추가: `BuildTool? BuildTool`, `Task<BuildRun>? Baseline`, `CancellationTokenSource? BaselineCancellation`.
  * `FeedbackBuilder.ForBuild(IReadOnlyList<Diagnostic> newErrors, string targetRelativePath, string targetTextLf) → Feedback` — 머리말 `"BUILD FAILED (<n> new error)"`/`"(<n> new errors)"`; 대상 파일(`File` == targetRelativePath) 오류를 먼저 ±3줄 문맥과 함께(기존 형식), 다른 파일 오류는 한 줄씩 `"<File>(<Line>): <Code> <Message>"`; 예산 맞추기 순서는 기존 규칙(뒤 항목부터 제거 → 첫 항목 문맥 축소 → 메시지 자르기).
  * 흐름(설계 §7.3을 이렇게 구체화 — 설계 문서는 Task 15에서 갱신): **LOCATE 끝**에서 `ProjectStyle`이 Sdk/Legacy면 `BuildToolResolver.ResolveAsync` → 실패면 EnvError(메시지 그대로); 성공하면 `ctx.BaselineCancellation`(실행 토큰과 연결)과 `ctx.Baseline = builds.BuildAsync(...)`를 **시작만** 한다. `ProjectStyle == None`이면 빌드 검증 없이 진행하고 `ctx.Messages`에 `"csproj가 없어 빌드 검증을 건너뜁니다 (구문 검사만 합니다)"`.
  * **APPLY**: 파일을 쓰기 전에(`DryRun`이 아닐 때만) `await ctx.Baseline` — 예외·`TimedOut` → EnvError; `Errors.Count > 0`이면 `StrictBaseline`이면 EnvError(`"원본 상태에서 이미 빌드 오류가 N개 있습니다 (--strict-baseline)"`), 아니면 한 번만 `ctx.Messages`에 `"원본 상태에서 이미 빌드 오류가 N개 있습니다 — 새로 생긴 오류만 검사합니다"`.
  * **오케스트레이터 `finally`**(롤백 전): `ctx.BaselineCancellation?.Cancel()` 후 `ctx.Baseline`이 끝날 때까지 최대 10초 기다린다(예외 무시) — 남은 빌드 프로세스가 없게 한다.
  * `FsmOrchestratorFactory.Create`에 `IProcessRunner processes` 매개변수 추가(호출부: CLI `Runners.cs`, E2E 테스트). 다른 시그니처 유지.

- [ ] **Step 1: 실패하는 테스트 작성** (`ScriptedProcessRunner` + `ScriptedLlmBackend`, 임시 SDK 프로젝트 — 빌드 출력은 Task 4 픽스처 형식의 문자열을 스크립트로 준다)
  ```csharp
  [Fact] CleanBaseline_BuildErrorOnAttempt1_FixedOnAttempt2_Exit0()   // 두 번째 프롬프트 FEEDBACK에 "BUILD FAILED (1 new error)"와 "CS0103"
  [Fact] BaselineHasErrors_EditStillSucceeds_WarnsOnce()              // Review Focus 1
  [Fact] StrictBaseline_WithBaselineErrors_Exit4_FileUntouched()
  [Fact] BuildTimeout_Exit4_Restores_NoOrphan()                       // Review Focus 2: 검증 빌드 TimedOut → 4, 원본 바이트, ScriptedProcessRunner에 미완료 호출 없음
  [Fact] CancelDuringBaseline_KillsBuild_FileUntouched()              // Review Focus 3: Baseline 지연 중 취소 → 2, 백업 없음, Baseline 호출이 취소됨
  [Fact] DryRun_DoesNotWaitForBaseline_AndCancelsIt()
  [Fact] ToolUnresolvable_Exit4_BeforeAnyWrite()
  [Fact] NoCsproj_SyntaxOnly_WithNotice()
  [Fact] SyntaxErrorShortCircuits_NoBuildRun()                        // 구문 오류면 빌드 호출 없음
  [Fact] NewWarning_IsNotice_NotFailure(); [Fact] WarnAsError_NewWarningFails()
  // FeedbackBuilderBuildTests
  [Fact] ForBuild_TargetFileFirst_WithContext_OthersOneLine()
  [Fact] ForBuild_Singular_And_Plural_Header()
  [Fact] ForBuild_OverBudget_KeepsFirstTargetError()
  ```
- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*BuildVerificationFlowTests" --filter-class "*FeedbackBuilderBuildTests"` / Expected: 컴파일 실패
- [ ] **Step 3: 구현** — 기존 M1 오케스트레이터 테스트 전부가 계속 통과해야 한다(그 테스트들의 임시 프로젝트는 SDK 스타일이므로, 공통 테스트 헬퍼가 기본으로 "빌드 성공" 응답을 큐에 넣게 바꾼다).
- [ ] **Step 4: 통과 확인** — Run: `dotnet build -warnaserror && dotnet test` / Expected: 실패 0. push 후 CI 4잡 녹색
- [ ] **Step 5: Commit** — `git commit -m "feat(core): verify by build with parallel baseline and step pipeline"`

---

### Task 7: 멤버 모양 검사와 헬퍼 편집 영역

**Files:**
- Create: `src/PellyCoder.Core/CSharp/MemberShapeGuard.cs`
- Modify: `Phases/ApplyPhase.cs`, `Phases/SlicePhase.cs`, `src/PellyCoder.Core/CSharp/RoslynSlicer.cs`, `src/PellyCoder.Core/Llm/PromptBuilder.cs`, `Llm/Resources/prompts/system.cs.txt` + `fewshot.cs.txt` (둘 다 `# version: 2`), `Feedback/FeedbackBuilder.cs`, `Fsm/RunRequest.cs`
- Test: `tests/PellyCoder.Core.Tests/CSharp/MemberShapeGuardTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/ShapeGuardFlowTests.cs`

**Interfaces:**
- Consumes: `MemberCandidate`, `MemberLocator.DisplayOf/FindByName` (M1), `RoslynSlicer.Build`, `PromptBuilder.BuildCSharp`
- Produces:
  * `sealed record ShapeCheck(bool Ok, string? Reason, IReadOnlyList<string> AddedPrivateMethods)` — `AddedPrivateMethods`는 이번 패치로 새로 생긴 private 메서드의 Display 목록.
  * `static class MemberShapeGuard { ShapeCheck Check(string beforeLf, string afterLf, string targetDisplay, LanguageVersion version); }` — 설계 §8.7.1 규칙(구체화):
    * 대상 멤버(이름·Display로 찾음): 매개변수(타입·`ref/out/in/params/this`·기본값), 반환 타입, 제네릭 매개변수·제약, 접근 한정자, `static`, 특성 목록이 같아야 한다. `async`는 반환 타입이 같을 때만 추가·제거 허용.
    * 대상 외 기존 멤버: 공백 정규화한 선언 텍스트가 모두 같아야 한다(삭제·변경 거부).
    * 새 멤버: 같은 타입 안의 `private` 메서드만 허용. 그 외(필드, 프로퍼티, public/internal/protected 메서드, 중첩 타입, 다른 타입) 거부.
    * using: 추가만 허용, 삭제 거부.
    * Reason 문구(영어, 그대로): `"signature of <before Display> changed to <after Display>. Signature changes are not allowed."`, `"attributes of <Display> changed. Attribute changes are not allowed."`, `"member <Display> was removed or changed. Only the target member may change."`, `"added <kind> <name>. Only private helper methods may be added."`, `"using <ns> was removed. Usings may only be added."`
  * `FeedbackBuilder.ForShapeRejection(string reason) → Feedback` = `"SHAPE REJECTED: " + reason`(예산 적용).
  * APPLY: 패치 성공 후·쓰기 전(드라이런 포함)에 `Check` → 실패면 `ctx.Feedback`, `ctx.FailureKind = "ShapeRejected"`, `PhaseOutcome.ShapeRejected`(전이 표에 이미 있음, 종료 1). 성공이면 `ctx.AddedHelpers`(`List<string>`, RunContext에 추가)에 누적(중복 없이).
  * SLICE·슬라이서: `RoslynSlicer.Build`에 `IReadOnlyList<string> helperDisplays` 매개변수 추가 — 현재 파일에서 찾은 각 헬퍼의 원문을 `SlicePart("helpers", …, Priority 5)`로 넣고, 포함됐으면 그 범위를 `EditableRegion(span, "helper")`로 추가. signatures 파트에서는 헬퍼를 뺀다. 예산 초과 시 helpers는 fields 다음(마지막)에 제거.
  * PromptBuilder: CONTEXT 순서 fields, signatures, usings, helpers, target; 섹션 머리 `[EDITABLE: helper]`. system.cs.txt 규칙 3을 `"SEARCH must copy contiguous lines verbatim from [EDITABLE: target], [EDITABLE: usings] or [EDITABLE: helper]. Never copy from [READ-ONLY] sections."`로 바꾸고 버전 2(few-shot도 버전 2, 내용 동일 가능). `PromptBuilderTests`의 정확 레이아웃 테스트를 갱신.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // MemberShapeGuardTests
  [Fact] BodyChangeOnly_IsOk()
  [Theory] [InlineData("add parameter")] [InlineData("change parameter type")] [InlineData("ref to out")] [InlineData("default value")] [InlineData("return type")] [InlineData("public to internal")] [InlineData("add static")] [InlineData("generic constraint")]
  SignatureChanges_AreRejected(string variant)
  [Fact] AsyncToggle_SameReturnType_IsOk(); [Fact] AsyncToggle_ChangesReturnType_IsRejected()
  [Fact] AttributeChange_IsRejected()
  [Fact] OtherMemberRemoved_IsRejected(); [Fact] OtherMemberChanged_IsRejected()
  [Fact] AddedPrivateMethod_IsOk_AndListed(); [Fact] AddedPublicMethod_IsRejected(); [Fact] AddedField_IsRejected(); [Fact] AddedNestedType_IsRejected()
  [Fact] UsingAdded_IsOk(); [Fact] UsingRemoved_IsRejected()
  [Fact] ReasonText_IsExact()   // 각 Reason 문구 정확 비교
  // ShapeGuardFlowTests (ScriptedLlmBackend + ScriptedProcessRunner)
  [Fact] SignatureChange_IsShapeRejected_NeverWritten()      // Review Focus 4: 시도 1 시그니처 변경 → 파일 쓰기 0회, 두 번째 프롬프트에 "SHAPE REJECTED", 시도 2 성공 → 0
  [Fact] HelperAddedOnAttempt1_IsEditableOnAttempt2()        // 시도 1이 헬퍼(빌드 오류 포함) 추가 → 시도 2의 프롬프트에 "[EDITABLE: helper]", 헬퍼 수정 SEARCH가 OutsideRegion 아님 → 0
  [Fact] DryRun_ShapeRejected_StillReported()
  ```
- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*MemberShapeGuardTests" --filter-class "*ShapeGuardFlowTests"` / Expected: 컴파일 실패
- [ ] **Step 3: 구현**
- [ ] **Step 4: 통과 확인** — Run: `dotnet build -warnaserror && dotnet test` / Expected: 실패 0(갱신한 PromptBuilder·Slicer 테스트 포함). push 후 CI 녹색
- [ ] **Step 5: Commit** — `git commit -m "feat(csharp): reject shape changes and let retries edit added helpers"`

---

### Task 8: 대상 탐색 — `--line`, 식별자, 모델 선택

**Files:**
- Create: `src/PellyCoder.Core/CSharp/IdentifierExtractor.cs`, `src/PellyCoder.Core/Llm/ModelMemberPicker.cs`, `src/PellyCoder.Core/Llm/Resources/prompts/locate.txt`
- Modify: `src/PellyCoder.Core/CSharp/MemberLocator.cs`, `Fsm/RunRequest.cs`, `Phases/LocatePhase.cs`, `FsmOrchestratorFactory.cs`, `Llm/PromptResources.cs`
- Test: `tests/PellyCoder.Core.Tests/CSharp/MemberLocatorLineTests.cs`, `IdentifierExtractorTests.cs`, `tests/PellyCoder.Core.Tests/Llm/ModelMemberPickerTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/LocateFlowTests.cs`

**Interfaces:**
- Consumes: `MemberLocator`(M1), `QwenChatTemplate`, `ILlmBackend`, `LlmOptions`, `SamplingSet`, `ScriptedLlmBackend`
- Produces:
  * `RunRequest`가 `(string FilePath, string? ClassName, string? MethodName, int? Line, string Request, bool Yes, bool DryRun, int? MaxRetries)`로 바뀐다(`MethodName` nullable, `Line` 추가). `Line`과 `ClassName/MethodName`을 함께 주면 LOCATE가 EnvError(`"--line은 --class/--method와 함께 쓸 수 없습니다"`) — CLI도 같은 검사(Task 9).
  * `MemberLocator.FindAtLine(string sourceLf, LanguageVersion version, int line) → MemberCandidate?` — 줄을 포함하는 가장 안쪽의 메서드·생성자·프로퍼티(접근자 안이면 그 프로퍼티)·연산자·변환 연산자; 지역 함수·람다 안이면 감싼 멤버; Span 기준(문서 주석 포함). 그 밖의 줄 → null.
  * `MemberLocator.AllMembers(string sourceLf, LanguageVersion version) → IReadOnlyList<MemberCandidate>` — `FindByName`과 같은 종류, 소스 순서.
  * `static class IdentifierExtractor { IReadOnlyList<string> Candidates(string request); }` — 설계 §8.4 정규식 셋(백틱, 큰따옴표, `\b[A-Z][A-Za-z0-9_]+\b`) 결과를 등장 순서·중복 없이. 백틱·따옴표 안에 `.`이 있으면 마지막 조각도 후보(`OrderService.CancelAsync` → `OrderService.CancelAsync`, `CancelAsync`).
  * `sealed class ModelMemberPicker(ILlmBackend backend, QwenChatTemplate template, PromptResources prompts, LlmOptions llm, SamplingSet sampling)`: `Task<MemberCandidate?> PickAsync(IReadOnlyList<MemberCandidate> members, string request, CancellationToken ct)` — 원시 프롬프트 = `template.Build(prompts.Locate, user) + template.EmptyThink`; user = `"### MEMBERS\n- <Display>\n…\n### REQUEST\n<request>"`; Sampling = Code, NumPredict 32, Stop [EndOfTurn]; 출력 첫 비어 있지 않은 줄을 `Trim()`·앞의 `- ` 제거 후 Display와 정확히 같으면 그것, 아니면 이름(괄호 앞, 마지막 `.` 뒤)이 정확히 하나와 같으면 그것, 아니면 null.
  * `locate.txt`(정확히):
    ```text
    # version: 1
    You choose which C# member a change request is about.
    Answer with exactly one line copied from the MEMBERS list, nothing else.
    ```
    `PromptResources`에 `Locate` 속성 추가(버전 검사에는 포함하지 않음).
  * LOCATE 흐름(설계 §8.4 순서): ① `Line` 지정 → `FindAtLine`, null이면 NotFound + 메시지 `"{line}번 줄은 메서드 안이 아닙니다. 메서드 안에 커서를 두고 실행하세요"`, `FailureKind = "TargetNotFound"`. ② `MethodName` 지정 → 기존 흐름. ③ 둘 다 없음 → `IdentifierExtractor` 후보 중 `AllMembers`의 이름과 같은 것만 → 1개면 그것, 2개 이상이면 기존 다중 후보 처리(대화형 선택/Ambiguous), 0개면 `AllMembers`가 60개 이하일 때 `ModelMemberPicker`, 60개 초과면 NotFound(`"멤버가 60개를 넘어 자동으로 고를 수 없습니다. --method나 --line을 쓰세요"`), 모델 답이 null이면 NotFound(`"요청문에서 대상 멤버를 찾지 못했습니다. --method나 --line을 쓰세요"`). 모델 선택 결과는 `ctx.Messages`에 `"대상: <Display> (모델이 선택)"`.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // MemberLocatorLineTests (코퍼스 Modern OrderService.cs + 인라인 소스)
  [Fact] Line_InsideMethodBody_ReturnsMethod()
  [Fact] Line_OnDocComment_ReturnsMember()
  [Fact] Line_InsideLambda_ReturnsEnclosingMember()
  [Fact] Line_InsidePropertyAccessor_ReturnsProperty()
  [Fact] Line_OnField_ReturnsNull(); [Fact] Line_OnClassDeclaration_ReturnsNull(); [Fact] Line_OnUsing_ReturnsNull(); [Fact] Line_OutOfRange_ReturnsNull()
  [Fact] Line_PicksOverloadContainingLine()                  // Review Focus 5 (인라인: 붙어 있는 M(int) / M(string))
  [Fact] AllMembers_SourceOrder()
  // IdentifierExtractorTests
  [Fact] Backticks_Quotes_PascalCase_InOrder_Distinct(); [Fact] DottedName_AddsLastSegment(); [Fact] KoreanRequest_WithoutIdentifiers_IsEmpty()
  // ModelMemberPickerTests (ScriptedLlmBackend)
  [Fact] SendsExactPrompt_NumPredict32_EmptyThink(); [Fact] ExactDisplay_IsPicked(); [Fact] NameOnly_UniqueMatch_IsPicked(); [Fact] UnknownAnswer_IsNull(); [Fact] DashPrefix_IsStripped()
  // LocateFlowTests (FSM, ScriptedLlmBackend)
  [Fact] Line_OnField_IsNotFound_WithCursorHint()            // Review Focus 5: 종료 3, 메시지 정확, 파일 그대로
  [Fact] Line_WithMethod_IsEnvError()
  [Fact] RequestNamesMethod_IsFoundWithoutModelCall()        // "`CancelAsync`를 고쳐줘" → 모델 호출은 EDIT 1번뿐
  [Fact] NoIdentifiers_ModelPicks_Exit0()                    // 첫 응답 = 선택, 둘째 = 패치
  [Fact] ModelAnswerNotInList_Exit3()
  [Fact] MoreThan60Members_Exit3_WithHint()
  ```
- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*MemberLocatorLineTests" --filter-class "*IdentifierExtractorTests" --filter-class "*ModelMemberPickerTests" --filter-class "*LocateFlowTests"` / Expected: 컴파일 실패
- [ ] **Step 3: 구현** — `RunRequest` 생성 호출부(CLI, 테스트)를 새 시그니처로 맞춘다(CLI의 옵션 추가는 Task 9; 여기서는 `Line: null`을 넘기게만).
- [ ] **Step 4: 통과 확인** — Run: `dotnet build -warnaserror && dotnet test` / Expected: 실패 0
- [ ] **Step 5: Commit** — `git commit -m "feat(locate): find target by line, request identifiers, or model pick"`

---

### Task 9: CLI — `--line`, 선택적 `--method`·요청문, 빌드 옵션, doctor 빌드 점검

**Files:**
- Modify: `src/PellyCoder.Cli/Commands/CsCommand.cs`, `OptionsResolver.cs`(위치는 현재 파일 기준), `Composition.cs`, `Runners.cs`, `src/PellyCoder.Core/Doctor/DoctorService.cs`, `README.md`
- Test: `tests/PellyCoder.Cli.Tests/CsCommandTests.cs`, `tests/PellyCoder.Core.Tests/Doctor/DoctorServiceTests.cs`

**Interfaces:**
- Consumes: `RunRequest`(Task 8), `BuildToolResolver`, `IProcessRunner`, `SystemProcessRunner`(Task 1·2), `FsmOrchestratorFactory.Create(…, processes)`(Task 6)
- Produces:
  * `pelly cs <FILE> [REQUEST] [--class N] [--method N] [--line N] [--yes] [--dry-run] [--max-retries N] [--strict-baseline] [--warn-as-error] [--config P] [--think M] [--model T] [--endpoint U]`
  * 검사(모두 종료 4, 문구 정확): `--line`과 `--class/--method` 동시 → `"--line은 --class/--method와 함께 쓸 수 없습니다"`; `--line` ≤ 0 → `"--line은 1 이상이어야 합니다"`; `--max-retries` < 0 → `"--max-retries는 0 이상이어야 합니다"`; REQUEST 생략 + 대화형 → `IAnsiConsole` 텍스트 프롬프트 `"수정 요청을 입력하세요:"`로 받기(빈 입력이면 `"요청문(REQUEST)이 비어 있습니다"`); REQUEST 생략 + 비대화형 → `"요청문(REQUEST)이 필요합니다 (비대화형 실행)"`. M1 메시지 `"M1에서는 --method로…"`는 삭제.
  * `--strict-baseline` → CLI 덮어쓰기 `build:strictBaseline=true`, `--warn-as-error` → `build:warnAsError=true`.
  * Composition: `IProcessRunner`를 `SystemProcessRunner(TimeProvider.System)` 싱글턴으로 등록해 오케스트레이터와 doctor에 넘긴다.
  * `DoctorService` 생성자에 `IProcessRunner processes, Func<bool>? isWindows = null` 추가. `서버 설정 확인 필요` 앞에 두 행 추가: `".NET SDK"` — `dotnet --version`(시간 초과 10초) 성공 → Ok `"<버전>"`, 실패 → Fail `"dotnet을 실행할 수 없습니다"` / Action `".NET 10 SDK를 설치하고 PATH를 확인하세요"`; `"MSBuild (레거시)"` — Windows면 `BuildToolResolver.ResolveAsync(Legacy)` 성공 → Ok 경로, 실패 → Warn 그 Error 문구 / Action `"레거시 .NET Framework 프로젝트를 고치려면 Visual Studio Build Tools가 필요합니다"`; Windows가 아니면 Warn `"Windows에서만 확인합니다"`.
  * README "사용법" 갱신: 새 옵션, 대상 지정 3가지 방법, 빌드 검증과 Baseline 동작(원래 있던 오류는 세지 않음), 레거시는 Windows에서만.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // CsCommandTests
  [Fact] Line_WithMethod_Exit4_Message(); [Fact] Line_Zero_Exit4(); [Fact] MaxRetriesNegative_Exit4()
  [Fact] NoMethodNoLine_PassesNullsToRunner()                // 이제 허용
  [Fact] Line_IsPassedToRunner()
  [Fact] RequestOmitted_Interactive_PromptsAndPasses()       // TestConsole 입력 "요청"
  [Fact] RequestOmitted_NonInteractive_Exit4_Message()
  [Fact] StrictBaseline_And_WarnAsError_OverrideOptions()
  // DoctorServiceTests (ScriptedProcessRunner, isWindows 주입)
  [Fact] DotnetVersion_Ok(); [Fact] DotnetMissing_Fail(); [Fact] MsBuild_NonWindows_Warn(); [Fact] MsBuild_WindowsResolved_Ok(); [Fact] MsBuild_WindowsMissing_Warn()
  ```
- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Cli.Tests && dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*DoctorServiceTests"` / Expected: 새 테스트 실패
- [ ] **Step 3: 구현**
- [ ] **Step 4: 통과 확인** — `dotnet build -warnaserror && dotnet test` 실패 0; 실제 실행 `dotnet run --project src/PellyCoder.Cli -- doctor --model qwen3.6:35b-a3b-coding-nvfp4` 표에 `.NET SDK` Ok, `MSBuild (레거시)` Warn(Mac). push 후 CI 녹색
- [ ] **Step 5: Commit** — `git commit -m "feat(cli): add --line, optional target and request, build options, doctor build checks"`

---

### Task 10: 프롬프트 안전 — 제어 토큰 차단과 피드백 예산 보장

**Files:**
- Create: `src/PellyCoder.Core/Llm/SpecialTokenGuard.cs`
- Modify: `src/PellyCoder.Core/Llm/QwenTokenCounter.cs` (특수 토큰 목록 노출), `Phases/PreflightPhase.cs`, `Feedback/FeedbackBuilder.cs`
- Test: `tests/PellyCoder.Core.Tests/Llm/SpecialTokenGuardTests.cs`, `tests/PellyCoder.Core.Tests/Feedback/FeedbackBuilderBudgetTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/PromptSafetyFlowTests.cs`

**Interfaces:**
- Produces:
  * `QwenTokenCounter.SpecialTokens : IReadOnlyCollection<string>` (tokenizer-meta.json의 26개).
  * `static class SpecialTokenGuard { string? FindIn(string text, IReadOnlyCollection<string> specialTokens); }` — 처음 발견한 토큰 문자열(서수 비교) 또는 null.
  * PREFLIGHT(서버 확인 전): 요청문에 토큰 → EnvError `"요청문에 모델 제어 토큰 '<tok>'이(가) 있어 실행할 수 없습니다"`; 원본 파일에 토큰 → EnvError `"파일에 모델 제어 토큰 문자열 '<tok>'이(가) 있어 안전하게 편집할 수 없습니다 (줄 <n>)"`. 이유: raw 모드에서 Ollama가 이 문자열을 제어 토큰으로 읽는다(M1 최종 리뷰).
  * `FeedbackBuilder`: 어떤 입력이든 결과 `Tokens ≤ budget`를 보장 — 기존 축소 단계 뒤에도 넘으면 첫 항목의 문맥 줄(오류 줄 포함) 텍스트를 줄 끝에서부터 잘라 `…`을 붙이고, 그래도 넘으면 파일 경로를 파일 이름만으로 줄이고, 마지막으로 전체 텍스트를 잘라 `…`. 서로게이트 쌍을 나누지 않는다(`char.IsSurrogatePair` 경계 보정). 이 보장은 `ForDiagnostics`, `ForBuild`, `ForPatchFailure`, `ForShapeRejection` 모두에 적용.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] FindIn_DetectsImEnd(); [Fact] FindIn_DetectsThinkClose(); [Fact] FindIn_PlainText_IsNull()
  [Fact] RequestWithControlToken_Exit4_NoServerCall()        // ScriptedLlmBackend.Requests 0, Status 호출도 전
  [Fact] FileWithControlToken_Exit4_LineReported_FileUntouched()
  [Theory] [InlineData(5000)] [InlineData(20000)] HugeErrorLine_FeedbackWithinBudget(int lineLength)   // Tokens ≤ 296, "CS" 코드와 머리말 포함
  [Fact] HugePath_FeedbackWithinBudget(); [Fact] Truncation_NeverSplitsSurrogatePair()   // 이모지 경계
  ```
- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*SpecialTokenGuardTests" --filter-class "*FeedbackBuilderBudgetTests" --filter-class "*PromptSafetyFlowTests"` / Expected: 실패
- [ ] **Step 3: 구현**
- [ ] **Step 4: 통과 확인** — `dotnet build -warnaserror && dotnet test` 실패 0
- [ ] **Step 5: Commit** — `git commit -m "fix(core): block raw-mode control tokens and guarantee feedback budget"`

---

### Task 11: FileGuard — 심볼릭 링크와 Unix 파일 모드 보존

**Files:**
- Modify: `src/PellyCoder.Core/Files/FileGuard.cs`
- Test: `tests/PellyCoder.Core.Tests/Files/FileGuardLinkAndModeTests.cs`

**Interfaces:**
- 규칙: 대상 경로가 심볼릭 링크면 최종 대상 파일을 해석해 읽기·백업·쓰기·복원을 그 파일에 한다(링크 자체는 그대로 둔다). 대상 폴더의 임시 파일은 최종 대상 파일 폴더에 만든다. Unix에서는 교체 전에 임시 파일에 원래 파일의 `UnixFileMode`를 복사한다(쓰기·복원 모두). Windows는 변경 없음(링크 테스트는 권한 문제로 Unix만).
- Produces: 공개 API 변화 없음. `FileSnapshot.Path`는 사용자가 준 경로를 유지한다.

- [ ] **Step 1: 실패하는 테스트 작성** (실제 임시 폴더; Windows에서는 `Assert.Skip("Unix only")`)
  ```csharp
  [Fact] Write_PreservesExecutableBit()             // 0755 파일 → 쓰기 후 0755
  [Fact] Restore_PreservesMode()
  [Fact] Write_ThroughSymlink_KeepsLink_UpdatesTarget()   // link → target; 쓰기 후 link는 여전히 링크, target 내용 변경
  [Fact] Restore_ThroughSymlink_KeepsLink()
  [Fact] ExternalChangeCheck_UsesTargetBytes()
  ```
- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*FileGuardLinkAndModeTests"` / Expected: 실패
- [ ] **Step 3: 구현**
- [ ] **Step 4: 통과 확인** — 같은 명령 + `--filter-namespace "*Files*"` 전체 / Expected: 통과. push 후 Windows 잡 녹색(Skip 확인)
- [ ] **Step 5: Commit** — `git commit -m "fix(files): keep symlinks and unix file mode on write and restore"`

---

### Task 12: 취소 판정·설정 검증·로그 보관

**Files:**
- Modify: `src/PellyCoder.Core/Fsm/FsmOrchestrator.cs`, `src/PellyCoder.Core/Logging/RunLogger.cs`, `src/PellyCoder.Core/Config/PellyOptionsLoader.cs`, `src/PellyCoder.Cli/CliApp.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/CancellationClassificationTests.cs`, `tests/PellyCoder.Core.Tests/Config/PellyOptionsValidationTests.cs`, `tests/PellyCoder.Core.Tests/Logging/RetentionTests.cs`, `tests/PellyCoder.Cli.Tests/ExitCodeMappingTests.cs`

**Interfaces:**
- 규칙:
  * 오케스트레이터: `OperationCanceledException`은 실행 토큰이 취소됐을 때만 `Cancelled`(종료 2), 그 외에는 `Crashed`(종료 4). CLI 전역 처리기(`CliApp`)도 공유 종료 토큰이 취소됐을 때만 2, 아니면 4.
  * 설정 검증(로더 오류 목록에 추가, 키 이름 포함): `llm:numCtx`, `llm:connectTimeoutSeconds`, `llm:firstTokenTimeoutSeconds`, `llm:streamIdleTimeoutSeconds`, `budget:*`, `build:timeoutSeconds`, `log:retentionDays` 는 ≥ 1; `retry:max` ≥ 0; `sampling:*:topK` ≥ 1.
  * 로그 보관 정리는 잠금을 얻은 실행에서만, 실행이 끝날 때 한다(`RunLogger.Prune(Workspace ws, int retentionDays)`를 오케스트레이터가 `ctx.Lock != null`일 때 `finally`에서 호출; `Start`에서는 정리하지 않는다). 현재 실행 폴더는 절대 지우지 않는다. `RunLogger`의 실패 표시 상태는 실행마다 초기화.
- Produces: `RunLogger.Prune(Workspace ws, int retentionDays, string currentRunId)`.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] StrayOperationCanceled_WithoutUserCancel_IsCrashedExit4()   // ScriptedLlmBackend.EnqueueException(new TaskCanceledException()) → 4, 롤백
  [Fact] UserCancel_IsExit2()
  [Theory] [InlineData("llm:numCtx","0")] [InlineData("log:retentionDays","0")] [InlineData("retry:max","-1")] [InlineData("build:timeoutSeconds","0")] [InlineData("budget:think","0")] [InlineData("sampling:code:topK","0")]
  InvalidValue_IsError_NamingKey(string key, string value)
  [Fact] Prune_OnlyWhenLockHeld(); [Fact] Prune_NeverDeletesCurrentRun(); [Fact] LoggerFailureFlag_ResetsPerRun()
  [Fact] Cli_UnexpectedOperationCanceled_Exit4()                    // ExitCodeMappingTests
  ```
- [ ] **Step 2: 실패 확인** — 해당 클래스 필터 / Expected: 실패
- [ ] **Step 3: 구현**
- [ ] **Step 4: 통과 확인** — `dotnet build -warnaserror && dotnet test` 실패 0
- [ ] **Step 5: Commit** — `git commit -m "fix(core): classify cancellation by token, validate config ranges, prune logs under lock"`

---

### Task 13: 사용자 안내 — 진행 표시·서버 메시지·줄바꿈 혼합·인라인 SQL

**Files:**
- Modify: `src/PellyCoder.Cli/Terminal/SpectreUserInteraction.cs`, `src/PellyCoder.Core/Fsm/Phases/PreflightPhase.cs`, `src/PellyCoder.Core/Files/FileSnapshot.cs`, `src/PellyCoder.Core/Fsm/Phases/ReviewPhase.cs`
- Create: `src/PellyCoder.Core/CSharp/InlineSqlDetector.cs`
- Test: `tests/PellyCoder.Cli.Tests/SpectreUserInteractionTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/NoticeTests.cs`, `tests/PellyCoder.Core.Tests/CSharp/InlineSqlDetectorTests.cs`, `tests/PellyCoder.Core.Tests/Files/TextFileCodecTests.cs`(추가)

**Interfaces:**
- 규칙:
  * `ShowStatus`: `attempt == 0`이면 `"[grey]<phase>[/] <message>"`(시도 표시 없음), 1 이상이면 기존 형식. 모든 외부 문자열 `Markup.Escape` — `[x]`가 든 경로·메시지·diff 줄로 회귀 테스트(`TestConsole`).
  * PREFLIGHT: `status.Reachable && !status.ModelInstalled && status.Error is not null`이면 "모델 없음" 대신 `"서버에서 모델 목록을 확인하지 못했습니다: <Error> (pelly doctor)"`.
  * `FileSnapshot`에 `bool MixedNewLines` 추가(CRLF와 단독 LF가 둘 다 있으면 true). PREFLIGHT가 true면 `ctx.Messages`에 `"파일에 CRLF와 LF가 섞여 있습니다 — 수정하면 줄바꿈이 <다수 쪽>으로 통일됩니다"`.
  * `static class InlineSqlDetector { bool ChangedSqlLiteral(string beforeLf, string afterLf, string targetDisplay, LanguageVersion version); }` — 수정 후 대상 멤버의 문자열 리터럴(일반·축자·보간 텍스트) 중 수정 전에 없던 리터럴이 `\b(SELECT|INSERT|UPDATE|DELETE|EXEC)\b`(대소문자 무시)를 포함하면 true. REVIEW가 diff를 보여주기 전에 true면 `ui.Warn("인라인 SQL이 바뀌었습니다 — 빌드는 통과했지만 DB로 검증되지 않았습니다 (설계 §8.15)")`.
- Produces: 위 공개 멤버.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] ShowStatus_AttemptZero_HasNoAttemptText(); [Fact] ShowStatus_AttemptTwo_ShowsAttempt()
  [Fact] Escape_BracketsInPathMessageAndDiff_DoNotThrow_AndRenderLiterally()
  [Fact] TagsFailure_ReportsListError_NotModelMissing()
  [Fact] MixedNewLines_Detected(); [Fact] MixedNewLines_Notice()
  [Fact] InlineSql_NewSelectLiteral_True(); [Fact] InlineSql_UnchangedLiteral_False(); [Fact] InlineSql_NonSqlLiteral_False(); [Fact] Review_ShowsInlineSqlWarning()
  ```
- [ ] **Step 2: 실패 확인** — 해당 클래스 필터 / Expected: 실패
- [ ] **Step 3: 구현**
- [ ] **Step 4: 통과 확인** — `dotnet build -warnaserror && dotnet test` 실패 0
- [ ] **Step 5: Commit** — `git commit -m "feat: clearer notices for attempts, server errors, mixed newlines, inline SQL"`

---

### Task 14: 실제 모델 E2E — 재시도·think·빌드 검증·자동 탐색

**Files:**
- Create: `tests/PellyCoder.Integration.Tests/E2E/CorruptingBackend.cs`, `tests/PellyCoder.Integration.Tests/E2E/BuildEndToEndTests.cs`
- Modify: `tests/PellyCoder.Integration.Tests/E2E/CsEndToEndTests.cs` (팩토리 새 시그니처), `docs/m1-e2e.md` → 유지, 신규 `docs/m2a-e2e.md`

**Interfaces:**
- Consumes: `FsmOrchestratorFactory.Create(fs, backend, ui, options, time, processes)`(Task 6), `OllamaBackend`, `SystemProcessRunner`, `RunRequest`(Task 8)
- Produces: `CorruptingBackend(ILlmBackend inner, Func<int, string, string> rewrite)` — n번째 생성 요청(1부터)의 응답 텍스트를 `rewrite(n, text)`로 바꿔 흘려보내는 테스트 데코레이터(GetStatusAsync는 그대로 위임).

- [ ] **Step 1: 테스트 작성** (`[OllamaFact]`, Modern 코퍼스 임시 복사 + `dotnet restore`)
  ```csharp
  [OllamaFact] FirstAttemptMismatch_RetryUsesThink_Succeeds()   // rewrite: 1번째 응답의 SEARCH 첫 줄을 없는 텍스트로 → 종료 0, summary Attempts 2, ThinkMode retry에서 2번째 시도 ThinkUsed(think 요청 존재)
  [OllamaFact] FirstAttemptBuildError_FeedbackFixes_Succeeds()  // rewrite: 1번째 응답 REPLACE 첫 줄 뒤에 "        undefinedVar++;" 삽입 → 빌드 CS0103 → 2번째 시도 프롬프트에 "BUILD FAILED" → 종료 0, ErrorCodes ∋ "CS0103"
  [OllamaFact] LineTarget_Works()                                 // CancelAsync 본문 안 줄 번호 → 종료 0
  [OllamaFact] NoMethodNoLine_RequestIdentifier_Works()           // 요청에 "`CancelAsync`" 포함 → 종료 0
  [OllamaFact] NoMethodNoLine_ModelPick_Works()                   // 식별자 없는 한국어 요청 → 종료 0, Messages에 "모델이 선택"
  ```
- [ ] **Step 2: 실행** — `PELLY_TEST_OLLAMA=http://localhost:11434 PELLY_TEST_MODEL=qwen3.6:35b-a3b-coding-nvfp4 dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*E2E*" --fail-skips on`을 3회. 실패 시 M1 Task 15와 같은 규칙(프롬프트 리소스 문제면 리소스 수정 + 버전 증가, Core 버그면 BLOCKED, 기대값 완화 금지).
- [ ] **Step 3: 기록** — `docs/m2a-e2e.md`에 테스트별 3회 통과 수, 시도 수, think 사용, 단계별 시간(특히 Baseline 빌드 시간과 EDIT 시간의 겹침), 그리고 CLI 실제 실행 1건(`pelly cs <복사본>/Shop.Api/Services/OrderService.cs --line <n> "<요청>" --yes`)의 출력·diff. 모델이 첫 시도에 성공해 재시도 경로를 못 탄 E2E가 있으면 그 사실을 적는다(이번 E2E는 데코레이터로 강제하므로 재시도가 반드시 일어나야 한다 — 일어나지 않았다면 실패로 본다).
- [ ] **Step 4: 전체 검증** — `dotnet build -warnaserror && dotnet test` 실패 0; push 후 CI 4잡 녹색
- [ ] **Step 5: Commit** — `git commit -m "test(e2e): exercise retry, think, build feedback and auto-locate against local model"`

---

### Task 15: 문서 갱신

**Files:**
- Modify: `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md`, `docs/PRD.md`, `README.md`

- [ ] **Step 1: 설계 문서 반영** — §7.3: Baseline은 LOCATE 끝(대상 프로젝트를 안 뒤)에서 시작하고 APPLY가 기다리며, 실행 종료 시 취소·대기한다. §8.9: `BuildRun`, 파싱 못 한 실패를 오류로 보는 규칙, CP949 재디코딩, `ProjectStyle == None`이면 빌드 생략. §8.7.1: 구체화한 규칙과 Reason 문구, 헬퍼 편집 영역. §8.4: `--line`·식별자·모델 선택 흐름과 메시지, 60개 상한. §8.5: Directory.Build.props와 Condition 무시. §8.16: `IVerifierStep` 실현. 결정 표에 D24(제어 토큰 문자열 차단), D25(Baseline 시작 시점). 각 변경 옆에 근거(작업 번호).
- [ ] **Step 2: PRD 반영** — FR-00 옵션 목록(`--line`, `--strict-baseline`, `--warn-as-error`, 선택적 `--method`·REQUEST), FR-06 Baseline 문구, 변경 이력 v1.7.
- [ ] **Step 3: 검증** — 문서만 변경. `git diff --stat`으로 세 파일만 바뀐 것 확인.
- [ ] **Step 4: Commit** — `git commit -m "docs: reflect M2a build verification, shape guard, and locate flow"`

---

## M2a 완료 기준
* SDK 프로젝트에서 빌드 검증(Baseline 차감 포함)이 실제 모델 E2E로 동작(Task 14, 재시도·빌드 피드백 경로 포함).
* 레거시 프로젝트 빌드 검증이 CI `windows-integration`에서 실제 MSBuild로 통과(Task 2·5의 `[WindowsFact]`, Skip 아님).
* `--line`·요청문 식별자·모델 선택으로 대상 지정 가능.
* M1 최종 리뷰 "곧 고치길 권장" 항목(피드백 예산, 심볼릭 링크·모드, 취소 판정, 보관 기간 검증, 제어 토큰) 닫힘.
* CI 4잡 녹색, `dotnet test` 실패 0.

## M2b로 넘기는 것
`pelly sql` 전체(SqlSafetyGuard·SqlSyntaxVerifier·SqlSchemaSlicer·SQL LOCATE), 벤치마크 실행기(`PellyCoder.Eval`)와 정의 30건 이상, `sql-integration` 잡에서 SQL 검증 통과.
