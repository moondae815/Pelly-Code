# Pelly-Coder M0 (기반·스파이크) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 솔루션 골격·CI·샘플 코퍼스·벤치마크 초안을 만들고, M1 설계값을 확정하는 스파이크 S1~S5를 수행한다.

**Architecture:** `.NET 10` 솔루션(`PellyCoder.Core` 라이브러리 + `PellyCoder.Cli` exe + 테스트 2개)을 만들고, 이후 모든 마일스톤이 쓰는 테스트 분류(Env Trait)와 CI를 먼저 깐다. 코퍼스(Modern/Legacy/Sql)는 벤치마크·통합 테스트용 데이터다. 스파이크는 `spikes/`의 일회성 코드로 실험하고 결과만 `docs/spikes/`에 남긴다.

**Tech Stack:** .NET 10 SDK 10.0.302, Spectre.Console.Cli, xUnit v3, YamlDotNet(테스트 전용), GitHub Actions, Docker(SQL Server 2022), Ollama 0.40.0

**Spec:** `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md` (요구사항 원본: `docs/PRD.md` v1.5)

**이 계획의 범위:** 설계 §11의 M0 행. M1~M3 계획은 이 계획의 Task 13(스파이크 결과 반영)이 끝난 뒤 별도로 작성한다.

## Global Constraints

* SDK는 `global.json`으로 `10.0.302` 고정(`rollForward: latestFeature`). 제품 프로젝트(`src/`)는 모두 `net10.0`.
* 모든 프로젝트: `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (`Directory.Build.props`). 코퍼스(`eval/corpus/`)는 이 설정을 상속하지 않는다.
* 패키지 버전은 `Directory.Packages.props`(Central Package Management)에서만 관리. 라이선스는 MIT 또는 Apache-2.0만.
* `Core`는 콘솔에 직접 쓰지 않는다(설계 D20). `Console.*` 호출은 `PellyCoder.Cli`에만.
* 스파이크 코드는 `spikes/` 아래에만 두고 `src/`·`tests/`에서 참조하지 않는다. 솔루션 파일에도 넣지 않는다.
* 스파이크 대상 모델: 환경 변수 `PELLY_SPIKE_MODEL`, 기본값 `qwen3.6:35b-a3b-coding-nvfp4`. 사용자가 폐쇄망과 같은 모델을 받으면 S1·S2를 다시 실행한다(설계 §13).
* 솔루션 파일은 `.sln` 형식(`dotnet new sln --format sln`) — Visual Studio 2022 호환.
* 코드 식별자·커밋 메시지는 영어, 문서(`docs/`, `README`)는 한국어.
* 모든 커밋 메시지 끝에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
* **선행 조건 (Task 3 전):** 사용자가 GitHub 저장소(비공개 권장)를 만들고 `origin` remote를 추가해야 한다. 없으면 Task 3에서 멈추고 사용자에게 요청한다. 저장소 생성·push는 사용자 확인 후에만 한다.

## Review Focus

1. **CP949·CRLF 코퍼스 파일이 도구에 의해 조용히 UTF-8·LF로 바뀌는 경우** — 에디터·Write 도구·git(`core.autocrlf`, Windows CI)가 바꿀 수 있다. `.gitattributes`로 막고, Task 5의 `CorpusInvariantTests`가 바이트 수준으로 검증한다.
2. **스파이크 측정값이 모델 콜드 로드나 이전 요청의 접두사 캐시에 오염되는 경우** — S2·S4는 워밍업 요청을 먼저 보내고, 비교 대상 요청마다 고유 nonce로 캐시를 끊는다(S2의 2단계 요청만 예외). 각 스파이크 문서에 측정 조건을 적는다.
3. **`windows-latest` 러너에 .NET Framework 4.5 타겟팅 팩이 없어 레거시 빌드가 실패하는 경우** — Task 3의 CI가 참조 어셈블리 설치 단계를 두고, 실패 시 어떤 프로젝트·타겟인지 로그로 드러나야 한다.
4. **Apple Silicon Docker에서 SQL Server 2022(amd64) 컨테이너가 뜨지 않는 경우** — Task 6에서 `--platform linux/amd64`로 실행하고, 실패 시 오류를 `docs/spikes/S5-sql-noexec.md`에 남기고 사용자에게 알린다(대체 수단을 임의로 고르지 않는다).
5. **벤치마크 YAML이 코퍼스에 없는 파일·메서드·객체를 가리키는 경우** — Task 7의 `BenchmarkDefinitionTests`가 모든 정의의 대상 존재 여부를 검증한다.

---

### Task 1: 솔루션 골격

**Files:**
- Create: `global.json`, `.gitignore`(`dotnet new gitignore`), `.gitattributes`, `Directory.Build.props`, `Directory.Packages.props`, `PellyCoder.sln`
- Create: `src/PellyCoder.Core/PellyCoder.Core.csproj`, `src/PellyCoder.Core/PellyInfo.cs`
- Create: `src/PellyCoder.Cli/PellyCoder.Cli.csproj`, `src/PellyCoder.Cli/Program.cs`
- Create: `tests/PellyCoder.Core.Tests/PellyCoder.Core.Tests.csproj`, `tests/PellyCoder.Core.Tests/PellyInfoTests.cs`
- Create: `tests/PellyCoder.Integration.Tests/PellyCoder.Integration.Tests.csproj`
- Create: `README.md` (프로젝트 한 줄 소개, 문서 링크, 빌드·테스트 명령)

**Interfaces:**
- Produces: `PellyCoder.Core.PellyInfo.Version : string` (어셈블리 InformationalVersion, `+` 뒤 커밋 해시 제거), `pelly --version` CLI 출력.

- [ ] **Step 1: 루트 설정 파일 작성**
  * `global.json`: `{"sdk":{"version":"10.0.302","rollForward":"latestFeature"}}`
  * `.gitattributes`:
    ```gitattributes
    * text=auto
    *.cs text eol=lf
    eval/corpus/Legacy/** -text
    eval/corpus/Sql/** -text
    tests/fixtures/** -text
    *.sln text eol=crlf
    ```
  * `Directory.Build.props`: Global Constraints의 세 속성 + `<LangVersion>latest</LangVersion>`, `<Version>0.1.0</Version>`. 단, `eval/corpus/Directory.Build.props`(빈 `<Project/>`)를 Task 4에서 만들어 상속을 끊는다.
  * `Directory.Packages.props`: `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`.

- [ ] **Step 2: 프로젝트 생성**
  ```bash
  dotnet new sln -n PellyCoder --format sln
  dotnet new classlib -o src/PellyCoder.Core -f net10.0
  dotnet new console  -o src/PellyCoder.Cli  -f net10.0
  dotnet new xunit3   -o tests/PellyCoder.Core.Tests -f net10.0
  dotnet new xunit3   -o tests/PellyCoder.Integration.Tests -f net10.0
  dotnet sln add src/*/*.csproj tests/*/*.csproj
  ```
  `xunit3` 템플릿이 없으면 `dotnet new install xunit.v3.templates` 후 재실행. 템플릿이 넣은 `Class1.cs`, `UnitTest1.cs` 삭제. 템플릿이 넣은 패키지 버전은 `Directory.Packages.props`로 옮긴다.
  * `PellyCoder.Cli.csproj`: `<AssemblyName>pelly</AssemblyName>`, `<PackAsTool>true</PackAsTool>`, `<ToolCommandName>pelly</ToolCommandName>`, `<PackageId>PellyCoder.Cli</PackageId>`, Core 프로젝트 참조, `Spectre.Console.Cli` 패키지.
  * Core.Tests → Core 참조. Integration.Tests → Core 참조.

- [ ] **Step 3: 실패하는 테스트 작성** (`tests/PellyCoder.Core.Tests/PellyInfoTests.cs`)
  ```csharp
  [Fact]
  public void Version_IsSemVerWithoutCommitHash()
  {
      Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", PellyInfo.Version);
  }
  ```

- [ ] **Step 4: 실패 확인**
  Run: `dotnet test tests/PellyCoder.Core.Tests`
  Expected: 컴파일 실패 (`PellyInfo` 없음)

- [ ] **Step 5: `public static class PellyInfo { public static string Version { get; } }` 구현** (`src/PellyCoder.Core/PellyInfo.cs`) — `AssemblyInformationalVersionAttribute`에서 읽고 `+` 이후 제거.

- [ ] **Step 6: `Program.cs` 작성** — `Spectre.Console.Cli`의 `CommandApp` 생성, `config.SetApplicationName("pelly")`, `config.SetApplicationVersion(PellyInfo.Version)`. 명령은 아직 없음.

- [ ] **Step 7: 검증**
  Run: `dotnet build -warnaserror && dotnet test && dotnet run --project src/PellyCoder.Cli -- --version`
  Expected: 빌드 경고 0, 테스트 1개 통과, 출력 `0.1.0`

- [ ] **Step 8: Commit**
  ```bash
  git add -A
  git commit -m "chore: scaffold solution, projects, and repo settings"
  ```

---

### Task 2: 환경별 테스트 분류 (Env Trait)

**Files:**
- Create: `tests/PellyCoder.Integration.Tests/Infrastructure/TestEnv.cs`
- Create: `tests/PellyCoder.Integration.Tests/Infrastructure/EnvFactAttributes.cs`
- Test: `tests/PellyCoder.Integration.Tests/Infrastructure/TestEnvTests.cs`

**Interfaces:**
- Produces:
  * `static class TestEnv { bool IsWindows; string? SqlConnection /* PELLY_TEST_SQL */; string? OllamaEndpoint /* PELLY_TEST_OLLAMA */; string? OllamaModel /* PELLY_TEST_MODEL */; }`
  * `[WindowsFact]`, `[SqlServerFact]`, `[OllamaFact]` — 조건이 없으면 Skip(사유 메시지 포함). 각각 xUnit Trait `Env=Windows|SqlServer|Ollama`를 단다(xUnit v3 `ITraitAttribute` 또는 `[Trait]` 병기 — 구현자가 선택하되 `--filter-trait "Env=Windows"`로 걸러져야 함).
  * 조건 판단 규칙: `SqlServerFact`는 `PELLY_TEST_SQL` 비어 있지 않음, `OllamaFact`는 `PELLY_TEST_OLLAMA`와 `PELLY_TEST_MODEL` 둘 다 비어 있지 않음.

- [ ] **Step 1: 실패하는 테스트 작성** (`TestEnvTests.cs`)
  ```csharp
  [Fact] public void SqlServer_IsUnavailable_WhenVariableEmpty()
      => Assert.False(TestEnv.IsAvailable(TestEnvKind.SqlServer, name => null));
  [Fact] public void Ollama_RequiresBothEndpointAndModel()
      => Assert.False(TestEnv.IsAvailable(TestEnvKind.Ollama,
             name => name == "PELLY_TEST_OLLAMA" ? "http://localhost:11434" : null));
  [Fact] public void Ollama_IsAvailable_WhenBothSet()
      => Assert.True(TestEnv.IsAvailable(TestEnvKind.Ollama, name => "x"));
  ```
  (`IsAvailable(TestEnvKind kind, Func<string, string?> getEnv)` — 테스트 가능하도록 환경 변수 조회를 주입받는 오버로드.)

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Integration.Tests` / Expected: 컴파일 실패

- [ ] **Step 3: `TestEnv`, `TestEnvKind { Windows, SqlServer, Ollama }`, 세 Fact 특성 구현**

- [ ] **Step 4: 검증**
  Run: `dotnet test tests/PellyCoder.Integration.Tests`
  Expected: 3개 통과. 이어서 각 특성을 쓴 임시 샘플 테스트 3개를 `Infrastructure/EnvFactSmokeTests.cs`에 두고(본문 `Assert.True(true)`) 실행하면 Mac에서 `Windows`, `SqlServer`, `Ollama`가 Skip으로 표시됨(환경 변수 미설정 시). 이 스모크 테스트는 유지한다(CI에서 필터 동작 확인용).

- [ ] **Step 5: Commit** — `git commit -m "test: add environment-gated fact attributes"`

---

### Task 3: CI 워크플로

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: Task 2의 Trait 이름(`Env=Windows|SqlServer|Ollama`)
- Produces: 잡 이름 `unit`, `windows-integration`, `sql-integration` (이후 마일스톤에서 단계만 추가)

- [ ] **Step 1: `ci.yml` 작성** — 트리거 `push`(모든 브랜치), `pull_request`, `workflow_dispatch`.
  * `unit`: matrix `os: [macos-latest, windows-latest]`, `actions/setup-dotnet`(global.json 사용), `dotnet build -warnaserror`, `dotnet test --filter-not-trait "Env=Windows" --filter-not-trait "Env=SqlServer" --filter-not-trait "Env=Ollama"` (xUnit v3 Microsoft Testing Platform 필터 문법이 다르면 동등한 필터로 — 의도: Env Trait가 없는 테스트만).
  * `windows-integration`: `windows-latest`. 이 Task에서는 `dotnet test --filter-trait "Env=Windows"`만. 코퍼스 빌드 단계는 Task 4·5에서 추가.
  * `sql-integration`: `ubuntu-latest`, 서비스 컨테이너 `mcr.microsoft.com/mssql/server:2022-latest`(`ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD`는 워크플로 안의 테스트 전용 고정값, 포트 1433, health check는 `sqlcmd -C -Q "SELECT 1"`), 환경 변수 `PELLY_TEST_SQL` 설정 후 `dotnet test --filter-trait "Env=SqlServer"`. 코퍼스 배포 단계는 Task 6에서 추가.

- [ ] **Step 2: 선행 조건 확인** — `git remote -v`에 `origin`이 없으면 멈추고 사용자에게 "GitHub 저장소 생성과 remote 추가"를 요청한다.

- [ ] **Step 3: Commit 후 push (사용자 확인 후)**
  ```bash
  git add .github/workflows/ci.yml
  git commit -m "ci: add unit, windows-integration, and sql-integration jobs"
  git push -u origin main
  ```

- [ ] **Step 4: 검증**
  Run: `gh run watch --exit-status` (가장 최근 run)
  Expected: 세 잡 모두 성공. `windows-integration`에서 `WindowsFact` 스모크 테스트가 Skip이 아니라 통과, `sql-integration`에서 `SqlServerFact` 스모크 테스트 통과.

---

### Task 4: Modern 코퍼스

**Files:**
- Create: `eval/corpus/Directory.Build.props` (빈 `<Project />` — 루트 설정 상속 차단), `eval/corpus/Directory.Packages.props` (`ManagePackageVersionsCentrally=false`)
- Create: `eval/corpus/Modern/Modern.sln`
- Create: `eval/corpus/Modern/Shop.Api/` — `Shop.Api.csproj`(`net8.0`, `Microsoft.NET.Sdk.Web`, Dapper 2.x, Microsoft.Data.SqlClient), `Controllers/OrdersController.cs`, `Services/IOrderService.cs`, `Services/OrderService.cs`, `Data/IOrderRepository.cs`, `Data/OrderRepository.cs`, `Models/Order.cs`, `Models/OrderStatus.cs`, `Models/OrderSearchRequest.cs`, `Program.cs`
- Create: `eval/corpus/Modern/Shop.Jobs/` — `Shop.Jobs.csproj`(SDK 스타일, `net48`, `Microsoft.NETFramework.ReferenceAssemblies` 패키지, Dapper 2.x), `DailySettlementJob.cs`, `Program.cs`
- Create: `eval/corpus/Modern/Shop.Api.Tests/` — `Shop.Api.Tests.csproj`(`net10.0`, xUnit, Shop.Api 참조), `OrderServiceTests.cs`
- Modify: `.github/workflows/ci.yml` (`unit` 잡에 `dotnet build eval/corpus/Modern/Modern.sln` 및 `dotnet test eval/corpus/Modern/Shop.Api.Tests` 단계 추가)

**Interfaces:**
- Produces (벤치마크·이후 테스트가 가리키는 대상 — 이름 고정):
  * `Shop.Api.Services.OrderService` 멤버: `Task CancelAsync(int orderId, string reason)`(60~90줄), `Task<decimal> CalculateRefundAsync(int orderId)`(100~140줄), `Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request)`(50~70줄)
  * `Shop.Api.Data.OrderRepository` 멤버: `GetByIdAsync`, `SearchAsync`, `UpdateStatusAsync` — 모두 Dapper 인라인 SQL
  * `Shop.Api.Controllers.OrdersController` 액션: `Get(int id)`, `Cancel(int id, CancelRequest body)`(`[HttpPost]`), `Search(...)`
  * `Shop.Jobs.DailySettlementJob.Run(DateTime businessDate)` (120~150줄)
  * `OrderStatus` 열거: `Pending, Paid, Shipped, Delivered, Cancelled`

- [ ] **Step 1: 코드 작성** — 위 멤버를 실무 코드처럼 작성한다: 검증·분기·로그·예외 처리를 넣어 줄 수 범위를 맞추고, 주석은 한국어. `OrderService`는 `IOrderRepository`를 생성자 주입받는다. 각 파일 LF·UTF-8.

- [ ] **Step 2: 테스트 작성** (`OrderServiceTests.cs`) — 가짜 `IOrderRepository`로 `CancelAsync`가 `Shipped` 상태에서도 현재는 취소된다는 것(벤치마크 작업의 "수정 전" 상태 고정), `CalculateRefundAsync`의 기본 환불 계산 2건.

- [ ] **Step 3: 검증**
  Run: `dotnet build eval/corpus/Modern/Modern.sln -warnaserror && dotnet test eval/corpus/Modern/Shop.Api.Tests`
  Expected: Mac에서 빌드 성공(`net48` 포함), 테스트 통과

- [ ] **Step 4: 줄 수 확인**
  Run: Roslyn 없이 간단히 — `awk` 등으로 각 대상 멤버 시작~끝 줄 수를 세어 Interfaces의 범위 안인지 확인하고 결과를 커밋 메시지 본문에 적는다.

- [ ] **Step 5: Commit** — `git commit -m "test(corpus): add modern SDK-style sample solution"`

---

### Task 5: Legacy 코퍼스

**Files:**
- Create: `eval/corpus/Legacy/Legacy.sln`
- Create: `eval/corpus/Legacy/Shop.Data/` — 레거시 csproj(`ToolsVersion="15.0"`, `Sdk` 속성 없음, `TargetFrameworkVersion v4.5`, `<Compile Include>` 명시), `packages.config`(Dapper **1.50.2** — net45 지원 버전), `OrderDao.cs`, `Dto/OrderDto.cs`, `Dto/OrderStatus.cs`, `Properties/AssemblyInfo.cs`
- Create: `eval/corpus/Legacy/Shop.Web/` — 레거시 **클래스 라이브러리** csproj(v4.8; 웹 애플리케이션 프로젝트 타입은 쓰지 않음 — Build Tools에 `WebApplication.targets`가 없을 수 있음), `packages.config`(`Microsoft.AspNet.Mvc` 5.2.9 및 의존 패키지), `Controllers/OrderController.cs`, `Services/OrderService.cs`, Shop.Data 참조
- Create: `eval/corpus/Legacy/Shop.Batch/` — 레거시 콘솔 csproj(v4.5), `Program.cs`, `SettlementBatch.cs`(ADO.NET `SqlCommand`/`SqlDataReader`)
- Create: `eval/corpus/Legacy/Shop.Tests/` — 레거시 csproj(v4.8), `packages.config`(MSTest.TestFramework·MSTest.TestAdapter 2.2.10), `OrderServiceTests.cs`, Shop.Web·Shop.Data 참조
- Create: `eval/corpus/Legacy/build.ps1` — `nuget restore` → 참조 어셈블리 설치(아래) → `vswhere`로 찾은 `MSBuild.exe`로 `Legacy.sln` 빌드 → `vstest.console.exe`로 Shop.Tests 실행
- Create: `tests/PellyCoder.Integration.Tests/Corpus/CorpusInvariantTests.cs`
- Modify: `.github/workflows/ci.yml` (`windows-integration`에 `pwsh eval/corpus/Legacy/build.ps1` 단계)

**Interfaces:**
- Produces (이름 고정):
  * `Shop.Web.Services.OrderService.Cancel(int orderId, string reason)`(60~100줄, **CP949**, 한국어 주석), `Shop.Web.Controllers.OrderController.Cancel(int id, string reason)`(`[HttpPost]`, `[ValidateAntiForgeryToken]`)
  * `Shop.Batch.SettlementBatch.Execute(DateTime businessDate)`(100~150줄, **CP949**), `Shop.Batch.SettlementBatch.BuildReport(...)`(**200줄 이상** — 예산 경계 사례)
  * `Shop.Data.OrderDao.GetOrder(int)`, `SearchOrders(...)`, `UpdateStatus(...)` — Dapper 1.50.2
  * 모든 Legacy `.cs`·`.csproj`·`.config`는 **CRLF**. C# 7.3 범위 문법만(`record`, 파일 범위 namespace, `switch` 식, nullable 참조 타입 금지).

- [ ] **Step 1: 실패하는 불변 조건 테스트 작성** (`CorpusInvariantTests.cs`, Env Trait 없음 — 어디서나 실행)
  ```csharp
  [Fact] public void LegacyProjects_HaveNoSdkAttribute()        // Legacy/**/*.csproj 루트에 Sdk 속성 없음
  [Fact] public void ModernProjects_HaveSdkAttribute()
  [Fact] public void LegacyCsFiles_UseCrLfOnly()                // 바이트에 단독 \n 없음
  [Theory]
  [InlineData("Legacy/Shop.Web/Services/OrderService.cs")]
  [InlineData("Legacy/Shop.Batch/SettlementBatch.cs")]
  public void Cp949Files_AreNotValidUtf8_AndDecodeAsCp949(string path) // strict UTF-8 디코딩 실패 + CP949 디코딩 결과에 "주문" 포함
  [Fact] public void BuildReport_IsAtLeast200Lines()             // 메서드 시그니처 줄 ~ 짝 맞는 닫는 중괄호 줄
  ```
  코퍼스 경로는 저장소 루트를 위로 찾아 `eval/corpus`로 해석하는 헬퍼(`CorpusPaths.Root`)를 같은 폴더에 둔다. CP949는 `CodePagesEncodingProvider.Instance` 등록 후 `Encoding.GetEncoding(949)`.

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Integration.Tests --filter "FullyQualifiedName~CorpusInvariant"` / Expected: 파일 없음으로 FAIL

- [ ] **Step 3: 코드 작성** — UTF-8·LF로 작성한 뒤 변환한다:
  ```bash
  # CRLF 변환 (Legacy 전체)
  find eval/corpus/Legacy -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.config' -o -name '*.sln' \) \
    -exec perl -pi -e 's/\r?\n/\r\n/' {} +
  # CP949 변환 (두 파일)
  for f in eval/corpus/Legacy/Shop.Web/Services/OrderService.cs eval/corpus/Legacy/Shop.Batch/SettlementBatch.cs; do
    iconv -f UTF-8 -t CP949 "$f" > "$f.tmp" && mv "$f.tmp" "$f"; done
  ```
  변환 후에는 이 두 파일을 텍스트 편집 도구로 다시 쓰지 않는다(다시 UTF-8이 됨). 수정이 필요하면 변환 전 단계부터 반복.

- [ ] **Step 4: `build.ps1` 작성** — .NET Framework 4.5 참조 어셈블리 설치 단계 포함:
  `nuget install Microsoft.NETFramework.ReferenceAssemblies.net45 -OutputDirectory $env:RUNNER_TEMP` 후 패키지의 `build/.NETFramework/v4.5` 내용을 `${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5`가 없을 때만 복사. 각 단계 실패 시 `exit 1`과 실패 단계 이름 출력.

- [ ] **Step 5: 로컬 검증**
  Run: `dotnet test tests/PellyCoder.Integration.Tests --filter "FullyQualifiedName~CorpusInvariant"`
  Expected: 6개 통과 (Fact 4 + Theory 2건)

- [ ] **Step 6: Commit 후 CI 검증**
  ```bash
  git add -A && git commit -m "test(corpus): add legacy .NET Framework sample solution"
  git push
  ```
  Run: `gh run watch --exit-status`
  Expected: `windows-integration`에서 `build.ps1` 성공 — Legacy.sln 빌드, Shop.Tests 통과. `unit`(windows) 잡에서 `CorpusInvariantTests` 통과(git이 CRLF·CP949를 바꾸지 않았음을 확인)

---

### Task 6: SQL 코퍼스와 로컬 SQL Server

**Files:**
- Create: `eval/corpus/Sql/schema/01-tables.sql` (`dbo.Customers`, `dbo.Orders`, `dbo.OrderItems`, `dbo.Shipments`, `dbo.Settlements` + 인덱스), `02-seed.sql`
- Create: `eval/corpus/Sql/objects/usp_CancelOrder.sql` (SSMS 형태: `SET ANSI_NULLS ON`/`GO`/`SET QUOTED_IDENTIFIER ON`/`GO`/`CREATE PROCEDURE`/`GO`)
- Create: `eval/corpus/Sql/objects/usp_SearchOrders.sql`, `usp_GetOrderDetail.sql`, `fn_OrderTotal.sql`(스칼라 함수), `vw_ShippedOrders.sql`(뷰)
- Create: `eval/corpus/Sql/objects/usp_SearchOrdersDynamic.sql` (`sp_executesql` 동적 SQL 사례), `usp_BuildSettlement.sql` (`#temp` 테이블 사례)
- Create: `eval/corpus/Sql/objects/Settlement_All.sql` (한 파일 여러 객체: 프로시저 2개 + `GRANT EXECUTE` 배치)
- Create: `eval/corpus/Sql/docker-compose.yml` (`mcr.microsoft.com/mssql/server:2022-latest`, `platform: linux/amd64`, 포트 `14333:1433`, `MSSQL_SA_PASSWORD`는 `.env`에서 — `.env.example`만 커밋)
- Create: `eval/corpus/Sql/deploy.sh` (컨테이너 안 `/opt/mssql-tools18/bin/sqlcmd -C`로 DB `PellyCorpus` 생성 → schema → objects 순서 배포. 인자로 호스트·포트·비밀번호)
- Create: `tests/PellyCoder.Integration.Tests/Corpus/SqlCorpusTests.cs`
- Modify: `.github/workflows/ci.yml` (`sql-integration`에 `deploy.sh` 실행 단계)

**Interfaces:**
- Produces: DB 이름 `PellyCorpus`. 객체 이름 고정: `dbo.usp_CancelOrder(@OrderId int, @Reason nvarchar(200))`, `dbo.usp_SearchOrders`, `dbo.usp_GetOrderDetail`, `dbo.fn_OrderTotal(@OrderId int)`, `dbo.vw_ShippedOrders`, `dbo.usp_SearchOrdersDynamic`, `dbo.usp_BuildSettlement`, `dbo.usp_SettleDaily`, `dbo.usp_SettleMonthly`. 테스트용 연결 문자열 형식: `Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=<...>;TrustServerCertificate=True`.

- [ ] **Step 1: 실패하는 테스트 작성** (`SqlCorpusTests.cs`, `[SqlServerFact]`)
  ```csharp
  [SqlServerFact] public async Task AllCorpusObjects_Exist()  // 위 9개 객체 OBJECT_ID 모두 NOT NULL
  [SqlServerFact] public async Task SeedData_HasShippedOrders() // Orders에 Status = Shipped 행 ≥ 3
  ```
  `Microsoft.Data.SqlClient`, `Dapper`를 Integration.Tests에 추가(Directory.Packages.props).

- [ ] **Step 2: SQL 파일·compose·deploy 작성** — 모든 `.sql`은 CRLF(SSMS 스크립트와 같게), UTF-8 BOM(SSMS 기본 저장 형식). Step 3 전에 Task 5와 같은 방식으로 CRLF 변환, BOM은 `printf '\xEF\xBB\xBF'`를 앞에 붙여 추가.

- [ ] **Step 3: 로컬 검증**
  ```bash
  cd eval/corpus/Sql && cp .env.example .env && docker compose up -d && ./deploy.sh localhost 14333 "$(grep SA_PASSWORD .env | cut -d= -f2)"
  PELLY_TEST_SQL="Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=...;TrustServerCertificate=True" \
    dotnet test tests/PellyCoder.Integration.Tests --filter "FullyQualifiedName~SqlCorpus"
  ```
  Expected: 2개 통과. 컨테이너가 뜨지 않으면(Review Focus 4) 오류 로그를 남기고 사용자에게 보고 후 대기.

- [ ] **Step 4: Commit 후 CI 검증** — `git commit -m "test(corpus): add SQL schema, objects, and local SQL Server setup"`, push, `gh run watch --exit-status` → `sql-integration` 성공.

---

### Task 7: 벤치마크 정의 10건

**Files:**
- Create: `eval/benchmark/README.md` (스키마 설명 — 설계 §10.6)
- Create: `eval/benchmark/tasks/*.yaml` 10개
- Create: `tests/PellyCoder.Integration.Tests/Benchmark/BenchmarkDefinitionTests.cs`, `BenchmarkTaskDefinition.cs`

**Interfaces:**
- Produces: YAML 스키마 (M3의 `PellyCoder.Eval`이 같은 스키마를 읽는다)
  ```yaml
  id: string            # 파일 이름(확장자 제외)과 같음, 형식 ^(cs-(modern|legacy)|sql)-\d{3}$
  kind: cs | sql
  file: string          # eval/ 기준 상대 경로
  method: string?       # cs: method 또는 line 중 하나 필수
  line: int?
  object: string?       # sql: 선택 (파일에 객체가 여러 개면 필수)
  request: string       # 한국어 요청문
  judge:
    build: bool         # cs 전용
    sqlCompile: bool    # sql 전용
    tests: [string]?    # 테스트 전체 이름
    mustContain: [string]?
    mustNotContain: [string]?
  ```
- 구성(10건): `cs-legacy-001~003`(Shop.Web OrderService.Cancel, Shop.Batch Execute, Shop.Data OrderDao.SearchOrders), `cs-modern-001~002`(OrderService.CancelAsync — 배송 상태 확인 추가, CalculateRefundAsync), `sql-001~005`(usp_CancelOrder, usp_SearchOrders, fn_OrderTotal, Settlement_All.sql의 usp_SettleMonthly(`object` 지정), usp_SearchOrdersDynamic). 최소 1건은 `line`으로 대상 지정.

- [ ] **Step 1: 실패하는 테스트 작성** (`BenchmarkDefinitionTests.cs`, Env Trait 없음)
  ```csharp
  [Fact] public void AllTasks_ParseAndMatchSchema()     // 필수 필드, id 형식, id == 파일 이름, kind별 필드 조합
  [Fact] public void AllTasks_TargetsExist()            // file 존재; cs+method: 파일 텍스트에 "\b{method}\s*\(" 존재; cs+line: 줄 수 이내; sql+object: 파일에 객체 이름 존재
  [Fact] public void TaskIds_AreUnique()
  [Fact] public void Composition_Is5Cs5Sql_With3Legacy()
  ```
  YamlDotNet을 Integration.Tests에 추가.

- [ ] **Step 2: 실패 확인** — Expected: 정의 파일 없음으로 FAIL

- [ ] **Step 3: YAML 10개와 README 작성**

- [ ] **Step 4: 검증** — Run: `dotnet test tests/PellyCoder.Integration.Tests --filter "FullyQualifiedName~BenchmarkDefinition"` / Expected: 4개 통과

- [ ] **Step 5: Commit** — `git commit -m "test(eval): add initial benchmark task definitions"`

---

### 스파이크 공통 규칙 (Task 8~12)

* 코드: `spikes/S<n>-<이름>/`에 .NET 10 파일 기반 앱(`<이름>.cs`, `#:package` 지시어로 패키지 지정, `dotnet run <파일>.cs`로 실행)이나 셸 스크립트.
* 결과 문서: `docs/spikes/S<n>-<이름>.md`, 섹션 고정 — `## 질문`, `## 환경`(장비, Ollama 버전, 모델 태그, 날짜), `## 방법`, `## 측정 결과`(원시 수치 표), `## 결론`, `## 설계 반영`(설계 문서의 어느 값을 무엇으로 바꾸는지).
* 통과 기준은 설계 §9 표를 그대로 쓴다. 기준을 못 넘으면 "실패"로 기록하고 문서에 대안을 적은 뒤, 설계를 바꾸는 결정은 사용자에게 묻는다.
* 측정은 워밍업 1회 후 5회 반복, 중앙값을 보고한다.

### Task 8: S1 — chat template·think 태그

**Files:** Create `spikes/S1-template/probe.cs`, `docs/spikes/S1-template.md`, `docs/spikes/S1-template.json`

- [ ] **Step 1:** `ollama show $PELLY_SPIKE_MODEL --template`와 `--modelfile` 출력을 문서에 그대로 붙인다.
- [ ] **Step 2:** `probe.cs` — `/api/generate`에 `raw: true`로 ChatML 프롬프트(`<|im_start|>system…<|im_end|>\n<|im_start|>user\n두 수를 더하는 C# 메서드를 써줘<|im_end|>\n<|im_start|>assistant\n<think>\n`)를 보내고 NDJSON 각 줄의 필드 이름 집합과 `response`/`thinking` 필드 분리 여부, `</think>` 출현 위치를 출력.
- [ ] **Step 3:** 같은 요청을 `raw: false` + `/api/chat`으로 보내 템플릿이 넣는 문자열과 비교.
- [ ] **Step 4:** 설계 §8.2 `template.json` 형식으로 `docs/spikes/S1-template.json` 작성. 문서 `## 설계 반영`에 차이점 기록.
- [ ] **Step 5:** Commit — `git commit -m "spike(S1): confirm chat template and think tags"`

### Task 9: S2 — raw 프리필과 접두사 캐시

**Files:** Create `spikes/S2-prefill-cache/prefill.cs`, `docs/spikes/S2-prefill-cache.md`
**Consumes:** S1의 템플릿 값

- [ ] **Step 1:** `prefill.cs` — 기본 프롬프트(약 3,500토큰: 코퍼스 `Shop.Api/Services/OrderService.cs` 전체 + 요청문, 맨 앞에 실행마다 다른 nonce 주석)로
  1단계: `basePrompt + "<think>\n"`, `num_predict: 512`, stop `["</think>"]`
  2단계: `basePrompt + "<think>\n" + think + "\n</think>\n\n<<<<<<< SEARCH\n"`, `num_predict: 256`
  각 단계의 TTFT(요청 전송 → 첫 `response` 청크), `prompt_eval_count`, `prompt_eval_duration`, `eval_count`를 기록. 1단계는 512 도달 시 클라이언트가 스트림을 끊는 경로도 따로 측정.
- [ ] **Step 2:** 5회 반복 표 작성. 통과 기준: 2단계 `prompt_eval_count` ≤ (think 토큰 + 50), 2단계 TTFT 중앙값 < 1.0초.
- [ ] **Step 3:** 실패 시 대안 측정: ① 1단계를 끊지 않고 `</think>` 이후 같은 스트림을 이어받기 ② `keep_alive`/동시 요청 조건 변화. 결과와 권고를 문서에 적는다.
- [ ] **Step 4:** Commit — `git commit -m "spike(S2): measure raw prefill and prefix cache reuse"`

### Task 10: S3 — think/code 샘플링 기본값

**Files:** Create `spikes/S3-sampling/sampling.cs`, `docs/spikes/S3-sampling.md`
**Consumes:** S1 템플릿, Task 7의 벤치마크 C# 작업 5건 + 같은 파일들에서 만든 추가 요청 5건(문서에 요청문 기록)

- [ ] **Step 1:** 모델 카드(Hugging Face의 해당 Qwen3.6 모델 페이지)의 권장 샘플링 값을 문서에 출처와 함께 기록.
- [ ] **Step 2:** `sampling.cs` — THINK 단계를 세 설정(A: 모델 카드 권장값, B: `Temp 0.6/TopP 0.95/TopK 20`, C: `Temp 0.1/TopP 0.9`)으로 10건씩 실행. 지표: 512 도달(잘림) 비율, 반복(같은 줄 3회 이상) 비율, think 토큰 중앙값.
- [ ] **Step 3:** CODE 단계(2단계 프리필)를 `Temp 0.1/TopP 0.9/repeat 1.05`와 `Temp 0.2/TopP 0.95/repeat 1.0`로 실행. 지표: SEARCH/REPLACE 블록 파싱 성공(마커 짝·REPLACE 종료), SEARCH가 원본에 정확 일치하는 비율.
- [ ] **Step 4:** 결론으로 설계 §8.1 `sampling` 기본값 제안. Commit — `git commit -m "spike(S3): choose think and code sampling defaults"`

### Task 11: S4 — .NET Qwen 토크나이저

**Files:** Create `spikes/S4-tokenizer/tokenizer.cs`, `docs/spikes/S4-tokenizer.md`, `spikes/S4-tokenizer/samples.txt`

- [ ] **Step 1:** 대상 모델의 `tokenizer.json`(+ 필요 시 `vocab.json`, `merges.txt`)을 Hugging Face에서 받아 `spikes/S4-tokenizer/`에 둔다. 라이선스(Apache-2.0 여부)를 문서에 기록. 파일은 커밋하지 않고(`.gitignore`) SHA-256만 문서에 기록.
- [ ] **Step 2:** `samples.txt` — 50개 샘플: 코퍼스 C# 메서드 20, SQL 10, 한국어 요청문 10, 템플릿 특수 토큰 포함 프롬프트 10.
- [ ] **Step 3:** `tokenizer.cs` — `Microsoft.ML.Tokenizers`로 로드 가능한 방식을 찾는다(우선 `BpeTokenizer`에 vocab/merges + byte-level 전처리, 안 되면 다른 제공 클래스). 샘플마다 로컬 토큰 수를 계산.
- [ ] **Step 4:** 같은 샘플을 `raw: true`, `num_predict: 1`로 보내 `prompt_eval_count`를 얻는다(샘플마다 앞에 nonce를 붙여 캐시 회피, nonce 토큰 수는 따로 측정해 뺀다).
- [ ] **Step 5:** 오차표(샘플별, 범주별 평균·최대 %) 작성. 통과 기준: 평균 오차 ≤ 2% → `QwenTokenCounter` 채택. 아니면 `CalibratedEstimator`의 범주별 계수(ASCII/한글/기타 문자당 토큰)를 같은 데이터로 산출해 기록.
- [ ] **Step 6:** Commit — `git commit -m "spike(S4): evaluate local Qwen tokenizer in .NET"`

### Task 12: S5 — NOEXEC 최소 권한·배치 순서

**Files:** Create `spikes/S5-sql-noexec/noexec.cs`, `spikes/S5-sql-noexec/setup.sql`, `docs/spikes/S5-sql-noexec.md`
**Consumes:** Task 6의 `PellyCorpus` DB

- [ ] **Step 1:** `setup.sql` — 로그인·사용자 3종 생성: ① `VIEW DEFINITION`만 ② ①+`SHOWPLAN` ③ ①+스키마 `ALTER`.
- [ ] **Step 2:** `noexec.cs` — 사용자별로 설계 §8.10 순서(`SET NOEXEC ON` 배치 → 대상 `ALTER PROCEDURE` 단독 배치 → `SET NOEXEC OFF` 배치)를 실행하고, 대상 문장을 ⓐ 정상 ⓑ 없는 컬럼(207 기대) ⓒ 없는 테이블(208 기대 — 지연 이름 확인으로 안 나올 수 있음) ⓓ 구문 오류로 바꿔 가며 반환 에러 번호·줄 번호를 기록. 각 실행 후 객체 정의가 바뀌지 않았는지 `OBJECT_DEFINITION` 비교로 확인.
- [ ] **Step 3:** 트랜잭션을 함께 쓰는 변형(`BEGIN TRAN` 배치 → 위 순서 → `ROLLBACK`)도 실행해 차이 기록.
- [ ] **Step 4:** 결론: 필요한 최소 권한, ⓒ가 NOEXEC로 잡히는지(설계 §8.10의 객체 존재 검사 필요성 확인), 확정 배치 순서. Commit — `git commit -m "spike(S5): determine NOEXEC permissions and batch order"`

---

### Task 13: 스파이크 결과 반영과 M0 마무리

**Files:**
- Modify: `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md` (§8.1 설정 기본값, §8.2 template.json·TokenCounter 선택, §8.10 배치 순서·권한, §9 표에 결과 열 추가)
- Modify: `docs/PRD.md` (§10 Q1~Q4 상태 갱신, 변경 이력 행 추가)
- Create: `src/PellyCoder.Core/Llm/Resources/template.json` (S1 확정값 — M1에서 EmbeddedResource로 연결)

- [ ] **Step 1:** 다섯 스파이크 문서의 `## 설계 반영` 항목을 설계 문서에 옮긴다. 설계를 바꾸는 결정(예: S2 실패로 생성 방식 변경, S4 실패로 추정기 채택)은 사용자 확인을 받은 뒤 반영한다.
- [ ] **Step 2:** 검증 — Run: `dotnet build -warnaserror && dotnet test && gh run list --limit 1`
  Expected: 로컬 전체 통과, 최신 CI run 성공.
- [ ] **Step 3:** M0 완료 기준 점검표를 `docs/spikes/README.md`에 작성: 스파이크 문서 5개(링크), CI 녹색(run URL), 코퍼스 CI 빌드 성공, 벤치마크 10건.
- [ ] **Step 4:** Commit — `git commit -m "docs: apply M0 spike results to design and PRD"`
- [ ] **Step 5:** 다음 단계 안내: M1 구현 계획 작성(writing-plans)을 사용자에게 제안한다.
