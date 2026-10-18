# Pelly-Coder M3b (벤치마크 50건·튜닝·반입 패키지) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** v1 외부 개발을 끝낸다. 할 일은 다섯 가지다.
1. 동작으로 판정하는 벤치마크 50건과 반복 측정 기준선을 만든다.
2. 그 기준선으로 프롬프트·샘플링을 튜닝하되, 회귀가 생기면 채택하지 않는다.
3. `win-x64` 단일 파일 배포본을 CI에서 검증한다.
4. 폐쇄망 반입 패키지(zip, SBOM, 라이선스, 체크섬, 버전 기록, 문서)를 만든다.
5. M3a에서 넘어온 항목을 닫는다.

**Architecture:**
* 벤치마크 판정용 테스트 프로젝트를 코퍼스 밖(`eval/benchmark/judges/`)에 두고, 실행기가 작업 복사본에 대해 빌드·실행한다. 코퍼스와 pelly의 `--test`는 이 테스트를 보지 못한다.
* 튜닝은 정해진 절차를 따른다: 50건 × 3회 반복 측정 → 후보 변경 → 같은 조건으로 다시 측정 → 채택 규칙.
* 패키지 쪽:
  * 단일 파일 publish와 오프라인 자체 점검은 CI `publish-check` 잡이 맡는다.
  * zip 조립은 허용 목록 방식 스크립트가 하고, CI `package` 잡이 결과물을 아티팩트로 남긴다.

**Tech Stack:** .NET 10 (self-contained single-file publish, `win-x64`), xUnit v3, CycloneDX .NET 도구(로컬 도구 매니페스트로 버전 고정), PowerShell 7(`pwsh`, 패키지 스크립트), GitHub Actions `windows-latest`, Ollama 0.40.0, SQL Server 2022 컨테이너

**Spec:** `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md`
* 절: §9(S6·S7), §10.4(`publish-check`), §10.5, §10.6, §11 M3b 행, §8.14(IDE External Tools)
* PRD: §5(오프라인 동작), §8.1·§8.2(벤치마크·회귀 차단), §11(반입 패키지·설치·업데이트), FR-08(`--sanitize`)
* M3a 이월 근거: 원장 사본 `/private/tmp/claude-501/-Users-payletter-git-root-ReSet/80aff902-238e-4e70-8e26-123252df0fa0/scratchpad/m3a-ledger.md`의 최종 분류(멀티 타깃 테스트 프로젝트, Ctrl+C 일시 정지, 대기 시간 미보고, 누락 테스트 규칙, VSTest 종단 확인, `deploy.sh` 재실행, PRD:62).

**범위 밖 (M4로 넘김):**
* 폐쇄망 설치와 현장 확인
* 실제 코드 기반 폐쇄망 벤치마크
* Visual Studio External Tools 실기 확인
* Legacy 작업의 실제 모델 + Windows 빌드 전체 판정(GPU와 Windows가 함께 있는 환경이 없음)

## Global Constraints

* **이어받는 제약:** M1~M3a 계획의 Global Constraints를 그대로 따른다.
  * 빌드 환경: SDK `10.0.302`, `net10.0`, Nullable·ImplicitUsings·TreatWarningsAsErrors
  * 패키지: CPM(간접 고정), MIT/Apache-2.0 라이선스만
  * Core 규칙: `Console.*` 금지, 외부 프로세스는 `IProcessRunner`, DB는 `ISqlExecutor`
  * 종료 코드는 0~5만 쓴다.
  * 언어: 식별자·커밋은 영어, 문서는 한국어
  * 커밋 끝에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
  * 정규식은 `\A…\z`와 `[0-9]`로 쓴다.
  * 실기 Windows에서 못 잰 경로는 「Windows 미확인」으로 표시한다.
* **실행 중 외부 통신 없음(PRD §5):** pelly는 설정된 Ollama·SQL 서버에만 접속한다. 빌드·패키징 단계(CI)의 NuGet·도구 내려받기는 예외다. 반입 zip 안의 실행 파일은 실행 중 다른 곳에 접속하지 않는다.
* **회귀 차단(PRD §8.2):**
  * 프롬프트·샘플링·슬라이서를 바꾼 뒤에는 벤치마크 전체를 같은 조건(3회 반복)으로 실행한다.
  * 성공률이 기준선보다 떨어지면 채택하지 않는다.
  * 검증 불변 조건 위반은 0건이어야 한다.
* **버전:** 이 마일스톤의 반입본 버전은 `0.9.0`이다(R6). `Directory.Build.props`의 `Version`을 바꾸고, `PellyInfo.Version`이 그 값을 따른다.
* **반입 zip 이름:** `pelly-<버전>-win-x64.zip`.
  * 들어가는 것은 Task 9의 허용 목록에 있는 파일뿐이다.
  * 개발자 PC의 설정 파일·경로·연결 문자열이 들어가면 안 된다.
* **벤치마크 실행 환경(로컬 Mac):**
  * Ollama `http://localhost:11434`, 모델 `qwen3.6:35b-a3b-coding-nvfp4`
  * SQL 컨테이너 `pelly-corpus-sql`(`localhost,14333`)
  * 연결은 `PELLY_SQL_CONNECTION`으로 넘긴다. 허용 목록은 저장소 밖 `--config` 파일에 둔다(M3a와 같음). 비밀번호는 문서에 쓰지 않는다.
* **CI:** 각 작업을 마치기 전에 push해서 모든 잡이 녹색인지 확인한다. 이 계획이 `publish-check`·`package` 잡을 추가하므로, 그 뒤로는 6잡이다.

## Review Focus

1. **판정 테스트가 원본에서도 통과함(빈 판정)**
   * 문제: 이런 테스트는 아무것도 고치지 않은 수정도 성공으로 센다.
   * 기대 동작: 정의 테스트가 원본 코퍼스에서 `judge.tests`는 실패, `judge.failingTests`는 통과함을 확인한다.
   * 테스트: Task 3 `JudgeTests_FailOnOriginal_And_FailingTestsPassOnOriginal`
2. **단일 파일 exe에서 내장 리소스나 네이티브 라이브러리(SqlClient SNI 등)가 로드되지 않음**
   * 기대 동작: `publish-check`가 배포본 exe로 `doctor --offline-selftest`를 돌려 종료 0이어야 한다. SqlClient 행이 네이티브 계층까지 로드한다.
   * 테스트: Task 7 `SelfTest_SqlClientRow_LoadsNativeLayer` + CI `publish-check`
3. **반입 zip에 개발자 정보가 섞임**
   * 섞일 수 있는 것: 설정, 경로, 연결 문자열, `bin/obj` 찌꺼기, pdb의 로컬 경로
   * 기대 동작: zip 내용이 허용 목록과 정확히 같고, 템플릿에 비밀번호·실제 호스트가 없다.
   * 테스트: Task 9 `Package_ContainsExactlyAllowedFiles`, `Template_HasNoSecretsOrRealHosts`, `Pdb_NotShipped`
4. **튜닝이 측정 분산을 개선으로 착각함**
   * 기대 동작: 채택 규칙(R4)이 3회 반복 평균과 작업별 하락을 함께 보고, 분산 안의 차이는 채택하지 않는다.
   * 테스트: Task 6 `Acceptance_WithinNoise_IsRejected`, `Acceptance_CategoryDrop_IsRejected`(실행기의 비교 기능 단위 테스트)
5. **IDE 외부 도구 창에서 Ctrl+C로 중단하면 창이 바로 닫힘(M3a 이월)**
   * 기대 동작: Ctrl+C는 결과를 보여 주고 키 입력을 기다린다. 창 닫기(SIGHUP·CTRL_CLOSE)·SIGTERM은 기다리지 않는다.
   * 테스트: Task 1 `CtrlC_StillPauses`, `TerminationSignal_SkipsPause`

## 결정 (계획 단계 판단, 틀렸을 때 비용 포함)

* **R1. 동작 판정은 Modern C# 작업에만 둔다.**
  * Legacy는 Mac에서 빌드·테스트를 할 수 없고 Windows+GPU 환경이 없다. 그래서 문자열 판정을 유지하고 M4에서 전체 판정한다.
  * SQL은 판정을 위해 모델 SQL을 실행해야 하는데, DB 무변경 원칙과 부딪혀 문자열 + NOEXEC 판정을 유지한다.
  * 틀렸을 때 비용: Legacy·SQL 성공률은 계속 문자열 기준이다.
* **R2. 판정 테스트 프로젝트는 코퍼스 밖(`eval/benchmark/judges/Modern/Shop.Api.JudgeTests`)에 둔다.**
  * 코퍼스를 가리키는 경로는 MSBuild 속성 `JudgeTargetRoot`로 바꿀 수 있게 한다.
  * 이유: 코퍼스 CI 빌드·테스트와 pelly `--test`가 판정 테스트를 보지 않게 하기 위함이다(원본에서 실패하는 테스트라 CI를 깨뜨린다).
  * 틀렸을 때 비용: 실행기가 프로젝트 하나를 더 빌드한다(작업당 수 초).
* **R3. 새 판정 필드 `judge.failingTests`를 둔다(수정 뒤 실패해야 하는 테스트).**
  * 용도: 코퍼스의 `*_CurrentlyCancels`처럼 수정 전 동작을 고정한 테스트를 동작 판정에 쓴다.
  * 이 필드의 테스트는 판정 프로젝트가 아니라 코퍼스 테스트 프로젝트에서 찾는다(M3a T14의 동작 판정과 같은 방식).
* **R4. 튜닝 채택 규칙.** 후보는 아래를 모두 만족해야 채택한다.
  1. 3회 반복 평균 성공 작업 수가 기준선보다 **1.0건 이상** 많다.
  2. 어떤 범주(cs-legacy·cs-modern·sql)도 평균이 0.5건 넘게 줄지 않는다.
  3. 3회 모두 검증 불변 조건 위반이 0건이다.
  4. 작업 시간 중위값이 기준선의 1.5배를 넘지 않는다.

  만족하는 후보가 없으면 기본값을 유지하고, 그것도 결과로 기록한다. 후보 수 상한은 4개다(측정 시간).
  틀렸을 때 비용: 작은 개선은 놓칠 수 있다.
* **R5. 튜닝 후보는 미리 정한 목록에서만 고른다.**
  * 목록은 Task 6에 적는다. 출처는 M3a에서 관찰한 결함 유형이다.
    * 모순 요청 회피 25%
    * think 512토큰 잘림
    * 판정 실패 유형
  * 이유: 측정 뒤에 후보를 늘리면 과적합된다.
  * 틀렸을 때 비용: 목록 밖의 좋은 아이디어는 다음 마일스톤으로 넘어간다.
* **R6. 반입본 버전은 `0.9.0`, M4 현장 확인 뒤 `1.0.0`으로 올린다.**
* **R7. SBOM은 CycloneDX .NET 도구를 쓴다.**
  * 로컬 도구 매니페스트 `.config/dotnet-tools.json`에 정확한 버전으로 고정한다(Apache-2.0).
  * 서드파티 라이선스 목록은 SBOM에서 뽑아 Markdown으로 만든다.
  * 라이선스가 MIT/Apache-2.0/BSD 계열이 아닌 패키지가 나오면 패키지 스크립트가 실패한다.
  * 틀렸을 때 비용: 도구 버전을 올릴 때 매니페스트를 바꿔야 한다.
* **R8. 모델 GGUF SHA-256과 Ollama 버전은 로컬 스크립트(`deploy/record-versions.sh`)가 개발 PC의 Ollama에서 읽어 `deploy/version-record.json`에 남긴다.** 이 파일은 커밋하며, 패키지 스크립트가 zip에 넣는다.
  * 읽는 곳: `/api/version`, `/api/show`의 `modelfile` FROM 줄
  * 이유: CI에는 모델이 없다.
  * 틀렸을 때 비용: 반입 전에 사람이 스크립트를 다시 실행해야 한다.

---

## 파일 구조 (신규·변경)

```text
.config/dotnet-tools.json                     # 신규: CycloneDX 고정
.github/workflows/ci.yml                      # 변경: publish-check, package 잡
Directory.Build.props                         # 변경: Version 0.9.0
src/PellyCoder.Cli/ShutdownCoordinator.cs, Program.cs   # 변경: Ctrl+C 일시 정지
src/PellyCoder.Core/Llm/QwenStreamClient.cs   # 변경: 대기 시간 미보고 처리
src/PellyCoder.Core/Testing/TestRunner.cs     # 변경: 멀티 타깃 --framework
src/PellyCoder.Core/Verification/TestStep.cs  # 변경: 누락 테스트 규칙 조건
src/PellyCoder.Core/Doctor/OfflineSelfTest.cs, src/PellyCoder.Core/Sql/SqlServerExecutor.cs   # 변경: SqlClient 행
eval/benchmark/judges/Modern/Shop.Api.JudgeTests/       # 신규: 판정 테스트 프로젝트
eval/benchmark/tasks/*.yaml, README.md        # 변경: 50건, judge.failingTests
eval/PellyCoder.Eval/*                        # 변경: 판정 테스트 실행, 반복 실행·비교
eval/corpus/Sql/deploy.sh                     # 변경: 재실행 가능
deploy/install.md, usage.md, ide-external-tools.md, no-network.md, pelly.json.template   # 신규
deploy/record-versions.sh, version-record.json, build-package.ps1, package-files.txt     # 신규
tests/…                                       # 각 작업 테스트
docs/spikes/S6-single-file.md, docs/m3b-baseline.md, docs/m3b-tuning.md, docs/m3b-s7-rider.md   # 신규
```

---

### Task 1: M3a 이월 ① — Ctrl+C 일시 정지·대기 시간 미보고·`deploy.sh`·PRD 표기

**Files:**
- Modify: `src/PellyCoder.Cli/ShutdownCoordinator.cs`, `src/PellyCoder.Cli/Program.cs`, `src/PellyCoder.Core/Llm/QwenStreamClient.cs`, `eval/corpus/Sql/deploy.sh`, `docs/PRD.md`(62행)
- Test: `tests/PellyCoder.Cli.Tests/ShutdownCoordinatorTests.cs`, `ExitPauseTests.cs`, `tests/PellyCoder.Core.Tests/Llm/QwenStreamClientTests.cs`

**Interfaces:**
- Produces:
  * `ShutdownCoordinator.TerminationRequested`(bool)
    * SIGTERM·SIGHUP·Windows CTRL_CLOSE로 중단됐을 때만 참이고, Ctrl+C(`RequestCancel`)는 거짓이다.
    * `Program`은 `TerminationRequested`가 거짓이면 토큰이 취소돼 있어도 일시 정지한다.
    * 대기 중 두 번째 Ctrl+C·종료 신호가 오면 바로 끝낸다.
  * 대기 시간 계산(R3 보완): 서버가 `load_duration`과 `prompt_eval_duration`을 **둘 다** 보고하지 않으면 `Queue = 0`이고 TTFT를 그대로 둔다.
  * `deploy.sh`
    * 복사 전에 컨테이너 안 `/tmp/corpus-sql`을 `docker exec -u root … rm -rf`로 지워 다시 실행할 수 있게 한다.
    * 이것은 코퍼스 배포 도구라서 고칠 수 있다. 코퍼스 내용(SQL 파일)은 바꾸지 않는다.
  * PRD 62행: `50건(M3)` → `50건(M3b)`.
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] CtrlC_StillPauses()                   // RequestCancel 후 ShouldPause 판단 + Wait가 키를 기다림
  [Fact] TerminationSignal_SkipsPause()        // RequestShutdownAndWait 경로 → TerminationRequested true → 대기 없음
  [Fact] SecondCtrlCDuringPause_EndsWait()
  [Fact] NoDurationsReported_QueueIsZero_TtftKept()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Cli.Tests` 그리고 `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Llm*"`
  * `deploy.sh`는 두 번 연속 실행해 두 번 다 종료 0인지 확인한다.
- [ ] **Step 5: Commit** — `git commit -m "fix: pause after Ctrl+C, zero queue without server durations, re-runnable SQL deploy"`

---

### Task 2: M3a 이월 ② — 테스트 실행기 보완·VSTest 종단 확인

**Files:**
- Modify: `src/PellyCoder.Core/Testing/TestRunner.cs`, `src/PellyCoder.Core/Verification/TestStep.cs`
- Test: `tests/PellyCoder.Core.Tests/Testing/TestRunnerTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/TestFlowTests.cs`, `tests/PellyCoder.Integration.Tests/Testing/TestRunnerVstestTests.cs`

**Interfaces:**
- Produces:
  * **멀티 타깃 처리:** 테스트 프로젝트에 `TargetFrameworks`가 여럿이면, `dotnet run` 경로에 `--framework <첫 번째 TFM>`을 붙인다(csproj와 가장 가까운 `Directory.Build.props`에서 읽음).
    * VSTest 경로(`dotnet test`)는 그대로 둔다.
    * 첫 TFM으로 실행할 수 없으면 `TestRun.Error = "멀티 타깃 테스트 프로젝트를 실행할 수 없습니다 ({tfm})"`.
  * **누락 테스트 규칙:** "원본에서 통과했는데 지금 결과에 없는 테스트 = 실패"는 `before.ExitCode == 0 && run.ExitCode != 0`일 때만 적용한다.
    * 원본이 이미 실패하던 프로젝트에서는 누락만으로 실패시키지 않는다.
    * 원본에서 통과했는데 지금 이름이 붙은 실패는 그대로 실패다.
  * **VSTest 종단 확인:** `[Fact]` 통합 테스트가 임시 폴더(저장소 밖이라 CPM 미적용)에 두 가지를 만든다.
    * xunit v2 + `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio` 테스트 프로젝트
    * 대상 클래스 라이브러리

    이 프로젝트를 restore한 뒤 `TestRunner`로 실행해, 테스트별 결과(통과 2·실패 1)를 TRX에서 읽는지 확인한다.
    * 패키지 버전은 이 테스트 안에 상수로 고정한다: xunit 2.9.x, Test.Sdk 17.x, runner 2.8.x. 작성 시점의 정확한 최신 안정판을 확인해 적는다.
    * CI 두 OS의 unit 잡에서 돈다.
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] MultiTarget_DotnetRunGetsFirstFramework()
  [Fact] MissingTest_BaselineAlreadyFailing_NotCounted()
  [Fact] MissingTest_BaselinePassedExit0_NowExit1_Counted()      // 기존 동작 유지
  [Fact] Vstest_RealProject_PerTestResults()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Testing*" --filter-namespace "*Fsm*"`, Integration `*Testing*`
- [ ] **Step 5: Commit** — `git commit -m "fix(testing): multi-target framework, gate missing-test rule on a passing baseline, VSTest end-to-end"`

---

### Task 3: 판정 테스트 기반 — 프로젝트·`judge.failingTests`·실행기 연동

**Files:**
- Create: `eval/benchmark/judges/Modern/Shop.Api.JudgeTests/Shop.Api.JudgeTests.csproj`, `JudgeFakes.cs`, `README.md`
- Modify: `eval/PellyCoder.Eval/BenchmarkTask.cs`, `IndependentVerifier.cs`, `TaskJudge.cs`, `WorkCopy.cs`, `eval/benchmark/README.md`
- Test: `tests/PellyCoder.Eval.Tests/*`, `tests/PellyCoder.Integration.Tests/Benchmark/JudgeTestDefinitionTests.cs`

**Interfaces:**
- Consumes: `TestRunner`(M3a T8), 실행기 `IIndependentVerifier`(M3a T13)
- Produces:
  * **판정 프로젝트:** `Shop.Api.JudgeTests.csproj`
    * xunit v3. 코퍼스 `Shop.Api.Tests`와 같은 패키지 구성이다.
    * 참조 `<ProjectReference Include="$(JudgeTargetRoot)/Shop.Api/Shop.Api.csproj" />`. `JudgeTargetRoot`의 기본값은 `$(MSBuildThisFileDirectory)../../../corpus/Modern`이다.
    * 솔루션(`PellyCoder.sln`·코퍼스 sln)에는 넣지 않는다.
    * 저장소 CPM의 영향을 받지 않도록 자체 `Directory.Build.props`/`Directory.Packages.props`를 둔다. 코퍼스와 같은 방식이다.
  * **`BenchmarkTask.Judge`:** `List<string>? FailingTests`를 추가한다(YAML `failingTests`). README 스키마 표에도 더한다.
  * **실행기 연동:** 종료 0 작업에 `Tests` 또는 `FailingTests`가 있으면 `IndependentVerifier`가 다음을 한다.
    1. 판정 프로젝트를 작업 복사본 옆 `work/<id>/judges/Modern/Shop.Api.JudgeTests`로 복사한다.
    2. `-p:JudgeTargetRoot=<작업 복사본>/Modern`으로 빌드한다.
    3. `TestRunner`로 실행한다.
    * `Tests`의 모든 이름은 TRX에서 Passed여야 한다.
    * `FailingTests`는 코퍼스 `Shop.Api.Tests`를 같은 방식으로 실행해 TRX에서 Failed여야 한다.
    * 이름이 TRX에 없으면 Failed로 본다.
    * 기존의 문자열·빌드 판정과 함께 적용한다.
  * **정의 테스트(`[Fact]`, CI unit 잡):** 원본 코퍼스 복사본에 대해 판정 프로젝트와 코퍼스 테스트를 실행한다. 모든 작업 정의의 `Tests`는 **원본에서 실패**, `FailingTests`는 **원본에서 통과**해야 한다(Review Focus 1).
  * `JudgeFakes.cs`: `IOrderRepository`·`ILogger` 가짜 구현. 코퍼스 `Shop.Api.Tests`의 방식을 따른다. 판정 테스트들이 같이 쓴다.
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] Judge_TestsPassAndFailingTestsFail_Passed()   [Fact] Judge_ListedTestMissingFromTrx_Failed()
  [Fact] Judge_FailingTestStillPasses_Failed()          [Fact] Copy_JudgeProjectNextToWork_TargetRootPassed()
  [Fact] JudgeTests_FailOnOriginal_And_FailingTestsPassOnOriginal()   // Integration, 작업 정의를 읽어 원본에서 실행
  ```
  * 이 단계에서 판정 테스트는 기존 Modern 8건 중 2건(cs-modern-001 CancelAsync 배송 중 예외, cs-modern-002 환불 반올림)에 대해서만 쓴다.
  * 나머지는 Task 4에서 쓴다.
  * cs-modern-001: `judge.failingTests: ["Shop.Api.Tests.OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels"]`를 더한다.
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Eval.Tests`, `dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*Benchmark*"`
  * 실행기를 실제로 `--tasks cs-modern-001,cs-modern-002`로 1회 돌려 판정 테스트가 실행됐는지 확인한다(보고서에 판정 테스트 결과가 나타나야 함).
- [ ] **Step 5: Commit** — `git commit -m "feat(eval): behaviour judges via an out-of-corpus judge test project and judge.failingTests"`

---

### Task 4: 벤치마크 50건

**Files:**
- Create: `eval/benchmark/tasks/` 신규 20건, 판정 테스트 클래스(`eval/benchmark/judges/Modern/Shop.Api.JudgeTests/*Tests.cs`)
- Modify: 기존 Modern 작업 YAML(판정 테스트 추가), `eval/benchmark/README.md`, `tests/PellyCoder.Integration.Tests/Benchmark/BenchmarkDefinitionTests.cs`

- [ ] **Step 1: 구성 테스트 갱신** — `Composition_Is30Cs20Sql_With18Legacy`
  * 개수: cs 30(legacy 18, modern 12), sql 20
  * 범주:
    * legacy: MVC 컨트롤러·서비스 8 / ADO.NET 배치 5 / Dapper 리포지토리 5
    * 범주는 YAML에 `category` 필드(선택)로 표시하고, 테스트가 센다.
  * 지정 방식: `line` 지정 3건 이상, `object` 지정 3건 이상
  * Modern 12건은 모두 `judge.tests` 또는 `judge.failingTests`를 가진다(R1).
- [ ] **Step 2: 작업 작성** (설계 §10.6, PRD §8.1 요청 종류)
  * 새 작업: Legacy 8, Modern 4, SQL 8
    * Modern 새 작업은 동작 판정을 전제로 고른다. 그래야 문자열로는 못 잡는 의미 오류를 잡는다.
    * 예: 경계값, null 입력, 조건 결합, Dapper 쿼리 매개변수
  * 기존 Modern 6건(001·002 외)에도 판정 테스트를 쓴다.
  * 판정 테스트 이름 규칙: `<작업 id 밑줄 표기>_<기대 동작>`(예: `cs_modern_003_GiftItemsExcludedFromItems`)
  * 시그니처를 바꾸는 요청은 넣지 않는다.
  * 원본에서 실패하는지는 Task 3의 정의 테스트가 강제한다.
  * 각 판정 테스트는 요청이 옳게 수행됐을 때만 통과해야 한다. 과하게 막는 수정도 걸러야 한다.
    * 예: 요청이 "배송 중 예외"면, 결제 완료 주문은 여전히 취소되는지 함께 확인한다(M3a 최종 리뷰의 과잉 차단 지적).
- [ ] **Step 3: 정의 테스트 통과** — `dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*Benchmark*"`
- [ ] **Step 4: README 갱신** — 구성 표 50건, `failingTests`, 판정 프로젝트, 범주 표
- [ ] **Step 5: Commit** — `git commit -m "feat(eval): extend benchmark to 50 tasks with behaviour judges for all modern tasks"`

---

### Task 5: 반복 측정과 기준선

**Files:**
- Modify: `eval/PellyCoder.Eval/EvalArguments.cs`, `Program.cs`, `KpiReport.cs`(반복 요약), 새 `RepeatSummary.cs`
- Create: `docs/m3b-baseline.md`
- Test: `tests/PellyCoder.Eval.Tests/RepeatSummaryTests.cs`

**Interfaces:**
- Produces:
  * `--repeat <N>`(기본 1): 같은 작업 집합을 N회 실행하고, 회차별 결과를 `run-<k>/`에 둔다.
  * 상위 `summary.md`·`summary.json`에 넣는 것:
    * 작업별 성공 횟수(예: `2/3`)
    * 범주별 평균·최소·최대
    * 전체 평균 성공 수와 표준편차
    * 회차별 검증 불변 조건 위반 수
    * 시간 중위값
  * `static class RepeatSummary { RepeatSummaryData Build(IReadOnlyList<IReadOnlyList<TaskResult>> runs); }`
- [ ] **Step 1~4: 테스트(RED) → 구현 → 통과**
  ```csharp
  [Fact] Summary_PerTaskCountsAndCategoryMeans()  [Fact] Summary_StdDevAndInvariantPerRun()  [Fact] Repeat_WritesRunFolders()
  ```
- [ ] **Step 5: 기준선 측정** — 현재 기본값(프롬프트 `cs-v2`·`sql-v1`, 샘플링 기본, think 512)으로 50건 × 3회를 실행한다.
  * `docs/m3b-baseline.md`에 적을 것:
    * 환경, KPI 표, 작업별 3회 결과
    * 범주별 평균·표준편차
    * 불안정 작업 목록(1/3, 2/3)
    * 실패 유형 분포
  * 검증 불변 조건 위반이 있으면 BLOCKED로 보고한다.
- [ ] **Step 6: Commit** — `git commit -m "feat(eval): repeated runs with per-task and category summary; record M3b baseline"`

---

### Task 6: 프롬프트·샘플링 튜닝

**Files:**
- Create: `eval/PellyCoder.Eval/Acceptance.cs`, `docs/m3b-tuning.md`
- Modify(채택된 후보에 따라): `src/PellyCoder.Core/Llm/Resources/prompts/*.txt`(버전 올림), `src/PellyCoder.Core/Config/PellyOptions.cs`(기본값), 설계 §8.1·PRD §7.2
- Test: `tests/PellyCoder.Eval.Tests/AcceptanceTests.cs`

**Interfaces:**
- Produces:
  * `static class Acceptance { AcceptanceResult Compare(RepeatSummaryData baseline, RepeatSummaryData candidate); }`
    * `sealed record AcceptanceResult(bool Accepted, IReadOnlyList<string> Reasons)`
    * R4 규칙을 그대로 구현한다.
  * `dotnet run --project eval/PellyCoder.Eval -- compare <기준선 summary.json> <후보 summary.json>`: 판정과 사유를 출력한다.
- **후보 목록 (R5, 이 순서로, 최대 4개):**
  1. **모순 요청 회피 줄이기:** `system.cs.txt` 규칙에 "FEEDBACK이 테스트 실패를 알려도 REQUEST가 요구한 동작을 없애거나 주석 처리하지 말 것"을 한 줄 추가한다(cs-v3).
  2. **think 잘림 줄이기:** 교정 시도의 think 예산 `budget.think` 512 → 768(`inputTotal`+`think`+`codeOutput` ≤ `numCtx` 검증 유지). M2a 측정(정답률 동일, 시간 2배)을 다시 확인하는 셈이다.
  3. **샘플링:** code `temperature` 0.1 → 0.2(`topP` 0.95). S3의 동일 결과를 50건으로 다시 확인한다.
  4. **예시 보강:** `fewshot.cs.txt`에 `[READ-ONLY: dto]`가 있는 입력 예시를 쓴다(DTO 슬라이스 도입 뒤 예시가 그 구간을 보여 주지 않음).
- [ ] **Step 1~4: `Acceptance` 테스트(RED) → 구현 → 통과**
  ```csharp
  [Fact] Acceptance_PlusOneOrMore_NoDrop_Accepted()
  [Fact] Acceptance_WithinNoise_IsRejected()          // +0.67 → 거절
  [Fact] Acceptance_CategoryDrop_IsRejected()         // 전체 +1.3이지만 sql −0.67 → 거절
  [Fact] Acceptance_InvariantViolation_IsRejected()   [Fact] Acceptance_TooSlow_IsRejected()
  ```
- [ ] **Step 5: 실험**
  * 후보마다 50건 × 3회를 실행하고 `compare`로 판정한다.
  * 후보는 서로 독립으로 평가한다(기준선 대비).
  * 채택된 후보가 둘 이상이면 함께 적용해 한 번 더 50건 × 3회를 재고, 그 조합도 R4를 통과해야 한다.
  * `docs/m3b-tuning.md`에 적을 것:
    * 후보별 요약표(평균·범주·시간·판정·사유)
    * 채택 여부와 그 근거
    * `--test` 모순 요청 회피율 재측정(M3a T14 방식으로 10회, 후보 1을 채택했을 때만)
- [ ] **Step 6: 채택 반영**
  * 프롬프트를 바꾸면 `# version: N`을 올린다(`cs-v3` 등).
  * 기본값을 바꾸면 `PellyOptions`, 설계 §8.1·§8.2, PRD §7.2를 갱신한다.
  * 아무것도 채택하지 않았으면 코드를 바꾸지 않는다.
  * 마지막 측정 결과가 M3b의 최종 KPI다.
- [ ] **Step 7: Commit** — `git commit -m "feat(tuning): acceptance rule and tuning experiments (adopted: …)"`

---

### Task 7: S6 단일 파일 배포본·SqlClient 자체 점검·`publish-check`

**Files:**
- Modify: `src/PellyCoder.Cli/PellyCoder.Cli.csproj`(publish 속성), `src/PellyCoder.Core/Doctor/OfflineSelfTest.cs`, `src/PellyCoder.Core/Sql/SqlServerExecutor.cs`, `.github/workflows/ci.yml`, `Directory.Build.props`(Version 0.9.0)
- Create: `docs/spikes/S6-single-file.md`
- Test: `tests/PellyCoder.Core.Tests/Doctor/OfflineSelfTestTests.cs`

**Interfaces:**
- Produces:
  * **publish 명령(PRD §11.1):** `dotnet publish src/PellyCoder.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=none -o artifacts/publish/win-x64`
    * 트리밍은 쓰지 않는다.
    * 필요한 추가 속성은 S6 측정으로 정해 csproj의 `Condition="'$(RuntimeIdentifier)'=='win-x64'"` 그룹에 둔다.
  * **자체 점검 SqlClient 행:** `OfflineSelfTest`에 `SqlClient` 행을 추가한다.
    * Core에 `SqlServerExecutor.SelfTestNativeLayer()`를 둔다. 정적이며 SqlClient는 이 클래스 안에서만 쓴다.
    * Windows: `Data Source=np:\\.\pipe\pelly-selftest-none\sql\query;Connect Timeout=1;`에 `Open`해서 `SqlException`이 나면 Ok다. 네이티브 SNI가 로드됐다는 뜻이고, 로컬 파이프만 쓰므로 네트워크가 없다. `DllNotFoundException`·`TypeInitializationException`이면 Fail.
    * Windows가 아니면 `SqlConnection` 객체 생성만 하고 Ok "관리 코드 SNI".
    * 다섯 행 → 여섯 행.
  * **CI 잡 `publish-check`(`windows-latest`):**
    1. publish
    2. `artifacts/publish/win-x64/pelly.exe --version` 출력이 `0.9.0`으로 시작
    3. `pelly.exe doctor --offline-selftest` 종료 0
    4. `pelly.exe cs --help` 종료 0
    5. 결과 exe 크기를 로그에 남김
  * `docs/spikes/S6-single-file.md`: 설정, exe 크기, 첫 실행 시간(압축 해제), 자체 점검 결과, 문제와 해결.
- [ ] **Step 1~4: 테스트(RED) → 구현 → 통과**
  ```csharp
  [Fact] SelfTest_SqlClientRow_LoadsNativeLayer()      // 이 OS에서 Ok
  [Fact] SelfTest_HasSixRowsInOrder()
  ```
  * 로컬 Mac에서 `osx-arm64` 단일 파일로도 publish해 자체 점검을 1회 실행한다(참고 기록).
- [ ] **Step 5: CI 확인** — push 후 `publish-check` 녹색. 실패하면 S6 문서에 원인과 해결을 적는다.
- [ ] **Step 6: Commit** — `git commit -m "feat(publish): win-x64 single-file publish, SqlClient self-test row, publish-check job (S6)"`

---

### Task 8: 실행 중 외부 통신 없음 — 정책 테스트와 명세

**Files:**
- Create: `tests/PellyCoder.Core.Tests/Policy/NetworkPolicyTests.cs`, `deploy/no-network.md`

**Interfaces:**
- Produces:
  * **소스 정책 테스트:** `src/` 아래 `*.cs`를 훑어 아래 형식은 지정 파일에서만 나와야 한다.

    | 형식 (생성 지점만 본다) | 허용 파일 |
    | :--- | :--- |
    | `new HttpClient(`, `SocketsHttpHandler` | `src/PellyCoder.Cli/Composition.cs` |
    | `new SqlConnection(` | `src/PellyCoder.Core/Sql/SqlServerExecutor.cs` |
    | `TcpClient`, `UdpClient`, `new Socket(`, `WebRequest`, `WebClient`, `Dns.` | 어디에도 없어야 함 |
    | 프로세스 생성·시작(`System.Diagnostics.Process`) | `src/PellyCoder.Core/Processes/SystemProcessRunner.cs` |

    * 계획 작성 시점(d39e3c1) 실측: `new HttpClient(`·`SocketsHttpHandler`는 `Composition.cs:40-41`에만, `new SqlConnection(`은 `SqlServerExecutor.cs:136`에만 있다.
    * 프로세스 생성의 실제 표기는 구현자가 `SystemProcessRunner.cs`에서 먼저 확인해 패턴으로 정한다.
    * 형식을 **참조**만 하는 것(주입받은 `HttpClient`, `SqlConnectionStringBuilder`)은 생성이 아니므로 검사하지 않는다.
    * Task 7의 자체 점검용 `SqlConnection` 생성은 같은 파일에 있으므로 통과해야 한다.
    * 위반 시 파일·줄을 출력한다.
  * **업데이트 확인·텔레메트리 없음:** 패키지 참조 목록(`Directory.Packages.props`)에 `ApplicationInsights`, `OpenTelemetry`, `Sentry`가 없음을 테스트한다.
  * **`deploy/no-network.md`(한국어, 보안 심사용):**
    * 접속 대상: 설정된 Ollama 하나, SQL 서버 하나
    * 프록시 동작(`HTTP_PROXY`/`NO_PROXY` 안내)
    * 이 정책 테스트가 무엇을 막는지
    * 확인 방법: 실행 중 `netstat`/Windows 리소스 모니터로 확인하는 절차
- [ ] **Step 1~4: 테스트 → 위반이 있으면 고침 → 통과**
- [ ] **Step 5: Commit** — `git commit -m "test(policy): network and process creation only in composition, SQL executor and process runner; no-network statement"`

---

### Task 9: 반입 패키지

**Files:**
- Create: `.config/dotnet-tools.json`, `deploy/build-package.ps1`, `deploy/package-files.txt`, `deploy/pelly.json.template`, `deploy/record-versions.sh`, `deploy/version-record.json`, `tests/PellyCoder.Integration.Tests/Package/PackageTests.cs`
- Modify: `.github/workflows/ci.yml`(`package` 잡)

**Interfaces:**
- Consumes: Task 7 publish 결과, Task 8 문서, Task 10 문서
- Produces:
  * **`deploy/build-package.ps1 -PublishDir <폴더> -OutDir <폴더>`**(pwsh 7)
    1. `dotnet tool restore` → CycloneDX로 `sbom.cdx.json`을 만든다(Cli 프로젝트 기준, 간접 의존 포함).
    2. SBOM에서 `THIRD-PARTY-NOTICES.md`(패키지·버전·라이선스 표)를 만든다. MIT·Apache-2.0·BSD-2/3-Clause가 아닌 라이선스가 있으면 실패한다(R7).
    3. `package-files.txt` 허용 목록대로 스테이징 폴더를 만든다. 목록 밖 파일은 넣지 않는다.
    4. 모든 파일의 SHA-256을 `SHA256SUMS.txt`로 쓴다(`<해시>  <상대경로>` 형식).
    5. `pelly-<Version>-win-x64.zip`을 만든다.
  * **`deploy/package-files.txt`**(zip 안 경로 → 출처):
    * `pelly.exe`
    * `pelly.json.template`
    * `docs/install.md`, `docs/usage.md`, `docs/ide-external-tools.md`, `docs/server-checklist.md`, `docs/no-network.md`
    * `sbom.cdx.json`, `THIRD-PARTY-NOTICES.md`
    * `LICENSE`는 넣지 않는다. 저장소에 라이선스 파일이 없고(d39e3c1 실측) pelly 자체의 라이선스는 사람이 정할 일이다. 정해지면 이 목록에 한 줄 더한다.
    * `version-record.json`, `benchmark-summary.md`(Task 6 최종 KPI), `SHA256SUMS.txt`
  * **`deploy/pelly.json.template`**
    * 내용: `llm.endpoint`(예시 `http://ollama.internal:11434`), `llm.model`, `sql.allowedHosts`/`allowedDatabases`(빈 배열 + 주석 대신 README 안내), `sql.connectionString`(`"Server=<서버>;Database=<DB>;Integrated Security=true;TrustServerCertificate=True"` 자리 표시자)
    * 비밀번호 키 없음
  * **`deploy/record-versions.sh`**
    * 로컬 Ollama에서 `/api/version`과 `/api/show {"model": …}`의 `modelfile` FROM 줄의 `sha256-<hex>`를 읽는다.
    * `{ "pellyVersion", "ollamaVersion", "model", "modelGgufSha256", "recordedAt" }`을 `deploy/version-record.json`에 쓴다.
    * 이 작업에서 한 번 실행해 커밋한다.
  * **CI 잡 `package`(`windows-latest`, `publish-check` 뒤 같은 잡 또는 needs):** publish → `build-package.ps1` → zip을 아티팩트 `pelly-package`로 올린다(보존 기간 14일).
  * **패키지 테스트**(`[WindowsFact]`, windows-integration 또는 package 잡에서): zip을 풀어 다음을 확인한다.
    * 파일 목록이 허용 목록과 정확히 같다.
    * `SHA256SUMS.txt`가 모든 파일과 맞는다.
    * 템플릿에 `Password`/`Pwd`·`localhost`·사용자 이름·`/Users/`·`C:\Users\`가 없다.
    * pdb가 없다.
    * `pelly.exe doctor --offline-selftest` 종료 0이다.
- [ ] **Step 1~4: 테스트(RED: 스크립트 없음) → 구현 → 통과**
  ```csharp
  [WindowsFact] Package_ContainsExactlyAllowedFiles()   [WindowsFact] Package_ChecksumsMatch()
  [WindowsFact] Template_HasNoSecretsOrRealHosts()      [WindowsFact] Pdb_NotShipped()
  [WindowsFact] Packaged_SelfTest_Exit0()
  ```
  * 로컬 Mac에서는 `pwsh`가 있으면 `osx-arm64` publish 결과로 스크립트를 1회 돌려 zip 구성을 확인한다(참고).
- [ ] **Step 5: CI 확인** — `package` 잡 녹색, 아티팩트에 zip이 있다.
- [ ] **Step 6: Commit** — `git commit -m "feat(package): air-gap package with SBOM, notices, checksums, version record and template"`

---

### Task 10: `deploy/` 문서 — 설치·사용·IDE External Tools

**Files:**
- Create: `deploy/install.md`, `deploy/usage.md`, `deploy/ide-external-tools.md`
- Modify: `deploy/server-checklist.md`(필요하면), `README.md`(링크)

**Interfaces:**
- Produces(모두 한국어, 폐쇄망 사용자 대상):
  * **`install.md`(PRD §11.2):**
    * zip 풀기 → `PATH` 추가 → `SHA256SUMS.txt` 확인(PowerShell `Get-FileHash` 명령 예)
    * `pelly.json.template`을 `%APPDATA%\pelly\config.json` 또는 작업 공간 `pelly.json`으로 복사·편집
    * `pelly doctor`로 확인
    * 대안: `dotnet tool install` 로컬 피드(.nupkg를 패키지에 넣지 않았다면 이 대안은 "M4에서 결정"으로 적는다)
  * **`usage.md`:**
    * `pelly cs`/`sql`의 대상 지정, `--test`, `--dry-run`, `--yes`, `--verbose`
    * 종료 코드표, `pelly report --sanitize`로 반출 자료 만들기
    * 알려진 한계: 인라인 SQL 미검증, 모순 요청 회피(Task 6 재측정값), Windows 미확인 항목
  * **`ide-external-tools.md`(설계 §8.14 표 그대로):**
    * Visual Studio·Rider 설정
    * 실행 전 저장
    * `--pause-on-exit`
    * SQL용 항목
    * 한글 입력 확인
    * "Rider 실기 확인 결과" 절: 비워 두고 Task 11에서 채운다.
- [ ] **Step 1: 문서 작성** — 명령과 옵션은 실제 CLI `--help` 출력과 대조한다.
- [ ] **Step 2: 검증** — 문서 안의 명령을 Mac에서 실행할 수 있는 것만 골라 실제로 실행해 본다(`pelly doctor`, `pelly cs --help` 등).
- [ ] **Step 3: Commit** — `git commit -m "docs(deploy): install, usage and IDE External Tools guides"`

---

### Task 11: S7 Rider 실기 확인 (사용자와 함께)

**Files:**
- Create: `docs/m3b-s7-rider.md`
- Modify: `deploy/ide-external-tools.md`("Rider 실기 확인 결과" 절)

이 작업은 사람이 Rider에서 직접 확인해야 한다. 구현자는 사람이 따라 할 수 있는 확인표와 준비물만 만들고, 결과 기입은 사람이 확인한 뒤 컨트롤러가 받아 적는다.

- [ ] **Step 1: 확인표 작성** — `docs/m3b-s7-rider.md`
  * 준비:
    * Mac용 `pelly` 실행 파일 경로(`dotnet publish -r osx-arm64` 결과)
    * 코퍼스 복사본 경로
    * Rider External Tools 설정값
  * 확인 항목(각각 기대 결과와 기입 칸):
    1. `--line` 실행 시 커서 메서드가 대상이 됨
    2. 요청문 생략 시 한글 입력 가능
    3. 출력 한글 깨짐 없음
    4. `--pause-on-exit`로 창이 남음
    5. Ctrl+C 후에도 창이 남음(Task 1)
    6. 승인 후 Rider가 파일을 다시 읽음
    7. SQL 항목 `--line`
- [ ] **Step 2: 준비물 생성** — Mac용 publish, 코퍼스 복사본, 설정값 문서를 만든다.
- [ ] **Step 3: 사용자 확인** — 컨트롤러가 브랜치 마무리 때 사용자에게 확인표를 건넨다. 결과를 받으면 `ide-external-tools.md`와 `m3b-s7-rider.md`에 기입하고 커밋한다.
  * 실패 항목은 결함으로 기록한다. 이 계획 안에서 고칠지는 사용자가 정한다.
- [ ] **Step 4: Commit(준비분)** — `git commit -m "docs(s7): Rider External Tools checklist and setup"`

---

### Task 12: 문서 갱신

**Files:**
- Modify: `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md`, `docs/PRD.md`, `README.md`

- [ ] **Step 1: 설계 문서**
  * §9.1: S6 결과(`docs/spikes/S6-single-file.md`), S7 결과(채워졌다면)
  * §10.4: `publish-check`·`package` 잡
  * §10.6: 판정 프로젝트, `failingTests`, 50건 구성, `--repeat`, `compare`, R4 채택 규칙
  * §8.13: SqlClient 행
  * §8.1·§8.2: 튜닝 결과 반영
  * §11 M3b 행: 실제 결과
  * 각 변경 옆에 근거(M3b Tn·R n)를 적는다.
- [ ] **Step 2: PRD**
  * §8.1: 50건 결과, §8.2 회귀 차단 절차
  * §11: 실제 패키지 구성, 버전 0.9.0
  * 변경 이력 v1.10
- [ ] **Step 3: README** — 패키지 만들기, 벤치마크 반복 실행·비교, `deploy/` 문서 링크
- [ ] **Step 4: 검증·Commit** — `git diff --stat`으로 문서만 바뀐 것을 확인한다. `git commit -m "docs: reflect M3b benchmark, tuning and air-gap package"`

---

## M3b 완료 기준

* 외부 벤치마크 50건이 있다. Modern 12건은 동작으로 판정한다.
* 3회 반복 기준선과 튜닝 결과(채택·기각 근거 포함)의 KPI 표가 있다. 검증 불변 조건 위반은 0건이다.
* CI `publish-check` 잡이 녹색이다: 배포본 exe로 자체 점검 6행이 모두 Ok.
* CI `package` 잡이 `pelly-0.9.0-win-x64.zip`을 만든다. 허용 목록, 체크섬, SBOM, 라이선스 표, 버전 기록이 들어 있다.
* `deploy/` 설치·사용·IDE·외부 통신 없음 문서가 있다.
* S7 확인표가 준비돼 있다. 사용자 확인 결과는 받은 만큼 기입한다.
* CI 전 잡 녹색, `dotnet test` 실패 0.

## M4로 넘기는 것

* 폐쇄망 반입·설치, `pelly doctor`로 현장 점검
* 폐쇄망 실제 코드 기반 벤치마크와 KPI 최종 판정
* Visual Studio External Tools 실기 확인
* 폐쇄망 서버와 같은 모델로 S1·S2·S4를 다시 확인
* 버전 1.0.0
