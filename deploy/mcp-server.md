# PellyCodeMcpServer — OpenCode에서 쓰는 pelly 도구

`PellyCodeMcpServer.exe`는 OpenCode가 부를 수 있는 도구 두 개를 내놓는 MCP 서버입니다. pelly와 같은 코드(Roslyn·ASP·JS 영향 찾기, 빌드·테스트·SQL·페이지 검사)를 씁니다.

| 도구 | 언제 | 하는 일 |
| :--- | :--- | :--- |
| `pelly_impact` | 고치기 전 | C# 메서드·속성(`.cs`), 저장 프로시저(`.sql`), JS 함수(`.js`, `.asp` 안 `<script>`)를 **코드에서** 부르는 곳(파일·줄)을 50곳까지 보여 줍니다 |
| `pelly_verify` | 고친 뒤, 끝내기 전 | 마지막 커밋(HEAD) 이후 바뀐 파일을 보고, HEAD 대비 **새로 생긴** 빌드 오류·실패 테스트·SQL 컴파일 오류·ASP·JS·CSS 경고만 보여 줍니다. 인자가 없습니다 |

* 서버는 파일을 고치지 않고, LLM(Ollama)을 부르지 않습니다. 고치는 일은 OpenCode의 모델이 합니다.
* 사용자의 git 상태(브랜치, 인덱스, stash)를 바꾸지 않습니다. git은 읽기만 합니다(`status`, `show`, `rev-parse`, `ls-files`).
* 외부 통신은 pelly와 같습니다. 설정한 개발 SQL Server 말고는 접속하지 않습니다([no-network.md](no-network.md)).
* pelly CLI(`pelly.exe`)는 그대로 씁니다. 이 서버는 CLI를 대신하지 않습니다.

## 1. 준비

* 반입 zip을 푼 폴더에 `pelly.exe`와 함께 들어 있습니다. **`BuildHost-net472`, `BuildHost-netcore` 폴더를 두 실행 파일이 함께 쓰므로** `PellyCodeMcpServer.exe`만 따로 옮기지 마십시오.
* `pelly.exe`와 같이 PC의 **.NET 10 런타임**으로 실행됩니다(.NET 10 SDK에 들어 있음, [install.md](install.md) 「사전 조건」). C# 빌드·테스트 검사에는 `dotnet` SDK가, 레거시 .NET Framework 프로젝트에는 MSBuild가 필요합니다.
* 작업 공간은 **git 저장소**여야 합니다. 「수정 전」 기준이 git의 마지막 커밋(HEAD)이기 때문입니다. `git`이 PATH에 있어야 합니다.
* 설정은 pelly와 같습니다. 사용자 설정(`%APPDATA%\pelly\config.json`) → 작업 공간 루트의 `pelly.json` → 환경 변수(`PELLY__*`, `PELLY_SQL_CONNECTION`) 순입니다. `llm` 절은 쓰지 않습니다. `pelly.json`이 깨진 JSON이면 서버가 멈추지 않고 설정 오류(파일 이름 포함)를 냅니다.

## 2. 점검: `--check`

OpenCode에 등록하기 전에 터미널에서 먼저 실행합니다. MCP를 띄우지 않고 표만 쓰고 끝납니다.

```
PellyCodeMcpServer.exe --check --root D:\src\shop
```

| 행 | 보는 것 |
| :--- | :--- |
| 루트·git | `--root`(없으면 현재 폴더)에서 위로 올라가 찾은 git 저장소 폴더. 저장소가 아니면 실패 |
| 설정 | 설정을 읽었는지, 연결 문자열(비밀번호는 가림)과 빌드·테스트 제한 시간 |
| .NET SDK, MSBuild (레거시) | `pelly doctor`의 같은 행과 같은 판정. MSBuild가 없으면 주의(레거시 프로젝트만 영향) |
| 의미 분석 | 실행 파일 옆 `BuildHost-*` 폴더(Roslyn 보조 프로세스). 없으면 실패 |
| SQL | 연결 문자열이 있을 때만 접속해 봅니다. 없으면 「설정 안 됨 (SQL 검사는 건너뜁니다)」 |
| 도구 | `2개: pelly_impact, pelly_verify` |
| HEAD 기준 | 마지막 기준 작업의 HEAD 커밋·상태·이유(디스크 기록만 읽음) |

* 행이 하나라도 「[실패]」이면 종료 코드 1, 아니면 0입니다.
* `--check`는 기준 빌드를 시작하지 않습니다.

## 3. OpenCode에 등록

OpenCode 설정 파일(`opencode.json`, 모든 프로젝트용은 `%USERPROFILE%\.config\opencode\opencode.json`)의 `mcp` 아래에 넣습니다. 경로는 자기 값으로 바꿉니다. JSON 안에서는 `\`를 `\\`로 씁니다.

```json
{
  "mcp": {
    "pelly": {
      "type": "local",
      "command": ["C:\\Tools\\pelly\\PellyCodeMcpServer.exe", "--root", "D:\\src\\shop"],
      "timeout": 660000
    }
  }
}
```

* **`"timeout": 660000`(밀리초, 11분)을 꼭 넣으십시오.** 개발 PC의 OpenCode 1.18.25에서 잰 값입니다(2026-11-17).
  * 진행 알림도 `timeout`도 없으면 도구 호출이 **60초**에 「MCP error -32001: Request timed out」으로 끊겼습니다.
  * 서버는 오래 걸리는 동안 10초마다 진행 알림을 보내고, 이 알림이 한도를 늘립니다. `pelly_verify`는 최대 600초 기다린 뒤 「기준 빌드가 아직 진행 중입니다. 잠시 뒤 다시 pelly_verify를 부르세요」를 돌려주고, 작업은 서버 안에서 계속합니다. 클라이언트가 진행 알림을 받지 않는 호출이면 45초만 기다립니다.
  * 660000은 그 600초에 60초를 더한 안전망입니다. 알림이 어떤 이유로 가지 않아도 600초까지는 끊기지 않습니다.
* **키 배치는 현장 OpenCode 버전에서 확인하십시오.** 위는 1.18.25에서 쓴 모양입니다. 버전에 따라 서버를 `"mcp": { "servers": { "pelly": { … } } }` 아래에 두거나 `"enabled": true`를 쓰기도 합니다(mssql-mcp-server 안내 참고). `timeout`의 모양도 버전 문서로 확인하십시오.
* **도구가 둘 다 보이지 않으면** 서버 항목에 `"codemode": false`를 넣으십시오. mssql-mcp-server 안내와 같은 주의입니다. OpenCode의 codemode는 MCP 도구를 묶어서 보여 주어 일부만 보일 수 있습니다.
* `--root`는 작업 공간(git 저장소) 폴더입니다. 저장소 안의 하위 폴더를 줘도 됩니다. 그 저장소의 맨 위 폴더가 루트가 됩니다. 저장소마다 서버 항목을 따로 두거나 프로젝트 루트의 `opencode.json`에 넣으십시오.
* OpenCode를 다시 시작한 뒤 `opencode mcp list`로 `pelly`가 연결됨이고 도구가 2개인지 봅니다.
* 스크립트에서 `opencode run`을 돌릴 때는 표준 입력을 비우십시오(macOS·Linux `< /dev/null`, Windows cmd `< NUL`). 표준 입력이 터미널이 아니면(파이프, 백그라운드) 시작 단계에서 멈출 수 있습니다. Windows 쪽은 미확인입니다.

## 4. 옵션

| 옵션 | 뜻 |
| :--- | :--- |
| `--root <폴더>` | 작업 공간. 없으면 현재 폴더. 하위 폴더를 주면 git 맨 위 폴더가 루트가 됩니다. git 저장소가 아니면 두 도구 모두 「git 저장소가 아닙니다: <루트>」를 돌려줍니다 |
| `--log-file <경로>` | 로그를 그 파일에도 씁니다(표준 오류에는 항상 씀). 시작 실패도 여기에 남습니다. OpenCode가 「서버 실패」만 보일 때 원인을 찾는 데 씁니다 |
| `--check` | 2절의 자기 점검. MCP를 띄우지 않습니다 |

로그 파일을 남기는 등록 예입니다.

```
C:\Tools\pelly\PellyCodeMcpServer.exe --root D:\src\shop --log-file D:\logs\pelly-mcp.log
```

(`command` 배열에는 `"--log-file", "D:\\logs\\pelly-mcp.log"`를 더합니다.) 표준 출력은 MCP 메시지만 씁니다. 그래서 터미널에서 옵션 없이 실행하면 아무것도 보이지 않고 입력을 기다립니다. 정상입니다(Ctrl+C로 끝냄).

## 5. 결과 읽기

**`pelly_impact`** — 첫 줄이 대상, 다음이 부르는 곳입니다. Roslyn 의미 분석이 실패해 이름 기준으로 찾았거나 사이트 색인이 잘렸으면 첫 줄에 그 사실을 적습니다(빠진 곳이 있을 수 있음). 50곳을 넘으면 「외 N곳」.

**`pelly_verify`** — 첫 줄은 「결과: 새 오류 N건, 새 경고 M건」 또는 「결과: 새 문제 없음」입니다.

* 오류: 새 빌드 오류, 새로 실패한 테스트, 새 SQL 컴파일 오류, 새 VBScript 구문 오류, 페이지에서 정의되지 않은 새 이름. 고쳐야 합니다.
* 경고: 그 밖의 추정 검사(JS 이름·요소·폼 필드·CSS 속성·jQuery API·서명이 바뀐 함수의 남은 호출). 확인할 단서입니다.
* 묶음마다 20줄까지 보이고 넘으면 「외 N건」입니다. include 파일이 바뀌면 그것을 include하는 페이지도 50개까지 검사합니다.
* HEAD에도 있던 오류·실패는 빼고 보입니다. 레거시 코드에 원래 있던 경고 때문에 모델이 엉뚱한 곳을 고치지 않게 하기 위함입니다.

## 6. HEAD 기준 사본

「수정 전」 빌드·테스트 결과는 작업 공간 **밖**의 사본에서 만듭니다.

* 위치: `%LocalAppData%\PellyCode\mcp-base\<해시>\`(해시는 루트 전체 경로에서 만든 16자). 옆의 `<해시>.baseline.json`이 결과 기록입니다.
* 서버가 켜질 때와 HEAD 커밋이 바뀐 것을 알았을 때 백그라운드에서 사본을 맞추고, 루트의 솔루션(`.sln`·`.slnx`)에 든 프로젝트를 빌드하고 테스트 프로젝트를 돌려 둡니다. 솔루션이 없으면 `pelly_verify` 때 만듭니다.
* 처음 한 번은 작업 공간 전체를 복사하므로(`bin`, `obj`, `.git`, `.pelly`, `.vs`, `node_modules` 등 제외, `packages`는 포함) 큰 저장소는 시간이 걸립니다. 다음부터는 바뀐 파일만 맞춥니다.
* SDK 형식 프로젝트는 사본에서 복원합니다. 폐쇄망에서는 PC에 이미 있는 NuGet 전역 패키지 폴더를 씁니다(pelly 빌드와 같음).
* **지워도 됩니다.** 서버를 끈 뒤 `%LocalAppData%\PellyCode\mcp-base\` 아래 폴더와 `.baseline.json`을 지우면 다음 시작 때 다시 만듭니다. 디스크가 모자라거나 기준이 이상해 보일 때 지우십시오.
* 복원 실패, MSBuild 없음, 시간 초과 같은 **환경 문제**로 기준을 못 만들면 `pelly_verify`는 지금 오류를 모두 보이고 첫 줄 아래에 「HEAD 기준을 만들지 못해 모든 오류를 보입니다: <이유>」를 적습니다. 이유는 `--check`의 「HEAD 기준」 행과 로그에도 남습니다.
* 작업 공간 루트에는 pelly와 같이 `.pelly\` 폴더(추적 제외 `.gitignore` 포함)가 생깁니다.

## 7. 테스트가 없을 때

바뀐 프로젝트를 참조하는 테스트 프로젝트가 없으면 빌드만 검사하고 「[테스트] 건너뜀 — <프로젝트>를 참조하는 테스트 프로젝트 없음. 동작 변경은 확인되지 않았습니다」를 보입니다. 오류 수에는 넣지 않습니다.

* 고치기 전에 pelly CLI로 현재 동작을 고정하는 테스트를 만들어 두면 `pelly_verify`가 그 테스트를 돌립니다. 테스트 만들기는 모델이 필요해 이 서버는 하지 않습니다.

  ```
  pelly test <파일.cs> --method <메서드>
  ```

  자세한 것은 [usage.md](usage.md)의 「`pelly test`」입니다.
* 이름 규칙에 맞지 않는 테스트 프로젝트는 `pelly.json`의 `test.projects`로, 레거시 테스트 프로젝트의 실행 명령은 `test.command`로 지정합니다(pelly와 같음).

## 8. SQL·ASP·웹 검사의 조건

* **SQL:** `.sql` 파일의 객체 배치마다 `NOEXEC`로 컴파일해 HEAD 텍스트와 비교합니다. 연결은 `sql.connectionString` 또는 `PELLY_SQL_CONNECTION`과 허용 목록(`sql.allowedHosts`·`allowedDatabases`)을 씁니다. 없거나 허용 목록 밖이면 「[건너뜀] SQL 컴파일 검사 — <이유>」. **운영 연결(`sql.production`)은 쓰지 않습니다.** 프로시저·함수·뷰 없이 테이블·데이터 배치만 있는 `.sql`은 컴파일 검사 대상이 없습니다.
* **ASP 구문(cscript):** Windows에서만 합니다. 그 밖에서는 「[건너뜀] ASP 구문 검사 — Windows가 아님」.
* **ASP·JS·CSS 검사와 ASP·JS 영향 찾기:** `pelly.json`에 `asp` 또는 `web` 절이 있어야 합니다([field-checklist.md](field-checklist.md) 0절의 예). 없으면 그 부분은 건너뛴다고 적습니다.
* 빌드·테스트 제한 시간은 `build.timeoutSeconds`·`test.timeoutSeconds`(pelly 설정 그대로)입니다. OpenCode가 호출을 끊으면 그 호출이 띄운 빌드·테스트 프로세스를 끝냅니다(백그라운드 기준 작업은 계속합니다).

## 9. AGENTS.md 권장 줄

작업 공간 루트의 `AGENTS.md`에 이 줄을 넣으면 모델이 도구를 빠뜨리지 않게 도와줍니다.

> 코드를 고친 뒤에는 끝내기 전에 반드시 `pelly_verify`를 부르고, 새 문제가 있으면 고친 뒤 다시 부른다. 메서드·SP·JS 함수의 이름이나 매개변수를 바꾸기 전에는 `pelly_impact`로 부르는 곳을 확인한다.

## 10. mssql-mcp-server와 함께 쓰기

DB를 보는 일(정의 보기, DB 안 의존성, 스키마 비교, SP→SP·테이블 영향)은 mssql-mcp-server가 맡고, 코드 쪽(부르는 곳, 고친 뒤 확인)은 이 서버가 맡습니다. 두 서버를 같은 설정에 나란히 둡니다. mssql 항목은 mssql-mcp-server 안내의 값을 그대로 쓰십시오.

```jsonc
{
  "mcp": {
    "pelly": {
      "type": "local",
      "command": ["C:\\Tools\\pelly\\PellyCodeMcpServer.exe", "--root", "D:\\src\\shop"],
      "timeout": 660000
    },
    "mssql": {
      "type": "local",
      "command": ["C:\\Tools\\MssqlMcpServer\\MssqlMcpServer.exe"],
      "environment": { "MSSQL_CONNECTION_STRING": "Server=<개발 DB 서버>;Database=<DB>;Integrated Security=true;TrustServerCertificate=true" },
      "codemode": false
    }
  }
}
```

* 예: SP의 매개변수를 바꾸는 요청이면, 모델은 mssql 도구로 정의와 DB 안 의존성을 보고, `pelly_impact`로 그 SP를 부르는 ASP·C# 코드를 찾고, 고친 뒤 `pelly_verify`로 SQL 컴파일과 빌드를 확인합니다.
* 두 서버의 SQL 연결은 따로 설정합니다. 이 서버는 pelly 설정(`sql.connectionString`·`PELLY_SQL_CONNECTION`)을 읽습니다.

## 11. 알려진 한계

* Windows 실행, 현장 OpenCode 버전의 등록 형식, 실제 레거시 저장소의 기준 빌드 시간은 **Windows 미확인**입니다. 현장 확인은 [field-checklist.md](field-checklist.md)의 「MCP 서버」 절입니다.
* ASP `Sub`·`Function`을 부르는 곳, CSS 선택자 영향, SP→SP·테이블 영향은 찾지 않습니다. `.css`가 바뀌어도 그것을 쓰는 페이지는 검사하지 않습니다.
* 도구 호출은 한 번에 하나씩 실행합니다.
