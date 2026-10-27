# PowerShell 자동완성 (`pelly completion powershell`) 설계

작성 2026-10-27. 상태: 설계, 사람 확인 전. 설계 검토(빌더 확인) 반영. 사람은 「질문 없이 바로 설계」를 골랐다. 「Claude 결정」으로 표시한 것은 사람이 아직 확인하지 않은 판단이다.

## 1. 왜 하는가

`pelly`를 터미널에서 직접 칠 때, 명령 이름(`cs`, `sql pull`, `spec check` …)과 옵션 이름(`--verify-test`, `--expect-break`, `--strict-baseline` …)을 외우거나 `--help`를 봐야 한다. 옵션이 V2에서 크게 늘었다. 파일 경로는 셸이 이미 Tab으로 완성하지만 명령·옵션은 안 된다. 현장은 Windows라 PowerShell에서 Tab으로 명령·옵션을 완성하게 한다.

## 2. 목표 / 비목표 / 실패 조건

**목표**
1. `pelly completion powershell`이 PowerShell 자동완성 스크립트를 표준 출력에 쓴다. 사람은 이것을 프로필에 한 번 등록한다.
2. Tab으로 완성되는 것: 명령과 하위 명령, 그 명령의 옵션 이름(긴 이름), 정해진 값이 있는 옵션의 값(`--think off|retry|always`).
3. 파일·폴더 자리에서는 PowerShell 기본 경로 완성이 그대로 동작한다(지금보다 나빠지지 않는다).
4. 명령·옵션 목록은 손으로 따로 적지 않는다. CLI가 이미 가진 명령 목록(`CliApp.CommandSettings`)과 설정 클래스의 옵션 속성에서 만든다. 명령을 더하면 완성도 따라온다.
5. Windows PowerShell 5.1과 PowerShell 7 둘 다.

**비목표**
- bash·zsh·fish 완성(개발 PC는 macOS지만 운영은 Windows). 필요하면 나중에 같은 생성기에 셸을 더한다.
- 파일 자리에서 확장자 거르기(`cs` 뒤에는 `.cs`만 등). 기본 경로 완성으로 충분하다(Claude 결정).
- 메서드 이름·SQL 객체 이름·테스트 이름 같은 값 완성(파일을 읽어야 하고 Tab마다 느려진다).
- 옵션 설명 문구 표시(툴팁). §4.3 이유로 넣지 않는다(Claude 결정).
- 숨은 옵션(`--offline-selftest`, `--selftest`) 완성. 숨은 것은 완성에서도 숨긴다.
- 프로필을 Pelly가 직접 고치는 설치 명령. 사람의 프로필 파일은 사람이 고친다.

**실패 조건**
- Tab을 누를 때마다 `pelly.exe`가 실행된다(단일 파일 시작이 1~2초라 Tab이 느려진다). 스크립트는 정적이어야 한다.
- 등록 뒤 `pelly cs .\Ser`+Tab 같은 경로 완성이 더 이상 되지 않는다.
- 명령을 하나 더했는데 완성 스크립트에 빠져도 테스트가 모른다.
- 한국어 Windows PowerShell 5.1에서 스크립트가 깨져 로드되지 않는다.
- 기존 명령의 동작·출력·종료 코드가 바뀐다.

## 3. 사용 흐름

```powershell
# 한 번: 스크립트를 파일로 저장하고 프로필에서 불러오게 한다
pelly completion powershell > "$HOME\pelly-completion.ps1"
Add-Content $PROFILE '. "$HOME\pelly-completion.ps1"'

# 새 PowerShell 창에서
pelly sp<Tab>            → pelly spec
pelly spec <Tab>         → check / new
pelly cs .\Order<Tab>    → (기본 경로 완성) .\OrderService.cs
pelly cs a.cs --ver<Tab> → --verify-test / --verbose
pelly cs a.cs --think <Tab> → off / retry / always
```

- 파일로 저장해 불러오는 방식을 안내한다(Claude 결정). `pelly completion powershell | Out-String | Invoke-Expression`을 프로필에 넣으면 PowerShell을 열 때마다 `pelly.exe`가 실행되어 1~2초 늦어진다. 두 방식 모두 문서에 적되 파일 방식을 먼저 둔다.
- `pelly`를 새 버전으로 바꾸면 스크립트를 다시 만든다(문서에 적는다). 스크립트 첫 줄 주석에 만든 `pelly` 버전을 적는다.
- 실행 정책이 `Restricted`면 프로필 자체가 실행되지 않는다. 이것은 PC 정책이라 Pelly가 바꾸지 않는다. 문서에 「`Get-ExecutionPolicy`가 Restricted면 쓸 수 없음, 정책은 관리자에게」라고 적고 현장 확인 항목에 넣는다.

## 4. 동작 (Claude 결정)

### 4.1 명령

- `pelly completion powershell`: 스크립트를 표준 출력에 쓰고 종료 0.
- `pelly completion` (셸 없음), `pelly completion bash` 등: 지원하는 셸(`powershell`)을 알려 주고 종료 4. `pwsh`도 `powershell`과 같은 뜻으로 받는다. 셸은 선택 위치 인자 `[SHELL]`이고, 검사는 명령 코드에서 직접 해 `ExitCodes.EnvError`(4)를 돌려준다(저장소의 다른 명령과 같은 방식, 설계 검토 반영).
- `completion` 명령 자신도 완성 대상이다(`pelly completion <Tab>` → `powershell`).

### 4.2 완성 규칙

스크립트는 `Register-ArgumentCompleter -Native -CommandName pelly, pelly.exe`로 등록한다. 입력 줄을 낱말로 나눠:

1. 아직 명령이 정해지지 않았으면 → 최상위 명령 이름 중 접두어가 맞는 것.
2. 분기 명령(`sql`, `spec`, `rules`, `ui`) 바로 뒤 → 하위 명령 이름. `sql`은 하위 명령(`pull`, `check`)이 없을 때 그 자리가 파일이므로(편집 명령이 분기의 기본 명령), 접두어가 `pull`/`check`와 맞지 않으면 아무것도 돌려주지 않아 기본 경로 완성에 맡긴다.
3. 현재 낱말이 `-`로 시작 → 정해진 명령의 옵션 긴 이름 중 접두어가 맞는 것. 이미 쓴 옵션은 빼지 않는다(여러 번 줄 수 있는 옵션이 있다). Spectre가 자동으로 붙이는 `--help`는 모든 명령에, `--version`은 명령이 정해지기 전(최상위)에만 더한다(설계 검토 반영). `ui cs`·`ui sql`에서는 창에서 거부되는 `--yes`·`--pause-on-exit`를 뺀다. 이 목록은 `UiCommands`가 거부하는 옵션과 같은 출처에서 가져온다(복사 금지).
4. 바로 앞 낱말이 값 목록이 있는 옵션(`--think`) → 그 값들.
5. 그 밖(파일 자리, 요청문 자리, 값이 자유로운 옵션 뒤) → **아무것도 돌려주지 않는다.** PowerShell은 네이티브 완성기가 결과를 주지 않으면 기본 경로 완성으로 넘어간다. 이 동작은 5.1과 7 모두에서 CI로 확인한다(§6). 넘어가지 않는 판이 있으면 그 판에서 경로 완성 결과를 직접 만들어 돌려준다.

옵션 값을 받는지(`--out <DIR>`)는 설정 클래스의 `CommandOption` 속성(값 이름 유무)에서 안다. 값이 자유로운 옵션 뒤는 5번 규칙(기본 경로 완성)이다.

### 4.3 스크립트 형식

- **ASCII만 쓴다.** 명령·옵션 이름은 영어라 충분하다. 한국어 설명을 넣으면 PowerShell 5.1이 BOM 없는 파일을 시스템 코드 페이지(CP949)로 읽거나, 파이프로 받은 출력을 콘솔 인코딩으로 해석하면서 깨질 수 있다. 그래서 설명 툴팁을 넣지 않는다.
- 명령 트리와 옵션 목록은 스크립트 안의 PowerShell 해시 테이블 리터럴로 들어간다. 실행 중 외부 호출 없음.
- PowerShell 5.1 문법만 쓴다(`?.`, `??`, 삼항 연산자 금지).

### 4.4 만드는 곳

- 새 파일 `src/PellyCoder.Cli/Completion/PowerShellCompletion.cs`: `CliApp.CommandSettings`의 키로 트리를 만든다. 키 `"sql"`과 `"sql pull"`처럼 한 낱말이 명령이면서 다른 키의 앞부분이면 「기본 명령이 있는 분기」, `"spec new"`처럼 앞부분만 나오면 「기본 명령이 사용법뿐인 분기」로 읽는다. 분기 이름(`sql`, `spec`, `rules`, `ui`)을 따로 하드코딩하지 않는다.
- 옵션 이름 읽기는 `CliApp`의 `OptionNames` 도우미를 같이 쓴다. 지금은 `private static`이고 숨은 옵션을 거르지 않으므로, `internal`로 열고 숨은 옵션 제외와 값 이름 유무를 함께 돌려주게 넓힌다(기존 호출의 결과는 그대로, 설계 검토 반영).
- 값 목록이 있는 옵션은 생성기 안의 작은 표 하나에 둔다(`--think`). 값은 `Enum.GetNames<ThinkMode>()`를 소문자로 바꾼 것이다(설정 검증이 `Enum.TryParse<ThinkMode>(ignoreCase: true)`이므로, `PellyOptionsLoader.cs`, 설계 검토 반영). 같은 출처라 따로 대조 테스트가 필요 없다.
- `completion` 명령은 `CliApp.Configure`에 등록하고 `CommandSettings`에도 넣는다(기존 등록 검사 테스트가 이를 지킨다).

## 5. 반입

- `pelly.exe` 안의 명령이라 반입 zip에 새 파일은 없다(Claude 결정). 미리 만든 `.ps1`을 zip에 넣으면 허용 목록·체크섬·실행 파일 버전과의 어긋남을 관리해야 한다.
- 문서: `deploy/usage.md`(설치 두 줄과 예), `deploy/install.md`(설치 뒤 선택 단계), README, PRD FR-00 명령 형식과 변경 이력.
- 오프라인: 스크립트는 네트워크를 쓰지 않는다. 새 외부 프로세스 실행도 없다.

## 6. 검증 방법

1. **생성기 단위 테스트**(어느 OS든):
   - `CommandSettings`의 모든 명령과, 각 설정 클래스의 숨기지 않은 모든 긴 옵션 이름이 스크립트에 있다.
   - 숨은 옵션(`--offline-selftest`, `--selftest`)은 없다.
   - 스크립트가 ASCII만 담는다.
   - 첫 줄 주석에 `PellyInfo.Version`이 있다.
   - `--think` 값 표가 설정이 받는 값과 같다.
2. **명령 테스트**: `completion powershell` 종료 0과 출력, `completion`·`completion bash` 종료 4와 안내 문구.
3. **실제 PowerShell 테스트**(Windows CI, `windows-integration` 잡의 Windows 전용 테스트로): 스크립트를 로드한 뒤 `TabExpansion2`로 완성 결과를 확인한다. `pwsh`(7)와 `powershell.exe`(5.1) 둘 다. 테스트는 `tests/PellyCoder.Integration.Tests`에 `[WindowsFact]`로 둔다. 이 프로젝트는 Cli를 출력 어셈블리 없이 참조하므로, 스크립트는 기존 CLI 종단 테스트(`E2E/CliArgumentEndToEndTests`)가 `pelly`를 프로세스로 띄우는 방식 그대로 `completion powershell`을 실행해 얻는다(설계 검토 반영). 네이티브 완성기가 `pelly` 낱말에 걸리도록, 임시 폴더에 빈 `pelly.cmd`를 두고 그 폴더를 PATH 앞에 넣은 PowerShell 프로세스에서 확인한다(함수나 별칭으로는 네이티브 완성기가 걸리지 않을 수 있다):
   - `pelly sp` → `spec`
   - `pelly spec ` → `check`, `new`
   - `pelly cs a.cs --ver` → `--verbose`, `--verify-test`
   - `pelly cs a.cs --think ` → `off`, `retry`, `always`
   - `pelly cs .\` → 임시 폴더의 파일 이름이 나옴(경로 완성 유지)
   - `pelly sql ` → `pull`, `check`와 함께 경로 완성이 막히지 않음
   - 숨은 옵션은 나오지 않음
   macOS에는 PowerShell이 없어 이 테스트는 건너뛴다(기존 Windows 전용 건너뜀 규칙).
4. 기존 게이트: `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0.

## 7. 사람이 현장에서 확인할 것

- 현장 PC의 PowerShell 실행 정책(`Get-ExecutionPolicy -List`)이 프로필 실행을 허용하는지. 허용하지 않으면 이 기능은 쓸 수 없다.
- 한국어 Windows의 PowerShell 5.1에서 `pelly completion powershell > 파일` 뒤 로드가 되는지(5.1의 `>`는 UTF-16으로 쓴다. ASCII만 쓰므로 문제없을 것으로 본다).
- `deploy/field-results.md`에 두 칸을 더한다.

## 8. 만드는 사람에게 맡기는 것

- 스크립트 안 낱말 나누기의 세부(따옴표 안 공백 등은 PowerShell이 주는 `$commandAst`로 처리).
- 생성기 내부 구조와 이름.
- 실제 PowerShell 테스트 안에서 `pwsh`·`powershell.exe`를 띄우는 세부(테스트 프로젝트는 `NetworkPolicyTests` 검사 대상 밖). `windows-integration` 잡의 `--minimum-expected-tests`·`--fail-skips on` 조건이 그대로 성립하는지 확인한다.
