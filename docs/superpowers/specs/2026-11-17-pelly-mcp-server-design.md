# PellyCodeMcpServer 설계 — OpenCode에 「부르는 곳 찾기」와 「고친 뒤 확인」을 주는 MCP 서버

* 날짜: 2026-11-17
* 상태: 초안 (사람 승인 전)
* 저장소 위치: `src/PellyCode.McpServer` → 실행 파일 `PellyCodeMcpServer.exe`

## 1. 왜 만드나

폐쇄망 개발자는 OpenCode + Qwen3.6:35b(Ollama)로 코드를 고치고, DB는 별도 MCP 서버(mssql-mcp-server)로 봅니다.
2026-11-17 개발 PC 비교(같은 모델 `qwen3.6:35b-a3b-q4_K_M`, 벤치마크 50건, 같은 판정기)에서 다음이 보였습니다.

| | pelly | OpenCode 단독 |
| :--- | ---: | ---: |
| 성공 | 47/50 | 42/50 |
| 깨진 빌드를 남기고 「완료」 | 0 | 1 |
| 고치지 않고 끝남 | 0 | 5 |
| 스스로 빌드·테스트·SQL 컴파일로 확인한 작업 | (pelly가 항상) | 0/50 |

OpenCode의 약점은 「고친 뒤 확인하지 않는다」와 「레거시 코드에서 부르는 곳을 모른다」입니다.
pelly Core에는 둘 다 이미 있습니다(Roslyn·ASP·JS 영향 찾기, 빌드·테스트·SQL·페이지 검사). 이것을 OpenCode가 부를 수 있는 도구로 내놓는 것이 이 서버입니다.
pelly CLI는 그대로 유지합니다(사람 결정). 이 서버는 CLI를 대신하지 않고 Core를 함께 씁니다.

## 2. 목표·비목표·실패로 볼 것

**목표**
* G1. OpenCode가 고친 뒤 `pelly_verify`를 부르면, 마지막 커밋(HEAD) 대비 **새로 생긴** 빌드 오류·실패 테스트·SQL 컴파일 오류·페이지 경고를 돌려준다.
* G2. 고치기 전에 `pelly_impact`를 부르면, 그 멤버·SP·JS 함수를 **코드에서** 부르는 곳(파일·줄)을 돌려준다.
* G3. 폐쇄망 반입: pelly와 같은 방식(framework-dependent, .NET 10 런타임 사용), 외부 통신 없음.

**비목표**
* 파일 수정, LLM 호출(서버는 Ollama를 쓰지 않음).
* DB 메타데이터 도구(정의 보기, DB 안 의존성, 스키마 비교) — mssql-mcp-server가 맡음.
* SP→SP·테이블 영향(DB가 필요한 `SqlImpactFinder`) — mssql-mcp-server의 `get_dependencies`로 대신.
* ASP Sub/Function을 부르는 곳, CSS 선택자 영향 — v1에 없음.
* 운영 DB 연결.

**실패로 볼 것 (돌아가도 안 되는 것)**
* 표준 출력에 MCP 메시지 말고 한 글자라도 쓰는 것(통신이 깨짐).
* 사용자 git 상태를 바꾸는 것(`git stash`, `git worktree add`, 인덱스 변경, 브랜치 이동).
* HEAD에도 있던 오류·실패를 「새 문제」로 알리는 것(레거시 코드에서 모델이 엉뚱한 곳을 고치게 됨).
* 도구가 오래 걸려 OpenCode가 끊는 것, 끊긴 뒤 빌드 프로세스가 남는 것.
* 도구가 많거나 인자가 복잡해 Qwen이 잘못 부르는 것.

## 3. 접근 방법

| 안 | 내용 | 판단 |
| :--- | :--- | :--- |
| **A (채택)** | 도구 2개: `pelly_impact(path, line?, name?)`, `pelly_verify()`. verify는 인자 없이 git 변경 파일을 보고 검사를 고름 | Qwen이 잘못 부를 여지가 가장 적음. 「확인을 안 한다」는 약점은 인자 없는 한 번 호출로 메우는 것이 맞음 |
| B | A + `pelly_web_check(path)` | 고치기 전 페이지 상태를 볼 수 있으나 verify와 겹치고 고를 도구가 늘어남 |
| C | 종류별 8개 안팎 | 하나하나는 단순하나 고르기 실수가 늘어남(mssql-mcp-server에서 도구 설명을 다듬어야 했던 경험) |

사람이 고를 기회가 있었으나 고르지 않아, 에이전트 권고 A로 진행한다(2026-11-17).

**구현 방식(에이전트 판단):** 서버가 Core를 프로세스 안에서 직접 부르고, 프로세스는 OpenCode가 띄운 동안 살아 있다. pelly.exe를 매번 실행하는 방식은 Roslyn 로드를 매번 다시 해야 해서 뺐다.

**저장소(사람 결정):** Pelly-Code 저장소 안의 별도 프로젝트. 별도 저장소는 Core를 패키지로 나눠 버전을 맞춰야 해서 뺐다.

## 4. 사람이 정한 것

* 프로젝트 `src/PellyCode.McpServer`, 실행 파일 이름 `PellyCodeMcpServer`.
* 「수정 전」 기준은 git의 마지막 커밋(HEAD). 현장 소스는 git으로 관리한다.
* pelly CLI는 유지한다.
* 솔루션·프로젝트 이름은 `PellyCode.*`(이 설계 전에 바꿈, 브랜치 `rename-pellycode`).

## 5. 구성

```
src/PellyCode.McpServer/   (Core 참조, Cli 참조 안 함)
  Program.cs               일반 호스트 + MCP stdio, --check
  Tools/ImpactTool.cs      pelly_impact
  Tools/VerifyTool.cs      pelly_verify
  Git/                     변경 파일, HEAD 내용 읽기
  Baseline/                HEAD 기준 사본과 그 빌드·테스트 결과 캐시
```

* SDK: `ModelContextProtocol` 2.4.0, `Microsoft.Extensions.Hosting` — mssql-mcp-server와 같은 버전. `Directory.Packages.props`에 고정.
* 로그는 표준 오류와(설정 시) 파일로만. mssql-mcp-server의 `Program.cs` 구조를 따른다.
* 도구 호출은 한 번에 하나만 실행한다(잠금). Roslyn 로드가 프로세스 환경 변수를 바꾸기 때문(`EnglishBuildMessages.Pin`). 백그라운드 기준 만들기(§11)는 이 잠금 밖에서 돌고(빌드·테스트는 자식 프로세스), Roslyn 로드만 같은 잠금을 쓴다. `pelly_verify`는 진행 중인 기준 작업이 있으면 그것을 기다리고, `pelly_impact`는 기다리지 않는다.
* 작업 공간 루트: `--root <폴더>` 인자 → 없으면 현재 폴더. 루트는 git 저장소여야 한다(아니면 두 도구 모두 「git 저장소가 아님」 오류).
* 설정: 루트의 `pelly.json` + 사용자 설정(`%AppData%/pelly/config.json`) + 환경 변수 `PELLY__*`. Core의 `PellyOptionsLoader`를 그대로 쓴다(Cli의 `OptionsResolver`와 같은 순서). `llm` 절은 읽기만 하고 쓰지 않는다.

## 6. `pelly_impact`

**입력:** `path`(루트 기준 상대 또는 절대), `line`(1부터, 선택), `name`(멤버·객체·함수 이름, 선택). `line`과 `name`이 둘 다 없으면 파일 전체가 대상(SQL은 파일의 객체 모두, C#·JS는 오류 「line이나 name을 주세요」).

**확장자별 동작**

| 파일 | 대상 정하기 | 찾는 곳 |
| :--- | :--- | :--- |
| `.cs` | `MemberLocator.FindAtLine` / `FindByName` | Roslyn(`SemanticImpactFinder`), 실패하면 이름 기준(`NameImpactFinder`) |
| `.sql` | 줄이 든 객체 배치, 또는 이름, 또는 파일의 객체 전부 | ASP의 SP 호출(`AspImpactFinder`, 사이트 색인), C# 문자열의 SP 이름(`SqlNameStringFinder`) |
| `.js`, `.asp`·페이지 안 `<script>` | 줄이 든 함수, 또는 이름 | `JsImpactFinder`(사이트 색인) |
| 그 밖 | — | 「지원하지 않는 파일 형식」 |

ASP·JS·SQL→ASP는 설정에 `asp` 또는 `web` 절이 있을 때만. 없으면 그 부분은 「설정에 asp 절이 없어 건너뜀」.

**출력(텍스트, 한국어):** 한 줄에 한 곳.
```
대상: OrderService.CancelAsync (Modern/Shop.Api/Services/OrderService.cs:42)
부르는 곳 3곳 (Roslyn 의미 분석):
- Modern/Shop.Api/Controllers/OrderController.cs:57 OrderController.Cancel — 호출
- ...
```
* 상한 50곳. 넘으면 「외 N곳」.
* 이름 기준으로 대신했거나 색인이 잘렸으면 첫 줄에 그 사실을 적는다(빠진 곳이 있을 수 있음).
* **Core 변경:** `ImpactTarget`에 줄 번호를 더한다(지금은 `SemanticImpactFinder`가 위치를 알지만 버림). pelly CLI 출력은 바뀌지 않게 한다.

**Roslyn 재사용:** 같은 프로젝트 묶음을 다시 부르면 불러 둔 `Solution`을 쓰고, 그 사이 바뀐 `.cs`만 `WithDocumentText`로 갱신한다(파일 수정 시각 비교). `.csproj`가 바뀌면 다시 불러온다. 이를 위해 캐시하는 `ISemanticLoader`를 서버에 둔다(Core의 `SemanticImpactFinder`가 매번 `using`으로 버리는 부분은 Core에서 owner가 null이면 버리지 않으니 그대로 둘 수 있다).

## 7. `pelly_verify`

**입력:** 없음.

**1단계 — 바뀐 파일:** `git status --porcelain=v1 -z --untracked-files=all` (무시 목록 제외). 수정·추가·삭제·이름 바꿈을 얻는다. 바뀐 것이 없으면 「HEAD 이후 바뀐 파일이 없습니다」.

**2단계 — 검사 고르기 (바뀐 파일의 종류로)**

| 바뀐 파일 | 검사 | 「수정 전」 |
| :--- | :--- | :--- |
| `.cs`, `.csproj` | 바뀐 파일의 프로젝트 + 그것을 참조하는 프로젝트(`ProjectReferenceGraph.AffectedProjects`)를 빌드. 그 프로젝트를 참조하는 테스트 프로젝트(`TestProjectLocator`) 실행 | HEAD 기준 사본의 빌드·테스트 결과(아래) |
| `.sql` | 파일의 객체 배치마다 `NOEXEC` 컴파일 | 같은 파일의 HEAD 텍스트를 같은 방식으로 컴파일 |
| `.asp` 등 ASP 확장자 | VBScript 구문(cscript, Windows만), 이름·요소·폼 필드 검사 | HEAD 텍스트 |
| `.js` | JS 이름·요소·폼 필드·jQuery API 검사, 서명이 바뀐 함수의 남은 호출 | HEAD 텍스트 |
| `.css` | CSS 속성 검사 | HEAD 텍스트 |

**「수정 전」의 원칙(에이전트 판단):** 「수정 전」은 **지금 작업 트리에서 바뀐 파일만 HEAD 내용으로 되돌린 상태**다. 바뀌지 않은 파일(include 대상, 사이트 색인, 다른 페이지)은 양쪽 모두 지금 트리를 쓴다. 그래서 HEAD 전체의 사이트 색인이나 include 해석을 따로 만들 필요가 없고, 빌드 기준 사본과도 원칙이 같다.

**바뀌지 않았지만 영향받는 파일(에이전트 판단):**
* `.inc` 등 include 파일이 바뀌면, 그것을 include하는 페이지(사이트 색인의 includer)도 같은 페이지 검사를 한다(include 대상 쪽만 HEAD/지금으로 바꿔 끼움). 페이지 수 상한 50, 넘으면 「외 N개 페이지는 검사하지 않음」.
* `.js` 함수의 서명이 바뀌면 그 함수를 부르는 곳의 인자 수 불일치를 알린다(`JsSignatureChanges` + `JsImpactFinder`, pelly apply 끝 검사와 같음).
* `.css`가 바뀌어도 그것을 쓰는 페이지는 검사하지 않는다(v1 비목표).

페이지·스크립트 검사는 `RunContext`를 만들지 않고 Core의 순수 검사 함수(`AspNameCheck.NewUnknown`, `PageElements.NewBroken`, `JsNameCheck.NewUnknown`, `CssPropertyCheck.NewUnknown`, 폼 필드 검사 등)를 HEAD 텍스트와 지금 텍스트로 부른다. internal인 것은 `InternalsVisibleTo`로 연다.

**HEAD 기준 사본 (빌드·테스트용):**
* **사본 위치는 작업 공간 밖**: `%LocalAppData%/PellyCode/mcp-base/<루트 경로 해시>/`(에이전트 판단). 작업 공간 안(`.pelly/`)에 두면 루트에 있는 SDK 형식 `.csproj`가 `**/*.cs`로 사본의 파일까지 컴파일해 가짜 오류가 난다.
* **사본은 계속 두고 차이만 맞춘다**(에이전트 판단): 처음 한 번은 작업 공간 전체를 복사하고(`bin`, `obj`, `.git`, `.pelly`, `.vs`, `node_modules` 제외 — 레거시 빌드에 필요한 `packages/`는 포함), 다음부터는 크기·수정 시각이 다른 파일만 다시 복사하고 없어진 파일은 지운다. 큰 레거시 저장소에서 HEAD가 바뀔 때마다 전체를 복사하지 않기 위함. 그 다음 바뀐 추적 파일은 `git show HEAD:<path>` 내용으로 되돌리고, 새로 추가된 파일은 지운다. 이렇게 하면 git에 없는 로컬 파일(NuGet 설정 등)도 그대로라 「환경 차이로 HEAD 빌드 실패」를 줄인다.
* 사본에서 같은 프로젝트를 빌드·테스트하고 결과를 **HEAD 커밋 + 프로젝트 묶음**을 키로 디스크에 캐시한다. 같은 HEAD에서 다시 verify하면 사본을 다시 만들지 않는다. HEAD가 바뀌면 캐시를 버린다.
* 비교는 pelly와 같다: 빌드는 `BaselineDiff.New`(파일·코드·메시지 기준, 줄 무시), 테스트는 `TestRegressions.Find`(새로 실패한 테스트만).
* HEAD 사본 빌드가 환경 이유로 실패하면 기준 없이 지금 오류를 모두 알리고 첫 줄에 「HEAD 기준을 만들지 못해 모든 오류를 보입니다: <이유>」.

**SQL 연결:** `sql.connectionString`(또는 `PELLY_SQL_CONNECTION`)과 허용 목록(`sql.allowedHosts`·`allowedDatabases`)을 pelly와 같이 쓴다. 없거나 허용 목록 밖이면 SQL 검사는 「건너뜀: 이유」. 운영 연결은 쓰지 않는다.

**출력(텍스트, 한국어):**
```
결과: 새 문제 2건 (바뀐 파일 3개: C# 2, SQL 1)
[빌드] Shop.Api — 새 오류 1건
- Modern/Shop.Api/Services/OrderService.cs:229 CS1503 Argument 1: cannot convert ...
[테스트] Shop.Api.Tests — 새로 실패 1건
- Shop.Api.Tests.OrderServiceTests.Cancel_Shipped_Throws: Expected InvalidOperationException ...
[SQL] Sql/objects/fn_OrderTotal.sql — 통과
[건너뜀] ASP 구문 검사 — Windows가 아님
```
* **오류와 경고를 나눈다(에이전트 판단).** 오류 = 새 빌드 오류, 새로 실패한 테스트, 새 SQL 컴파일 오류, 새 VBScript 구문 오류, 페이지에서 정의되지 않은 이름(pelly에서도 실패로 치는 `AspNames`). 경고 = 그 밖의 추정 검사(JS 이름·요소·폼 필드·CSS 속성·jQuery API·서명 불일치 호출). 첫 줄은 「결과: 새 오류 N건, 새 경고 M건」. 레거시 페이지의 추정 경고로 Qwen이 고치기를 끝없이 되풀이하지 않게 하기 위함이다.
* 새 문제가 없으면 첫 줄 「결과: 새 문제 없음」.
* 각 묶음 상한 20줄, 넘으면 「외 N건」.

**시간(에이전트 판단, 기준 미리 만들기는 §11의 사람 결정):**
* **측정 결과(2026-11-17, 개발 PC OpenCode 1.18.25, Qwen3.6 35B, 시험 도구 `sleep(seconds)`):**
  * 진행 알림도 `timeout` 키도 없으면 **60초**에 끊긴다(`MCP error -32001: Request timed out`; 30초는 통과, 90초는 정확히 60초 뒤 실패).
  * 등록의 `timeout` 키는 실행 시간에 **듣는다**. `timeout: 600000`이면 300초는 통과, 900초는 정확히 600초에 같은 오류로 끊겼다.
  * **진행 알림(10초마다)은 한도를 연장한다.** `timeout` 키 없이도 90·300·900초 호출이 모두 끝까지 갔다(900초 호출은 964초 만에 정상 응답). 상한은 900초까지 확인했고 그 위는 재지 않았다.
  * 결정: 진행 알림을 10초 이하 간격으로 보낸다는 전제로 `VerifyService.DefaultWaitLimit` = **600초**(상한; 확인한 900초에서 15초를 빼도 600 이상). 알림을 보낼 수 없는 경우의 대비값은 60초 − 15초 = 45초. 등록 예의 `timeout`은 **660000**(대기 한도 600초 + 60초)으로 둔다. 알림이 어떤 이유로 안 가도 600초까지는 끊기지 않게 하는 안전망이다.
  * 측정에서 얻은 주의: `opencode run`은 표준 입력이 터미널이 아니면(파이프·백그라운드) 시작 단계에서 멈출 수 있다. 자동 실행 스크립트는 `< /dev/null`을 붙인다.
* 서버는 오래 걸리는 단계마다 MCP 진행 알림을 보낸다.
* 정해진 한도 안에 끝나지 않으면 「기준 빌드가 아직 진행 중입니다. 잠시 뒤 다시 pelly_verify를 부르세요」를 돌려주고, 작업은 서버 안에서 계속한다(다음 호출이 이어받음).
* 빌드·테스트 제한은 `build.timeoutSeconds`·`test.timeoutSeconds`(pelly 설정 그대로). 도구 취소(OpenCode가 끊음)가 오면 자식 프로세스 트리를 죽인다(`SystemProcessRunner`가 이미 함).

## 8. 시작·점검·반입

* `PellyCodeMcpServer.exe --check`: MCP 없이 루트, git, 설정, .NET SDK·MSBuild, BuildHost 폴더, SQL 연결(있으면), 도구 목록을 점검해 표준 출력에 쓰고 끝낸다. 실패가 있으면 종료 코드 1. 검사 내용은 `pelly doctor`의 해당 행과 같은 판정을 쓴다.
* 반입: pelly 반입 zip에 함께 넣는다(에이전트 판단 — BuildHost 폴더를 같이 써야 하므로 같은 폴더가 간단). CI는 pelly와 서버를 **같은 출력 폴더에** publish하고, 두 프로젝트가 같은 Roslyn·MSBuild 패키지 버전을 쓰는지(같은 `BuildHost-*` 내용인지) 확인한다. `build-package.ps1`은 한 폴더에서 실행 파일 둘을 읽도록 고친다. `deploy/package-files.txt`, CI 고정 파일 수, SBOM·고지 문서를 함께 고친다. 실행 파일 100MB 상한 검사 대상에 넣는다.
* 문서: `deploy/mcp-server.md`(OpenCode 등록 예, `codemode` 주의, `--check`, AGENTS.md에 넣을 한 줄). `deploy/field-checklist.md`에 MCP 확인 항목을 더한다(작업 규칙: 기능이 바뀌면 현장 확인 안내도 고침).
* 외부 통신 없음: `NetworkPolicyTests`가 `src/` 전체를 훑으므로 새 프로젝트도 자동으로 들어간다. `deploy/no-network.md`의 표에 새 프로젝트를 더한다.

**OpenCode 등록 예 (현장 OpenCode 버전에서 형식 확인 필요):**
```json
"mcp": {
  "pelly": { "type": "local", "command": ["C:\\Tools\\pelly\\PellyCodeMcpServer.exe", "--root", "D:\\src\\shop"] }
}
```

**AGENTS.md 권장 줄:** 「코드를 고친 뒤에는 끝내기 전에 반드시 `pelly_verify`를 부르고, 새 문제가 있으면 고친 뒤 다시 부른다. 메서드·SP·JS 함수의 이름이나 매개변수를 바꾸기 전에는 `pelly_impact`로 부르는 곳을 확인한다.」

## 9. 확인 방법

* 단위·통합 테스트: 각 도구를 가짜 git 저장소(임시 폴더, `git init`)로 시험 — 새 오류만 나오는지, HEAD에 있던 오류는 빠지는지, 바뀐 파일 없음, git 아님, SQL 연결 없음 건너뜀, 표준 출력 오염 없음(MCP 클라이언트로 띄워 첫 메시지가 JSON-RPC인지).
* MCP 프로토콜 시험: SDK의 클라이언트로 서버를 띄워 도구 목록 2개와 호출 결과를 확인(mssql-mcp-server의 통합 테스트 방식).
* **효과 측정(이 계획에 포함, 에이전트 판단):** 2026-11-17 비교 연결은 커밋되지 않은 채 옛 이름 워크트리(`Pelly-Code-oc-compare`)에만 있다. 계획의 마지막 작업에서 그 연결(`ExternalAgent.cs`, `opencode-agent.sh`)을 새 이름으로 옮겨 커밋하고, OpenCode 설정에 이 서버를 등록하는 변형을 더해 50건을 「OpenCode + MCP」로 돌린다. 볼 것: 성공 건수, 깨진 채 끝남(목표 0), `pelly_verify`를 부른 작업 비율, 작업 시간. AGENTS.md 권장 줄을 넣은 경우와 뺀 경우를 함께 잰다.

## 10. 만드는 사람에게 맡기는 것

(아래와 함께, §7의 상한값·캐시 정리 주기·진행 알림 간격도 맡긴다.)

* 도구 설명 문구(짧게, 탐색을 부추기지 않게 — mssql-mcp-server 커밋 `597d804`의 교훈).
* HEAD 기준 사본의 캐시 형식과 정리(오래된 사본 지우기).
* 출력 문구의 세부.
* `--check`의 표 모양.

## 11. 사람이 정한 것 (2026-11-17, 초안 검토 중)

**HEAD 기준을 미리 만든다.**
* 서버가 켜질 때와 HEAD 커밋이 바뀐 것을 알았을 때(도구 호출마다 `git rev-parse HEAD` 확인, 그리고 30초마다), 백그라운드에서 §7의 HEAD 기준 사본을 맞추고 빌드·테스트를 돌려 캐시한다.
* 대상 프로젝트: 미리 만들 때는 아직 바뀐 파일을 모르므로, 루트의 솔루션(`.sln`/`.slnx`)에 든 프로젝트 전부를 빌드하고, 그중 테스트 프로젝트(§7과 같은 규칙, `test.projects` 우선)를 실행한다. 솔루션이 없으면 미리 만들지 않고 verify 때 만든다.
* 화면에는 아무것도 띄우지 않는다. 상태는 로그와 `--check`에만 남긴다.

**미리 만든 빌드가 실패하면:**
1. **HEAD 코드에 컴파일 오류·실패 테스트가 있으면** 그것이 정상적인 기준이다. verify는 그것을 빼고 새로 생긴 것만 알린다.
2. **환경 문제로 빌드·테스트를 못 돌리면**(복원 실패, MSBuild 없음, 시간 초과, 복사 실패, 테스트 호스트 비정상 종료) 이유를 기록하고 백그라운드에서 다시 시도하지 않는다. 다시 시도는 HEAD가 바뀔 때와 verify가 불릴 때 한 번. verify 때도 못 만들면 지금 상태의 오류·실패를 모두 보이고 첫 줄에 「HEAD 기준을 만들지 못해 모든 오류를 보입니다: <이유>」(테스트만 못 돌렸으면 「HEAD에서도 실패했을 수 있음」).
3. **만드는 중에 서버가 꺼지면** 끝나지 않은 결과는 캐시에 쓰지 않는다.
* `--check`는 마지막 기준 작업의 상태(성공·실패·이유·HEAD 커밋)를 보인다.

**테스트 프로젝트가 없으면:**
* 테스트 단계만 건너뛰고 빌드는 검사한다. 출력에 「[테스트] 건너뜀 — <프로젝트>를 참조하는 테스트 프로젝트 없음. 동작 변경은 확인되지 않았습니다」. 오류 수에 넣지 않는다.
* 이름 규칙에 맞지 않는 테스트 프로젝트는 `test.projects`로 지정한다. 레거시 테스트 프로젝트는 pelly와 같이 `test.command`가 필요하다.
* 테스트 만들기(`pelly test`)는 LLM이 필요해 이 서버의 범위 밖이다. 사람이 pelly CLI로 미리 만들어 두면 verify가 그 테스트를 쓴다(`deploy/mcp-server.md`에 안내).

