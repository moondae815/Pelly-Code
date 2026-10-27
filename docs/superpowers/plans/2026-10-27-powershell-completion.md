# PowerShell 자동완성 (`pelly completion powershell`) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `pelly completion powershell`이 명령·옵션·정해진 값을 Tab으로 완성하는 정적 PowerShell 스크립트를 출력한다.

**Architecture:** `CliApp.CommandSettings`의 키와 설정 클래스의 `CommandOption` 속성으로 명령 트리를 만들고(`PowerShellCompletion`), 그 트리를 PowerShell 해시 테이블 리터럴로 담은 `Register-ArgumentCompleter -Native` 스크립트를 만든다. 결과가 없으면 PowerShell 기본 경로 완성으로 넘어간다. 실제 동작은 Windows CI에서 `TabExpansion2`로 확인한다.

**Tech Stack:** .NET 10, Spectre.Console.Cli, xUnit v3, Windows PowerShell 5.1 / PowerShell 7.

**Spec:** `docs/superpowers/specs/2026-10-27-powershell-completion-design.md`

## Global Constraints

- 작업 위치는 워크트리 `/Users/payletter/git-root/Pelly-Code-completion`(브랜치 `completion`). 모든 셸 명령은 `cd /Users/payletter/git-root/Pelly-Code-completion && …`. 메인 체크아웃에서 빌드·테스트하지 않는다.
- 게이트: `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0(건너뜀은 SQL 서버·로컬 모델·Windows 전용만).
- 기존 명령의 동작·출력·종료 코드는 바뀌지 않는다. 종료 코드는 0–5만.
- 생성된 스크립트는 ASCII만, PowerShell 5.1 문법만(`?.`, `??`, 삼항 연산자, `&&`/`||` 파이프라인 연산자 금지). 실행 중 외부 프로세스·네트워크 없음.
- 숨은 옵션(`IsHidden = true`)은 완성에서 빼고, `ui cs`·`ui sql`에서는 `--yes`·`--pause-on-exit`를 뺀다.
- 사용자에게 보이는 문구는 한국어, 코드 식별자·주석은 영어.
- 커밋 끝: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. push하지 않는다.

## Review Focus

1. **경로 완성이 막힘** — 파일 자리(`pelly cs .\`)에서 완성기가 빈 결과 대신 무언가를 돌려주면 기본 경로 완성이 사라진다. 테스트: Task 3 `PathCompletion_StillWorks` (5.1·7 둘 다).
2. **`sql` 분기의 이중 역할** — `pelly sql ` 뒤는 `pull`·`check`와 파일이 모두 올 수 있다. 접두어가 하위 명령과 안 맞으면(`pelly sql db\`) 빈 결과여야 한다. 테스트: Task 3 `SqlBranch_OffersSubcommandsAndKeepsPaths`(매칭은 스크립트 안에서 일어나므로 실제 PowerShell로만 확인한다).
3. **이미 값 자리인 옵션 뒤** — `--config ` 뒤에서 옵션 이름을 내놓으면 안 되고(경로 완성), `--think ` 뒤에서는 값만. 테스트: Task 3 `FreeValueOption_FallsBackToPaths`.
4. **`pelly.exe`로 친 경우** — 명령 이름을 `pelly.exe`로 입력해도 같은 완성. 테스트: Task 3 `ExeName_Completes`.
5. **대소문자** — PowerShell 사용자는 `pelly CS`처럼 칠 수 있다. 완성은 대소문자 무시로 접두어를 맞추고 결과는 소문자 이름을 낸다(Spectre는 명령 이름 대소문자를 구분하므로 결과는 정확한 이름이어야 한다). 테스트: Task 3 `Prefix_IgnoresCase`.

---

## 파일 구조

| 파일 | 책임 |
| :--- | :--- |
| `src/PellyCoder.Cli/CliApp.cs` | `OptionNames`를 `internal`로, 숨은 옵션 제외·값 유무를 아는 `CommandOptions` 추가 |
| `src/PellyCoder.Cli/Completion/CompletionTree.cs` (새) | 명령 트리 모델과 `CommandSettings`에서 트리 만들기 |
| `src/PellyCoder.Cli/Completion/PowerShellCompletion.cs` (새) | 트리 → 스크립트 문자열 |
| `src/PellyCoder.Cli/Commands/CompletionCommand.cs` (새) | `pelly completion [SHELL]` |
| `src/PellyCoder.Cli/Commands/UiCommands.cs` | 창에서 거부하는 옵션 이름 목록(`GuiRejectedOptions`) |
| `tests/PellyCoder.Cli.Tests/Completion/*` (새) | 트리·스크립트·명령 테스트 |
| `tests/PellyCoder.Integration.Tests/E2E/PowerShellCompletionTests.cs` (새) | 실제 PowerShell 5.1·7에서 `TabExpansion2` |

---

### Task 1: 명령 트리와 스크립트 생성기

**Files:**
- Modify: `src/PellyCoder.Cli/CliApp.cs` (`OptionNames` 근처), `src/PellyCoder.Cli/Commands/UiCommands.cs`
- Create: `src/PellyCoder.Cli/Completion/CompletionTree.cs`, `src/PellyCoder.Cli/Completion/PowerShellCompletion.cs`
- Test: `tests/PellyCoder.Cli.Tests/Completion/CompletionTreeTests.cs`, `tests/PellyCoder.Cli.Tests/Completion/PowerShellCompletionTests.cs`

**Interfaces:**
- Produces:

```csharp
// CliApp (internal)
internal sealed record OptionInfo(string LongName, bool TakesValue);   // LongName without "--"
internal static IReadOnlyList<OptionInfo> CommandOptions(Type settingsType); // excludes IsHidden; TakesValue = ValueName != null
// OptionNames(...) keeps its current results for existing callers.

// UiCommands
public static readonly IReadOnlyList<string> GuiRejectedOptions = ["yes", "pause-on-exit"]; // long names without "--"

namespace PellyCoder.Cli.Completion;
public sealed record CompletionCommand(string Name, IReadOnlyList<OptionInfo> Options, IReadOnlyList<CompletionCommand> Children, bool AcceptsPositional);
// AcceptsPositional: the command itself takes positional arguments (FILE etc.) — e.g. "sql" (edit is its default command), "cs".
// A branch whose default is only a usage command ("spec", "rules", "ui") has AcceptsPositional = false and the options of its usage settings (none visible today).
public static class CompletionTree
{
    public static CompletionCommand Build(); // root: Name "pelly", Options [help, version], Children = top-level commands incl. "completion"
}
public static class PowerShellCompletion
{
    public static string Script(CompletionCommand root, string version); // ASCII only
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> OptionValues; // "think" -> Enum.GetNames<ThinkMode>() lower-cased
}
```

Tree rules (design §4.2, §4.4):
- Keys of `CliApp.CommandSettings` are split on space. A first word that appears both alone (`"sql"`) and as a prefix (`"sql pull"`) is a branch with a default command: `AcceptsPositional = true`, `Options` = the default command's options. A first word that only appears as a prefix (`"spec new"`) is a usage-only branch: `AcceptsPositional = false`.
- `completion` is added to `CommandSettings` in Task 2; until then `Build()` must not depend on it (Task 2 adds it through the dictionary, nothing else changes).
- Every command gets `help` (`--help`). Only the root gets `version`.
- `ui cs` / `ui sql` drop `UiCommands.GuiRejectedOptions`.
- `AcceptsPositional` for leaf commands: true when the settings type has a `CommandArgument` property.

Script rules (design §4.2, §4.3): `Register-ArgumentCompleter -Native -CommandName pelly, pelly.exe -ScriptBlock { param($wordToComplete, $commandAst, $cursorPosition) … }`. Walk `$commandAst.CommandElements` (skip element 0) up to the cursor; descend into children while the word matches a child name exactly (case-insensitive); then:
1. word to complete starts with `-` → options of the current command whose `--name` starts with it (case-insensitive);
2. previous element is an option with an `OptionValues` entry → those values;
3. previous element is an option with `TakesValue` → nothing (path fallback);
4. current command has children → child names matching the prefix; if none match and `AcceptsPositional`, nothing;
5. otherwise nothing.
Results are `[System.Management.Automation.CompletionResult]::new($text, $text, 'ParameterName' or 'ParameterValue', $text)`. First line: `# pelly <version> PowerShell completion. Regenerate after updating pelly: pelly completion powershell`.

- [ ] **Step 1: Write the failing tests**

```csharp
// CompletionTreeTests
[Fact] Root_HasEveryTopLevelCommandFromRegistry()
// names == distinct first words of CliApp.CommandSettings.Keys (plus "completion" once Task 2 lands — assert by computing from the dictionary, not a literal)
[Fact] SqlBranch_HasSubcommandsAndAcceptsPositional()      // sql: children [check, pull], AcceptsPositional true, options include "object","line"
[Fact] SpecBranch_IsUsageOnly()                            // spec: children [check, new], AcceptsPositional false
[Fact] EveryVisibleOptionAppears()
// for every key and settings type: each CommandOption long name with IsHidden == false is in that node's Options
[Fact] HiddenOptionsAreExcluded()                          // doctor has no "offline-selftest"; ui (usage) has no "selftest"
[Fact] UiEditCommands_DropGuiRejectedOptions()             // ui cs: no "yes","pause-on-exit"; has "verify-test"; cs: has "yes"
[Fact] HelpEverywhere_VersionOnlyAtRoot()
[Fact] TakesValue_FollowsValueName()                       // cs: "line" TakesValue true, "dry-run" false

// PowerShellCompletionTests
[Fact] Script_IsAscii()                                    // all chars <= 0x7F
[Fact] Script_FirstLineNamesVersion()                      // starts with "# pelly " + PellyInfo.Version
[Fact] Script_RegistersForPellyAndPellyExe()               // contains "-CommandName pelly, pelly.exe" and "-Native"
[Fact] Script_ContainsEveryCommandAndOption()              // each name from the tree appears as a quoted literal
[Fact] Script_UsesNoPowerShell7OnlySyntax()                // no "?.", "??", "&&", "||" (ternary is also banned; the reviewer reads for it)
[Fact] ThinkValues_MatchThinkModeEnum()                    // OptionValues["think"] == ["off","retry","always"]
```

- [ ] **Step 2: Verify RED** — `cd /Users/payletter/git-root/Pelly-Code-completion && dotnet test tests/PellyCoder.Cli.Tests -- --filter-class "*CompletionTreeTests" --filter-class "*PowerShellCompletionTests"` → compile errors.
- [ ] **Step 3: Implement** the interfaces above. `GuiOptionErrors` keeps its messages; add a test in the same commit (`UiCommandTests` or the new file) that `GuiRejectedOptions` names exactly the options `GuiOptionErrors` rejects (set each bool, check one error each).
- [ ] **Step 4: Verify GREEN** — `dotnet build -warnaserror && dotnet test` → 0 failures.
- [ ] **Step 5: Commit** — `feat(cli): command tree and PowerShell completion script generator`

---

### Task 2: `pelly completion [SHELL]` 명령

**Files:**
- Create: `src/PellyCoder.Cli/Commands/CompletionCommand.cs`
- Modify: `src/PellyCoder.Cli/CliApp.cs` (`Configure`, `CommandSettings`), `tests/PellyCoder.Cli.Tests/ExitCodeMappingTests.cs` (registry key list)
- Test: `tests/PellyCoder.Cli.Tests/Completion/CompletionCommandTests.cs`

**Interfaces:**
- Consumes: `CompletionTree.Build()`, `PowerShellCompletion.Script(root, version)`.
- Produces: `CompletionSettings` with `[CommandArgument(0, "[SHELL]")] string? Shell`; command registered as `config.AddCommand<CompletionCommand>("completion").WithDescription("셸 자동완성 스크립트를 출력합니다 (PowerShell).")`; `CommandSettings["completion"] = typeof(CompletionSettings)`.

Behavior: `powershell` or `pwsh` (case-insensitive) → write the script to stdout **as plain text** (not Spectre markup; use the console's raw writer or `Console.Out` through an injected `TextWriter` so `[` in the script is not parsed), exit 0. Missing or other value → red line `지원하는 셸은 powershell입니다: pelly completion powershell`, exit 4.

The registry guard `ExitCodeMappingTests.ValueOptionCheck_KnowsEveryCommand` pins the key list; add `"completion"` to its expected list (same ruling as the V2-0 `ui` keys: the guard exists to list every command).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Powershell_PrintsScript_Exit0()      // output == PowerShellCompletion.Script(CompletionTree.Build(), PellyInfo.Version) (+ newline handling as written)
[Fact] Pwsh_IsTheSame()
[Fact] UpperCase_IsAccepted()               // "PowerShell"
[Fact] NoShell_Exit4WithHint()              // exit 4, output contains "pelly completion powershell"
[Fact] Bash_Exit4WithHint()
[Fact] Tree_IncludesCompletionCommand()     // CompletionTree.Build() children contain "completion"
```

- [ ] **Step 2: Verify RED**, **Step 3: Implement**, **Step 4: Verify GREEN** (`dotnet build -warnaserror && dotnet test`).
- [ ] **Step 5: Commit** — `feat(cli): pelly completion powershell prints the completion script`

---

### Task 3: 실제 PowerShell 확인과 문서

**Files:**
- Create: `tests/PellyCoder.Integration.Tests/E2E/PowerShellCompletionTests.cs`
- Modify: `deploy/usage.md`, `deploy/install.md`, `README.md`, `docs/PRD.md` (FR-00 명령 형식 한 줄, 변경 이력 한 행), `deploy/field-results.md` (설계 §7의 두 칸)

**Interfaces:**
- Consumes: the built CLI via `CliBinary.Dll()` (as in `E2E/CliArgumentEndToEndTests`) running `completion powershell`.

Test harness (Windows only, `[WindowsFact]` from `Infrastructure/EnvFactAttributes.cs`): get the script from `dotnet <CliBinary.Dll()> completion powershell`, write it to a temp `pelly-completion.ps1`, create an empty `pelly.cmd` in a temp bin folder, then for each shell (`pwsh`, `powershell.exe`) run one process with `-NoProfile -NonInteractive -Command` that prepends the bin folder to `$env:PATH`, dot-sources the script, runs `TabExpansion2 -inputScript $line -cursorColumn $line.Length` for each case, and prints `CompletionText` values one per line with a case marker. Parse the output in C#. Use `SystemProcessRunner` like the existing E2E tests. If a shell is not installed on the runner, the test fails (both are present on `windows-latest`).

- [ ] **Step 1: Write the tests** (one `[WindowsTheory]` or two `[WindowsFact]`s per shell; names):
  - `TopLevel_Completes` — `pelly sp` → contains `spec`; `pelly com` → `completion`
  - `Branch_OffersSubcommands` — `pelly spec ` → `check`, `new`
  - `Options_Complete` — `pelly cs a.cs --ver` → `--verbose`, `--verify-test`; never `--offline-selftest`
  - `Values_Complete` — `pelly cs a.cs --think ` → `off`, `retry`, `always`
  - `PathCompletion_StillWorks` — in a temp dir containing `OrderService.cs`: `pelly cs .\Ord` → a result ending in `OrderService.cs`
  - `SqlBranch_OffersSubcommandsAndKeepsPaths` — `pelly sql p` → `pull`; `pelly sql .\Ord` (temp dir with `Orders.sql`) → path result
  - `FreeValueOption_FallsBackToPaths` — `pelly cs a.cs --config .\Ord` → path result, no `--` names
  - `ExeName_Completes` — `pelly.exe sp` → `spec`
  - `Prefix_IgnoresCase` — `pelly SP` → `spec`
  - `UiEdit_HidesRejectedOptions` — `pelly ui cs a.cs --y` → no `--yes`
  If PowerShell 5.1 does not fall back to path completion on an empty native result (design §4.2-5), make the script return path results itself on that version and keep the same tests.
- [ ] **Step 2: Local check** — the tests skip on macOS; run `dotnet build -warnaserror && dotnet test` → 0 failures. CI (`windows-integration`, `--fail-skips on`) is the first real run; it runs after the human chooses to push.
- [ ] **Step 3: Docs** — usage.md: the two install lines from design §3 (file method first, then the `Invoke-Expression` one-liner with its startup cost), what completes, "regenerate after updating pelly", execution policy note. install.md: optional step after install pointing to usage.md. README: short section. PRD: FR-00 `pelly completion powershell` line and a 변경 이력 row (next version number after the latest row). field-results.md: two checks from design §7. Windows statements carry 「Windows 미확인」 until CI/field confirm.
- [ ] **Step 4: Verify** — `dotnet test` 0 failures (doc tests such as PackageSourceTests still pass).
- [ ] **Step 5: Commit** — `test(e2e): PowerShell 5.1 and 7 complete pelly commands, options and paths; docs`
