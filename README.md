# Pelly-Coder CLI

폐쇄망 로컬 LLM(`Qwen3.6-35B-A3B`, Ollama)으로 C# 메서드와 SQL 저장 프로시저를 수정하고, 컴파일러·DB 엔진으로 검증하는 코딩 CLI입니다. Classic ASP(VBScript) 페이지도 실행 없이 할 수 있는 판정(구조·안전·이름·`cscript` 컴파일·SP 존재)으로 고칩니다(`pelly asp`). 클라이언트 JavaScript(jQuery)·CSS는 따로 있는 파일뿐 아니라 ASP·HTML 페이지 안 `<script>`·`<style>` 블록도 브라우저 실행 없이 문법·문법 수준·안전 판정으로 고칩니다(`pelly js`·`pelly css`).

## 문서
- [PRD](docs/PRD.md)
- [v1 설계](docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md)
- 현장 모델 파일 기준선(M4): [`qwen3.6:35b-a3b-q4_K_M` 50건 × 3회](docs/m4-field-model-baseline.md) (이전: [`qwen3.6:35b`](docs/m4-gguf-baseline.md))
- 프로젝트 규칙(V2-3) 외부 사전 측정: [규칙 있음/없음 50건 × 3회](docs/v2-3-rules-premeasure.md)
- 벤치마크·튜닝 기록(M3b): [기준선 50건 × 3회](docs/m3b-baseline.md) · [튜닝(채택 없음)](docs/m3b-tuning.md) · [S6 단일 파일](docs/spikes/S6-single-file.md) · [S7 Rider 확인표](docs/m3b-s7-rider.md)
- 반입·운영 가이드: [설치](deploy/install.md) · [사용](deploy/usage.md) · [IDE External Tools](deploy/ide-external-tools.md) · [서버 확인 사항](deploy/server-checklist.md) · [외부 통신 없음](deploy/no-network.md)
- [구현 계획](docs/superpowers/plans/)

## 빌드·테스트
```bash
export AVALONIA_TELEMETRY_OPTOUT=1   # Avalonia 빌드 시점 사용 정보 수집 끄기 (PowerShell: $env:AVALONIA_TELEMETRY_OPTOUT = "1")
dotnet build -warnaserror
dotnet test
dotnet run --project src/PellyCoder.Cli -- --version
```

## 사용법

### `pelly edit` — 확장자로 언어를 골라 수정
```bash
pelly edit <파일> ["<요청문>"] [--line N] [언어별 대상 옵션] [공통 옵션]
```
파일 확장자를 보고 `pelly cs`·`pelly sql`·`pelly asp`·`pelly js`·`pelly css` 중 하나로 그대로 넘깁니다. 동작·검증·종료 코드는 넘겨받은 명령과 같습니다. IDE 외부 도구에 메뉴를 언어마다 따로 만들지 않고 이 명령 하나만 등록하면 됩니다.

| 확장자 | 실행 | 쓸 수 있는 대상 옵션 |
| :--- | :--- | :--- |
| `.cs` | `pelly cs` | `--class`, `--method`, `--line`, `--test`, `--verify-test`, `--expect-break`, `--strict-baseline`, `--warn-as-error` |
| `.sql` | `pelly sql` | `--object`, `--line` |
| `asp.extensions`(기본 `.asp`, `.inc`) | 대상에 따라 `pelly asp`·`pelly js`·`pelly css` (아래) | `--proc`, `--function`, `--selector`, `--line`, `--lines` |
| `.htm`, `.html` (`asp.extensions`에 없을 때) | `pelly js`·`pelly css` | `--function`, `--selector`, `--line`, `--lines` |
| `.js` | `pelly js` | `--function`, `--line`, `--lines` |
| `.css` | `pelly css` | `--selector`, `--line`, `--lines` |

* **페이지(`asp.extensions` + `.htm`·`.html`)는 대상을 보고 고릅니다.** `--function`은 js, `--selector`는 css, `--proc`은 asp입니다. `--line N`은 N번 줄이 클라이언트 `<script>` 안이면 js, `<style>` 안이면 css, 그 밖이면 asp입니다. `--lines A-B`는 범위 전체가 블록 하나의 내용 안이면 js·css, 아니면 asp입니다(걸친 블록은 `pelly asp`가 JS·CSS 판정을 더합니다). 지정이 없으면 asp이고, `.htm`·`.html`이면 js입니다. 고른 결과는 한 줄로 보입니다. 예: `admin/order/list.asp 214줄 → <script> 블록이라 pelly js로 실행합니다`.
* `.htm`·`.html`은 HTML 부분을 고치지 않습니다. 그래서 asp로 가야 할 지정(`--proc`, 블록 밖 `--line`, 블록에 걸친 `--lines`)이면 「.htm 파일의 HTML 부분은 고치지 않습니다」와 함께 종료 4입니다. `.html`을 `asp.extensions`에 넣었다면 그 파일은 ASP 페이지로 다룹니다.
* 대소문자는 구분하지 않습니다(`.CS`도 C#).
* 다른 언어의 옵션을 주면 실행 전에 멈춥니다. 예: 「--method은(는) .sql 파일에 쓸 수 없습니다」(종료 4).
* 목록에 없는 확장자는 「지원하지 않는 파일 형식입니다: .txt」로 멈춥니다(종료 4). ASP 확장자는 설정 `asp.extensions`를 따릅니다.
* 공통 옵션(`--yes`, `--dry-run`, `--max-retries`, `--verbose`, `--pause-on-exit`, `--config`, `--think`, `--model`, `--endpoint`)은 그대로 넘깁니다.
* 실행 첫 줄에 어느 명령으로 넘겼는지 보입니다. 예: `.asp 파일 → pelly asp로 실행합니다`.
* `pelly test`(동작 고정 테스트)와 `pelly ui`는 언어 대신 하는 일이 다른 명령이라 `edit`에 포함하지 않습니다.

### `pelly cs` — C# 멤버 하나 수정
```bash
pelly cs <파일> ["<요청문>"] [--class <클래스>] [--method <메서드>] [--line N] [--yes] [--dry-run] \
  [--max-retries N] [--strict-baseline] [--warn-as-error] [--test] \
  [--verify-test <이름>]... [--expect-break <이름>]... [--verbose] [--pause-on-exit] \
  [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
```

**대상 지정 방법 (3가지)**
1. `--method <이름>` (+ 선택 `--class <클래스>`): 같은 이름이 여러 개면 `--class`로 좁힙니다.
2. `--line N`: IDE 커서 위치의 줄 번호. 그 줄을 포함하는 멤버를 고칩니다. `--class`/`--method`와 함께 쓸 수 없고 1 이상이어야 합니다.
3. 둘 다 생략: 요청문에 나온 식별자로 찾고, 없으면 모델이 멤버 목록(최대 60개)에서 고릅니다.

* 요청문을 생략하면 대화형 실행에서는 `수정 요청을 입력하세요:`로 물어보고, 비대화형(입력 리다이렉트)에서는 종료 코드 4로 끝납니다.
* 순서: 패치를 파일에 **먼저 쓰고**(APPLY) → 검증(VERIFY) → 통과하면 diff를 보여주고 승인을 묻습니다(REVIEW). 거부하면 원래 파일로 되돌립니다.
  승인 질문이 떠 있는 동안 파일은 이미 바뀐 상태이므로, IDE나 파일 감시 도구(빌드 watch 등)가 승인 전 변경을 볼 수 있습니다.
* 모델은 대상 멤버 안을 고치면서 **private 도우미 메서드를 추가**할 수 있습니다. 시그니처 변경과 다른 멤버 수정은 거부되어 재시도됩니다.
* 요청문이나 대상 파일에 모델 제어 토큰 문자열(`<|im_end|>` 등)이 있으면 서버를 부르기 전에 종료 코드 4로 멈춥니다.
* 파일에 CRLF와 LF가 섞여 있으면 알림을 보여주며, 수정하면 줄바꿈이 많은 쪽으로 통일됩니다.
* 수정한 C# 문자열 리터럴에 SQL(`SELECT`·`INSERT`·`UPDATE`·`DELETE`·`EXEC`)이 있으면 승인 전에 "인라인 SQL이 바뀌었습니다 — 빌드는 통과했지만 DB로 검증되지 않았습니다" 경고를 보여줍니다(`--yes`일 때도).
* `--yes`는 질문 없이 승인, `--dry-run`은 파일을 쓰지 않고 diff만 보여줍니다.
* 실행 기록은 작업 공간의 `.pelly/runs/<runId>/`(`summary.json`, `events.jsonl`, `attempt-*/`)에 남습니다.

**빌드 검증**
* 파일을 쓴 뒤 구문 검사에 더해 프로젝트를 빌드해 확인합니다. **원래 파일에 이미 있던 오류는 세지 않으며** 그 사실을 알림으로 보여줍니다. `--strict-baseline`을 주면 원래 있던 CS 오류도 종료 코드 4로 처리합니다.
* 원본 프로젝트 자체가 컴파일되지 않으면(패키지 복원 안 됨 등 CS가 아닌 오류, 예: NETSDK1004) 종료 코드 4로 멈추고 먼저 복원·빌드하라고 안내합니다.
* `--warn-as-error`는 새로 생긴 경고도 오류로 취급합니다(`build.warnAsError`).
* 레거시 .NET Framework 프로젝트는 **Windows에서만** 빌드 검증합니다(MSBuild를 vswhere 또는 `build.msbuildPath`로 찾음). 그 밖의 환경에서는 알림과 함께 구문 검사만 합니다.
* csproj가 없으면 구문 검사만 합니다.

**`--test` — 테스트까지 검증**
* 대상 프로젝트를 참조하고 이름에 `Test`가 들어간 테스트 프로젝트(또는 `test.projects`로 지정한 것)를 수정 전후로 실행해 **새로 실패한 테스트만** 실패로 봅니다. 원래 실패하던 테스트는 세지 않습니다. 테스트 실행 제한은 `test.timeoutSeconds`(기본 300초)입니다.
* 프로젝트 종류는 csproj에서 판정합니다: xunit v3와 VSTest는 테스트별로 비교하고, 그 밖의 Testing Platform 프로젝트는 종료 코드만 비교합니다. 레거시 프로젝트는 `test.command`(`{project}`·`{dll}`·`{trx}`)가 필요하며 Windows에서는 확인하지 않았습니다.
* 원본 상태의 테스트를 돌리지 못하면 알림과 함께 빌드로만 검증합니다.
* **한계:** 요청이 기존 테스트와 모순되면(테스트가 고정한 옛 동작을 바꾸라는 요청 등) 모델이 요청을 로그 출력으로 바꾸는 식으로 회피해 테스트를 통과하고 종료 0이 될 수 있습니다(실측 약 25%). `--test`를 써도 **diff를 직접 읽고** 승인하십시오.

**`--verify-test` — 테스트 먼저, 그다음 패치 (V2-2a)**
* `--verify-test <이름>`(여러 번 가능)은 **패치 전에 실패하고(RED) 패치 뒤에 통과해야(GREEN)** 하는 테스트를 지정합니다. 이름은 `[네임스페이스.]클래스.메서드` 또는 `메서드`이며, 점 경계 뒷부분이 일치하면 맞고 대소문자를 구별합니다. 데이터 행(`Theory`)은 모든 행이 통과해야 통과입니다. `--test`를 켠 것과 같고 회귀 확인에 같은 실행 결과를 씁니다. `--dry-run`에서는 테스트를 돌리지 않습니다.
* 순서: 기준 실행에서 지정 테스트가 실패하는지 확인(RED) → 패치 → 구문·규칙·빌드·테스트. 지정 테스트가 통과하고(GREEN) 실행한 모든 테스트 프로젝트(대상의 테스트 프로젝트와 지정 테스트를 선언한 프로젝트의 합집합)의 다른 테스트가 새로 실패하지 않아야 합니다(회귀). 아니면 실패한 테스트 이름·assert 메시지·대상 파일의 스택 프레임을 모델에 돌려주며 재시도하고, 재시도를 다 쓰면 원본으로 되돌리고 종료 1입니다.
* RED에서 지정 테스트가 **이미 통과**하면 「이 테스트는 변경을 검증하지 못합니다」 경고를 보입니다. 대화형이면 계속할지 묻고(거부하면 종료 2), `--yes`이거나 비대화형(터미널 없음)이면 묻지 않고 기록한 뒤 계속합니다.
* `--expect-break <이름>`(여러 번 가능, `--verify-test`와 함께만): 요청이 바꾸는 동작을 기존 테스트가 고정해 두었을 때, 이 변경으로 실패해도 되는 테스트를 회귀 판정에서 뺍니다. 패치 뒤에도 통과하면 알림만 남깁니다. 같은 테스트를 두 옵션에 함께 줄 수 없습니다.
* 테스트 프로젝트는 `--test`와 같이 찾고, 지정 테스트가 선언된 파일의 가장 가까운 프로젝트를 더합니다. 필터(`--filter`)나 `vstest.console` 없이 프로젝트 전체를 돌려 지정 테스트의 결과를 읽습니다. 레거시 프로젝트는 `test.command`에 `{trx}`가 있어야 합니다.
* **v1 `--test`와 다른 점:** 판정할 수 없으면 알림 뒤 빌드로 내려가지 않고 **종료 4**로 멈춥니다.
* 종료 4가 되는 경우(편집 전, 판정 불가, 또는 실행 중 지정 테스트 파일이 바뀌었을 때):
  * 서버 호출 전 인자 검사: 이름 형식 오류, `--expect-break`만 줌, 같은 이름을 두 옵션에 줌.
  * 수정 대상에 csproj가 없음(`csproj가 없어 테스트 우선 검증을 할 수 없습니다`); 레거시 프로젝트를 Windows 밖에서 실행.
  * 지정 테스트를 작업 공간에서 찾지 못함(테스트 특성이 붙은 메서드만 찾는다고 안내)·이름이 여러 테스트와 맞음(후보를 나열); 지정 테스트가 어떤 테스트 프로젝트에도 속하지 않음; 테스트 선언 파일을 읽지 못함.
  * `--verify-test`가 수정 대상 멤버 자신을 가리킴.
  * 기준 테스트를 돌리지 못함; 원본 상태에서 테스트 프로젝트가 빌드되지 않음(새 시그니처로 쓴 테스트는 단독 실행에서는 받지 않습니다 — `pelly apply`의 작업 그룹은 이를 컴파일 RED로 받습니다); 테스트별 결과가 없음(`OtherTestingPlatform`, `{trx}` 없는 레거시 `test.command`); 기준 결과에 지정 테스트가 없거나 건너뛰도록 표시됨.
  * 실행 중 지정 테스트 파일이 바뀜(GREEN을 인정하지 않음).
* 종료 코드 요약: 0 GREEN·회귀 없음, 1 GREEN 실패(`TestGreenFailed`)·회귀(`TestRegression`)·테스트 프로젝트에 새 컴파일 오류(`TestErrors`), 2 「이미 통과」 질문에서 계속하지 않음(`TestRedDeclined`)·diff 승인 거부(`Rejected`), 4 위 사유(`TestNotFound`·`TestAmbiguous`·`TestUnverifiable`·`TestFileChanged`; 파일 읽기 실패는 실패 유형 없이 종료 4).
* `pelly report`는 수용 기준 충족률(RED를 통과했고 사람이 취소·거부하지 않은 실행 중 GREEN·회귀 없음·종료 0인 비율, 종료 2 제외)과 RED 확인 경고율을 냅니다. `summary.json`의 `testFirst`에는 개수와 상태만 있고 테스트 이름은 없습니다.
* **알려진 한계:** 환경(DB·네트워크) 탓으로 실패하는 테스트도 RED로 보입니다(RED 실패 메시지 첫 줄은 실행 폴더의 `messages.txt`에 늘 남고 `--verbose`면 화면에도 보임). 모델이 테스트 입력에만 맞는 분기를 넣어 GREEN을 만들 수 있으므로 **diff 승인이 마지막 방어선**입니다. 테스트는 한 번씩만 돌려 불안정한 테스트는 우연히 판정이 바뀔 수 있고, 시도마다 테스트 프로젝트 전체를 돌아 큰 프로젝트에서는 `test.timeoutSeconds`·`test.projects`로 조절합니다. `pelly sql`에는 없습니다. 명세의 `test:`·`breaks:`는 아래 `pelly apply` 절을 보십시오.
  * 테스트 찾기는 Fact·Theory·Test·TestCase·TestMethod·DataTestMethod 특성만 봅니다: 사용자 정의 특성(`SkippableFact`·`WpfFact` 등)이 붙은 테스트는 찾지 못합니다(종료 4).
  * 추상 기반 클래스에 선언된 테스트나 `DisplayName`을 준 테스트는 찾기를 통과해도 실행 결과의 이름이 달라 RED에서 찾지 못할 수 있습니다(종료 4). 제네릭 테스트 클래스(결과 이름 ``Foo`1``)도 맞추지 못합니다. 중첩 클래스는 `Outer.Inner.M`으로 씁니다(결과의 `Outer+Inner`와 맞춥니다).
  * 피드백의 스택 프레임은 영어(`at … in …:line N`)와 한국어 .NET Framework(`위치: … 파일 …:줄 N`) 형식을 읽습니다. 한국어 형식은 픽스처로만 확인했고 Windows 실환경은 미확인이며, 그 밖의 지역화 형식에서는 프레임이 없습니다.
  * `--expect-break`로 뺀 테스트(`BreaksExcluded`)는 `summary.json`에 기록되고 `pelly report`의 `breaks로 뺀 테스트` 줄에 더해집니다.

**실행 후 표시 옵션**
* `--verbose`: 실행이 끝난 뒤 단계별 시간·토큰·시도 요약과 실행 폴더 경로를 보여줍니다. 프롬프트 원문은 출력하지 않고 `attempt-N/prompt.txt` 경로를 안내합니다(`pelly sql`에서도 같습니다).
* `--pause-on-exit`: 결과를 보여준 뒤 키 입력을 기다립니다. IDE External Tools 창이 바로 닫히지 않게 할 때 씁니다. 열한 명령(`cs`·`sql`·`sql pull`·`sql check`·`doctor`·`report`·`rules init`·`rules check`·`spec new`·`spec check`·`apply`) 모두의 옵션입니다. 대기 여부는 `Program`이 `--` 앞의 원시 인자로 판정하므로 인자 오류로 끝나도 기다립니다. 입력이 리디렉션되거나 종료 신호를 받으면 기다리지 않습니다. Ctrl+C로 중단한 실행은 기다립니다. 키 대기는 Windows에서 확인하지 않았습니다.

예:
```bash
pelly cs Shop.Api/Services/OrderService.cs "배송 중인 주문은 취소할 수 없게 해줘" --method CancelAsync --test --verbose
pelly cs Shop.Api/Services/OrderService.cs "배송 중인 주문은 취소할 수 없게 해줘" --method CancelAsync \
  --verify-test OrderServiceTests.CancelAsync_WhenShipped_Throws --expect-break OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels
pelly cs Shop.Api/Services/OrderService.cs "null 검사 추가" --line 42 --pause-on-exit
pelly cs Shop.Api/Services/OrderService.cs        # 요청문을 대화형으로 입력
```

### `pelly sql` — 저장 프로시저·함수·뷰 하나 수정
```bash
pelly sql <파일.sql> ["<요청문>"] [--object <이름>] [--line N] [--yes] [--dry-run] \
  [--max-retries N] [--verbose] [--pause-on-exit] [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
```

* 파일은 SSMS가 스크립트한 형태(`SET ANSI_NULLS ON` / `GO` / `CREATE PROCEDURE …` / `GO`)나 여러 객체가 든 파일이어도 됩니다. 수정할 수 있는 것은 **대상 객체 배치 하나**이고, 같은 파일의 다른 배치가 바뀌면 거부됩니다.
* **대상 지정:** 파일에 `CREATE`/`ALTER PROCEDURE|FUNCTION|VIEW`가 하나면 자동, 여럿이면 `--object dbo.이름` 또는 `--line N`(그 줄을 포함하는 정의)으로 지정합니다. 지정이 없으면 대화형은 선택 메뉴, 비대화형은 종료 코드 3입니다. `--line`과 `--object`는 함께 쓸 수 없습니다.
* 검증은 **DB 서버가 합니다**: 참조 객체가 존재하는지(`OBJECT_ID`)와 `SET NOEXEC ON`으로 대상 배치를 컴파일해 새 오류가 없는지 봅니다(트랜잭션 안에서 실행하고 롤백하며, 객체는 실제로 바뀌지 않습니다). 원본에 이미 있던 오류는 세지 않습니다. 서버 연결이 필수이고, 연결할 수 없으면 종료 코드 4입니다.
* 동적 SQL 안의 문장, `#임시테이블`의 컬럼, 다른 DB·연결된 서버(3·4부 이름) 객체는 검증하지 못하며 알림만 보여줍니다.
* 파일에 쓰기 전에 **안전 가드**가 검사합니다: 대상 밖 배치 변경, 최상위 문장 수 변경, 금지 구문(`GRANT`/`REVOKE`/`DENY`, `xp_cmdshell`, `OPENROWSET`/`OPENDATASOURCE`, `SET NOEXEC`·`PARSEONLY`·`FMTONLY`, `ALTER DATABASE`류, 로그인 문장, `SHUTDOWN`, `RECONFIGURE`)은 거부되어 재시도되고, 끝내 못 고치면 종료 코드 5입니다. **원본 대상에 이미 금지 구문이 있으면** 모델을 부르기 전에 종료 코드 5로 멈춥니다(고칠 수 없는 프로시저).
* 구문 오류가 있는 SQL을 만들면 파일에 쓰기 전에 걸러 재시도합니다(소진하면 종료 코드 1).
* **명령 묶음:** `pelly sql`은 이제 명령 묶음입니다. `pelly sql <파일> …`은 위와 같이 수정 명령이고, `pelly sql pull`·`pelly sql check`가 더해졌습니다. 파일 이름이 `pull`이나 `check`이면 `./pull`·`./check`로 쓰십시오. 그 밖의 인자 오류(위치 인자가 너무 많음, `--line` 값이 숫자가 아님)는 `Unknown command '<파일>'`을 보이고 종료 코드 4로 끝나며 아무것도 실행하지 않습니다. 없는 `--옵션`은 `Unknown option '<이름>'.`으로 알립니다(종료 코드 4).
* **운영 연결이 있으면(`sql.production`):** 대상 객체를 찾은 뒤 모델을 부르기 전에 운영 정의와 비교합니다(읽기 조회만). `pelly sql pull`로 받은 뒤 운영에서 바뀌었으면 메시지(`받은 뒤 운영에서 <dbo.이름>가 바뀌었습니다 …`)를 보이고 종료 코드 4(`SqlProdChanged`)로 멈춥니다. 그 밖의 경우(받은 기록 없이 다름, 운영에 없음, 운영 접속 불가)는 알림만 보이고 계속합니다. 파일이 운영과 같은데 받은 기록이 운영보다 오래되었으면(고친 파일을 이미 SSMS로 반영한 경우) `받은 기록을 운영 정의로 갱신합니다 (이 파일은 이미 운영에 반영되어 있습니다)`를 알리고 계속하며, pelly가 쓰는 파일의 받은 기록 줄(첫 줄)만 운영 해시·지금 시각(UTC)으로 바꿉니다. 그 밖의 대상 밖 변경은 전처럼 거부합니다. `--dry-run`이면 쓰지 않고(알림 끝에 ` (--dry-run: 파일은 쓰지 않음)`), 검증 실패·거절이면 원래 파일(옛 기록)로 되돌립니다. 받은 기록 줄이 대상 객체 배치 안에 있으면(SET 줄 없이 바로 정의) 갱신하지 않고 `받은 기록이 운영보다 오래되었습니다 — 고치기 전에 pelly sql pull <dbo.이름> --force로 다시 받으십시오`를 알립니다. 실행 기록 `summary.json`의 `sqlProd.recordRefreshed`에 갱신 여부가 남습니다. 운영 연결이 없으면 동작·출력이 이전과 같습니다.
* 운영 연결이 있을 때 `pelly apply`가 `--dry-run`이거나 승인을 거부해도 `.pelly/runs/apply-…/`에 `impact-sql.json`만 남을 수 있습니다.

예:
```bash
pelly sql Procedures/usp_CancelOrder.sql "이미 배송된 주문은 취소하지 않고 오류를 내게 해줘"
pelly sql Settlement_All.sql "월 정산에 수수료 컬럼 추가" --object dbo.usp_SettleMonthly
pelly sql Settlement_All.sql "조건 추가" --line 120
```

**SQL 설정** (`pelly.json` 또는 사용자 설정의 `sql` 섹션)

| 키 | 기본값 | 설명 |
| :--- | :--- | :--- |
| `sql.allowedHosts` | `[]` | 접속을 허용할 서버 이름(`tcp:`·포트를 뗀 호스트 또는 `호스트\인스턴스`). **비어 있으면 아무 데도 접속하지 않고 종료 코드 5** |
| `sql.allowedDatabases` | `[]` | 접속을 허용할 데이터베이스. 비어 있으면 위와 같음 |
| `sql.compatLevel` | `160` | 파서 호환성 수준: 90, 100, 110, 120, 130, 140, 150, 160, 170 |
| `sql.connectionString` | `null` | **비밀번호가 없는 연결만** (예: `Server=db01;Database=Shop;Integrated Security=true;TrustServerCertificate=True`). `Password`/`Pwd`가 있으면 설정 오류(종료 코드 4) |
| `sql.production.allowedHosts` | `[]` | 운영(읽기 전용) 접속을 허용할 서버 이름. 개발용 `sql.allowedHosts`와 따로 둡니다. 비어 있으면 운영에 접속하지 않습니다 |
| `sql.production.allowedDatabases` | `[]` | 운영 접속을 허용할 데이터베이스. 비어 있으면 위와 같음 |
| `sql.production.connectionString` | `null` | 운영 읽기 전용 연결. **비밀번호가 없는 연결만**(있으면 설정 오류, 종료 코드 4). 개발 연결(`sql.connectionString`)과 같은 서버·DB이면 설정 오류입니다 |
| `sql.pullDirectory` | `db/pulled` | `pelly sql pull`이 파일을 쓰는 작업 공간 안 폴더 |

```json
{ "sql": { "allowedHosts": ["db01"], "allowedDatabases": ["Shop"], "connectionString": "Server=db01;Database=Shop;Integrated Security=true" } }
```
* 비밀번호가 필요한 연결(SQL 인증)은 환경 변수 `PELLY_SQL_CONNECTION`으로만 지정합니다. 이 변수가 있으면 `sql.connectionString`보다 우선하며, 허용 목록은 이 경우에도 적용됩니다. (`dotnet user-secrets`는 지원하지 않습니다.)
  ```bash
  export PELLY_SQL_CONNECTION="Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=<비밀번호>;TrustServerCertificate=True"
  ```
  PowerShell:
  ```powershell
  $env:PELLY_SQL_CONNECTION = "Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=<비밀번호>;TrustServerCertificate=True"
  ```
  cmd: `set PELLY_SQL_CONNECTION=Server=...` (따옴표 없이)
* 비밀번호가 필요한 운영 연결(SQL 인증)은 환경 변수 `PELLY_SQL_PROD_CONNECTION`으로만 지정합니다. 이 변수가 있으면 `sql.production.connectionString`보다 우선하며, `sql.production.*` 허용 목록은 이 경우에도 적용됩니다.
* **운영 연결은 읽기 조회만 보냅니다:** 카탈로그 뷰·`OBJECT_DEFINITION`·`OBJECT_ID`·`HAS_PERMS_BY_NAME`·`sys.dm_sql_referenced_entities` 조회뿐이고, `SET NOEXEC`·트랜잭션·DDL·DML은 운영으로 보내지 않습니다. 운영 계정에는 DB 수준 `CONNECT`와 `VIEW DEFINITION`만 주십시오([deploy/server-checklist.md](deploy/server-checklist.md)). 운영 연결을 설정하지 않으면 운영 관련 동작은 모두 꺼집니다.
* **Windows 미확인:** SQL 경로는 Windows에서 확인하지 않았습니다(CI의 SQL 테스트는 Linux에서만 돕니다). 코퍼스 배포 스크립트 `eval/corpus/Sql/deploy.sh`는 bash가 필요하므로 Windows에서는 WSL이나 Git Bash로 실행하십시오.
* 허용 목록은 `Data Source`의 `.`, `(local)`, `np:` 형태를 정규화하지 않습니다. 허용 목록과 일치하지 않으면 접속을 거부하므로(fail closed) 호스트 이름을 쓰십시오.
* 연결 문자열에 서버나 데이터베이스 키가 둘 이상 있거나 `Failover Partner`가 있으면 거부합니다(종료 코드 5).
* **검증 계정 권한:** DB 수준 **`VIEW DEFINITION`** 하나면 됩니다(`ALTER`·`SHOWPLAN` 불필요). 없으면 객체 존재 검사와 스키마 요약이 동작하지 않으므로 종료 코드 4와 안내 메시지로 멈춥니다. `pelly doctor`가 접속·허용 목록·권한을 점검합니다. 운영 연결은 별도 행 「SQL 운영(읽기 전용)」으로 점검합니다(연결 없음은 Ok `운영 연결 없음(선택)`, `VIEW DEFINITION`이 없으면 Fail `VIEW DEFINITION 권한 필요`).

### `pelly sql pull` — 서버의 프로시저·함수·뷰를 SSMS 모양의 ALTER 스크립트로 받기
```bash
pelly sql pull <객체> [<객체> ...] [--from prod|dev] [--out <폴더>] [--force] [--config <경로>] [--pause-on-exit]
```
* 기본은 운영(`sql.production`, 읽기 조회만)에서 받고, 운영 연결이 없으면 종료 코드 4와 설정 안내로 멈춥니다. 개발 DB에서 받으려면 `--from dev`를 명시합니다.
* `<객체>`는 `dbo.usp_X`, `usp_X`(스키마 생략 시 `dbo`), `[dbo].[usp_X]`. 프로시저·함수·뷰만 받습니다. 그 밖의 종류, 없는 객체, 암호화된 객체, 정의를 읽는 조회가 실패한 객체(권한·잠금 시간 초과 등), 이미 있는 파일(`--force`로 덮어씀)은 그 객체만 실패로 알리고 나머지는 계속 받으며, 하나라도 실패하면 종료 코드 4입니다.
* 파일은 작업 공간의 `db/pulled/<스키마>.<이름>.sql`(`sql.pullDirectory`·`--out`으로 변경, 작업 공간 밖이면 종료 코드 4)에 UTF-8(BOM)로 씁니다. 첫 줄 주석(받은 기록)에 출처·서버·DB·객체·정의 해시·받은 시각(UTC)이 남습니다. 고친 파일을 운영에 반영한 뒤 같은 파일을 `pelly sql`로 다시 고치면 pelly가 이 줄을 운영 정의로 갱신합니다.
* 운영에서 받을 때 개발 연결이 설정되어 있으면 개발 정의와 비교해, 다르거나 개발 DB에 없으면 경고를 보이고(파일은 씁니다) 다를 때는 둘째 줄에 `-- pelly pull: dev differs (sha256=...)`를 남깁니다.
* 받은 파일은 커밋하지 않습니다. pelly는 `.gitignore`를 고치지 않으므로 저장소 `.gitignore`에 직접 한 줄을 넣으십시오.
  ```
  db/pulled/
  ```
  폴더가 무시되지 않으면(`git check-ignore`) 알림을 보입니다. git이 없으면 이 확인은 건너뜁니다.

### `pelly sql check` — SSMS에서 운영에 실행하기 직전 확인
```bash
pelly sql check <파일.sql> [--object <이름>] [--config <경로>] [--pause-on-exit]
```
* 모델을 부르지 않고 파일을 바꾸지 않습니다. 운영(`sql.production`)에는 읽기 조회만 합니다. 대상은 파일에 객체가 하나면 자동, 여럿이면 `--object`로 지정합니다.
* 종료 코드: 운영과 같음·받은 뒤 운영이 바뀌지 않음·운영에 없음(새 객체) → 0, 받은 뒤 운영이 바뀜(반영하면 그 변경을 덮어씀) → 1, 대상이 여럿인데 `--object`가 없음 → 3, 운영 연결 없음·접속 실패·받은 기록 없이 파일이 운영과 다름·파일 오류 → 4.
* 받은 기록은 `pelly sql pull`이 파일 첫 줄에 남긴 주석입니다. 운영과 같다는 판정이 받은 기록보다 먼저입니다. 운영과 같은데 받은 기록이 운영보다 오래되었으면 `받은 기록이 운영보다 오래되었습니다 — 다음 pelly sql 실행 때 갱신합니다`를 함께 보입니다(종료 코드 0). `check`는 파일을 바꾸지 않습니다. 정의를 읽는 조회가 실패하면(권한 등) 접속 실패처럼 종료 코드 4입니다.
* **SSMS 흐름:** `pelly sql pull dbo.usp_X` → 받은 파일을 `pelly sql`로 수정 → 운영에 반영하기 전에 `pelly sql check <파일>` → SSMS에서 운영에 실행. pelly는 운영에 쓰지 않으므로 마지막 실행은 사람이 합니다.
* **한계:** 동적 SQL(`EXEC(@sql)`, `sp_executesql`)로 부르는 곳, 다른 DB·연결된 서버를 거는 참조, 시노님(synonym)을 거친 참조는 영향 탐색에서 찾지 못합니다. 이름을 상수 클래스에 두고 쓰는 C# 코드는 상수 정의 위치만 찾습니다. 영향 탐색은 서버에 있는 **고치기 전** 정의를 기준으로 합니다.

### `pelly spec`·`pelly apply` — 작업 명세 파일과 순차 실행 (V2-1a)
```bash
pelly spec new <id> [--pause-on-exit]
pelly spec check <명세 파일> [--impact] [--config <경로>] [--pause-on-exit]
pelly apply <명세 파일> [--continue] [--resume] [--review-at-end] [--yes] [--dry-run] \
            [--max-retries N] [--verbose] [--config <경로>] [--pause-on-exit]
```
여러 파일에 걸친 변경을 `changes/<id>.md` 명세 파일에 작업 줄로 적고, 위에서부터 하나씩 `pelly cs`·`pelly sql`과 같은 검증 절차로 적용합니다.

```markdown
---
id: add-refund
status: draft
---
# 환불 추가

## 수용 기준
- AC1: 배송 완료 주문은 취소할 수 없다
  test: OrderServiceTests.Cancel_Delivered_Throws
  breaks: OrderServiceTests.Cancel_Delivered_CurrentlyCancels

## 작업
- [ ] T1 cs  src/Orders/OrderService.cs#OrderService.Cancel  배송 완료면 예외 던지기 (AC1)
- [ ] T2 sql db/sp/usp_CancelOrder.sql#dbo.usp_CancelOrder   배송 완료 제외 조건 추가 [G1]
- [ ] T3 asp admin/order/cancel.asp#CancelOrder              usp_CancelOrder 호출에 맞춰 고치기 [G1]
```

* **작업 줄:** `- [ ] <ID> <cs|sql|asp|js|css> <파일>[#심볼] <요청> [(AC 참조)]`. 필드 사이는 공백 하나 이상입니다. 작업은 최대 20개입니다. 작업 줄은 제목이 정확히 `## 작업`인 절 아래에만 둡니다 — 다른 절(예: `## 작업 (2차)`)에 있는 작업 줄은 `InvalidTaskLine` 오류입니다.
* **`cs` 작업은 `#멤버`가 필수입니다.** 클래스가 둘 이상이거나 멤버가 겹치면 `#클래스.멤버`로 씁니다. `sql` 작업의 `#객체`는 선택입니다.
* **`asp` 작업 (A2):** `.asp`·`.inc` 파일(설정 `asp.extensions`) 하나의 대상 하나를 `pelly asp`와 같은 검증 절차로 고칩니다. 대상은 **필수**이고 두 가지 꼴입니다: `#Proc`(`Function`·`Sub`, 클래스 안이면 `#Class.Proc`) 또는 줄 범위 `#L<시작>-<끝>`(`L`은 대문자, 1부터, 시작 ≤ 끝, 예: `admin/order/list.asp#L24-37`). 줄 범위는 `asp`·`js`·`css` 작업에서만 쓸 수 있습니다. 클라이언트 `<script>`·`<style>` 블록은 `asp` 작업으로 줄 범위를 잡아 고칩니다(`pelly asp --lines`와 같음). 같은 파일을 먼저 고치는 작업(종류 상관없음 — W3) 뒤에 `#L` 줄 범위를 쓰면 줄이 어긋날 수 있어 `spec check`가 오류로 거부하므로, 그런 파일은 `#Proc`으로 쓰거나 `#L` 작업을 그 파일의 첫 작업으로 두거나 작업을 하나로 합치십시오. `asp` 작업은 `cs`·`sql` 작업이 하는 요청문 이름 검사(`UnknownIdentifier` 경고)는 `asp` 작업에 하지 않습니다.
* **`js`·`css` 작업 (W3):** `.js`·`.css` 파일, 또는 페이지(`asp.extensions`·`.htm`·`.html`)의 클라이언트 `<script>`·`<style>` 블록 하나를 `pelly js`·`pelly css`와 같은 검증 절차로 고칩니다. 대상은 필수입니다: `js`는 `#함수`(`#fnSearch`, `#Order.save`) 또는 `#L시작-끝`, `css`는 공백 없는 `#선택자`(`#.search-box`, `#td.num`, id 선택자는 `##main`) 또는 `#L시작-끝`입니다(공백이 있는 선택자는 `#L`로 씁니다). 페이지의 `#L` 범위는 블록 하나 안에 있어야 합니다.
  ```markdown
  - [ ] T1 js  js/order/history.js#fnHistory      매개변수 page를 끝에 더한다 [G1]
  - [ ] T2 asp admin/order/history.asp#L12-12     onclick의 fnHistory 호출에 1을 넘긴다 [G1]
  - [ ] T3 js  admin/order/history.asp#goHistory  fnHistory 호출에 1을 넘긴다 [G1]
  - [ ] T4 css css/admin.css#.search-box          너비를 넓힌다
  ```
* **명세의 `js` 작업은 함수 이름·매개변수를 바꿀 수 있습니다 (W3):** 단독 `pelly js`는 대상 함수의 이름·매개변수가 바뀌면 거절하지만(`--signature`를 주면 허용), 명세의 `js` 작업은 허용합니다. 함수 밖 새 최상위 선언은 여전히 거절합니다. 부르는 곳은 **pelly가 자동으로 고치지 않습니다** — 부르는 곳마다 작업 줄을 쓰고(같은 그룹으로 묶으면 함께 되돌림), pelly는 빠진 곳을 아래처럼 알려 줍니다. 바뀐 서명은 같은 apply의 뒤 `js`·`asp` 작업의 모델 입력에 「T1이 바꾼 함수: fnHistory(no, page) (원래 fnHistory(no))」로 보이고(대상이 그 이름을 쓸 때), 뒤 작업이 같은 파일의 옛 이름(`#fnHistory`)을 가리키면 새 이름으로 따라갑니다(「T5의 대상 fnHistory는 T1이 바꾼 fnLoad로 찾음」, `apply.json` 작업 항목의 `note`).
* **JS 영향 탐색 (W3):** `spec check --impact`는 미완료 `js` 작업의 `#함수`를 사이트 전체(`.js`, 페이지 `<script>`, `onclick=` 같은 이벤트 속성, `href="javascript:…"`, VBScript 문자열 안 HTML)에서 부르거나 넘기는(`setTimeout(fnX, 10)`) 곳을 찾아, 명세 작업 대상 안에 없으면 경고 `JsImpactNotInSpec`을 남깁니다: 「작업 T1의 영향 대상이 명세에 없습니다: admin/order/history.asp:12 (fnHistory 호출, 인수 1개, onclick)」. 이벤트 속성은 `asp` 작업만 덮습니다. 그 파일(페이지면 include까지)이 같은 이름의 함수를 직접 선언하면 다른 함수로 보고 빼고, 씨앗 `.js`를 `<script src>`로 부르지 않는 페이지에는 「(이 페이지는 js/order/history.js를 부르지 않음)」을 붙입니다. 함수 하나당 10줄, 나머지는 「외 N곳」이고 전체 목록은 `.pelly/impact/<명세 id>-js.json`입니다. `#L` 대상 작업은 찾지 않고 `JsImpactLinesTarget`으로 알립니다. **SQL·ASP 영향과 달리 `pelly apply`는 JS 영향으로 확인을 묻지 않습니다** — 함수 본문만 고치는 작업이 더 흔하기 때문이고, apply에서는 실제로 서명이 바뀐 경우만 아래 적용 뒤 확인이 경고합니다. 이름 기준이라 이름이 같은 다른 함수도 찾힙니다. `asp.siteRoot`·`asp.encoding`·`web.vendorPaths`가 쓰입니다.
* **서명이 바뀐 뒤 고치지 않은 호출 (W3):** apply가 끝나면 이번에 적용한 `js`·`asp` 작업이 바꾼 JS 함수 서명을 원본과 비교해 남은 호출을 경고합니다(종료 코드는 바뀌지 않음). 이름이 바뀌었으면 옛 이름의 호출·참조, 매개변수 개수가 바뀌었으면 인수 개수가 새 매개변수 수와 다른 호출(명세가 덮었어도 고쳐지지 않았으면 경고), 개수는 같고 이름·순서만 바뀌었으면 확인할 호출 목록입니다. 예: 「fnHistory의 서명이 바뀌었습니다: fnHistory(no) → fnHistory(no, page). 고치지 않은 호출: admin/order/history.asp:12 — 기존 호출은 새 인수가 undefined로 동작함」. `apply.json`의 `jsCallersNotUpdated`, `pelly report`의 「JS 서명 변경 뒤 고치지 않은 호출 N곳」에 남습니다. 서명을 바꾼 그룹 밖 `js`·`asp` 작업 뒤에 다른 작업이 실패하면 「T1(js)은 적용됐고 T2는 실패했습니다. 함수와 부르는 곳이 어긋나 있을 수 있습니다. 함께 되돌리려면 [G1]로 묶으십시오」(`JsCallerMismatch`, 서명을 바꾼 작업이 `asp`면 「T1(asp)…」)를 경고합니다.
* **폼 필드 어긋남 (W3b):** apply가 끝나면 이번에 적용한 `asp`·`js` 작업의 파일 전체를 원본(첫 실행 전)과 비교해 폼 필드 짝을 다시 봅니다. 앞 작업이 저장 페이지의 키를 바꾸고 뒤 작업이 폼을 고쳤다면 작업별 검토 줄의 「폼 필드」 경고는 기록으로 남고, 끝에는 남은 어긋남만 경고합니다. 「작업 중 폼 필드 경고 N건 중 M건은 뒤 작업이 해결함」을 한 줄 보입니다. 남은 것은 `apply.json`의 `formFieldMismatches`(`kind`: `notSent`·`nobodySends`, `target`, `key`, `sender`, `message`)와 `pelly report`의 「폼 필드 어긋남 N건」에 남습니다(종료 코드는 바뀌지 않음).
* **`pelly spec check`:** 문법, 대상 파일·심볼 존재, AC 참조, 작업 수 상한을 검사합니다. 오류가 있으면 종료 코드 4입니다. `asp` 작업의 오류 코드: `AspTargetRequired`(`#대상` 없음), `LinesOutOfFile`(줄 범위가 파일 길이 밖), `LinesNotSupported`(`cs`·`sql` 작업에 `#L…`), `AspLinesAfterEdit`(같은 파일을 먼저 고치는 작업 뒤의 줄 범위, 「프로시저 대상(#이름)으로 쓰거나 작업을 하나로 합치십시오」), `KindMismatch`(파일 확장자가 `asp.extensions`에 없음), `SymbolNotFound`·`SymbolAmbiguous`(프로시저를 못 찾거나 여럿), `AspEncoding`(파일을 읽을 수 없는 인코딩)이며 모두 오류입니다. `js`·`css` 작업(W3)의 오류 코드는 `WebTargetRequired`(대상 없음), `LinesOutsideBlock`(페이지의 `#L`이 `<script>`·`<style>` 블록 하나 안이 아님, 「asp 작업으로 쓰십시오」), `WebVendorFile`(압축·라이브러리 파일), `WebEncoding`, `LinesAfterEdit`(위 `AspLinesAfterEdit`와 같은 규칙), 그리고 `KindMismatch`·`SymbolNotFound`·`SymbolAmbiguous`·`LinesOutOfFile`입니다. 작업 그룹 `[G1]`은 아래 `pelly apply`의 그룹 항목을 보십시오.
* **수용 기준의 `test:`·`breaks:` (V2-2b):** 수용 기준 줄 바로 아래에 들여쓴 `test: <이름>`(수용 기준당 하나)과 `breaks: <이름>`(여러 줄 가능)을 적을 수 있고, 순서는 상관없으며 다른 줄이 나오면 거기서 끝납니다. 이름 형식과 찾기 규칙은 `--verify-test`와 같습니다(`[네임스페이스.]클래스.메서드` 또는 `메서드`, 테스트 특성이 붙은 메서드만 찾음). `breaks:`는 이 변경으로 실패해도 되는 기존 테스트로, `--expect-break`와 같이 회귀 판정에서만 빠집니다. `test:`가 없는 수용 기준과 연결 작업이 없는 명세의 동작은 그대로입니다.
* **연결 작업 규칙:** 수용 기준의 연결 작업은 `(AC…)`로 그 수용 기준을 참조하는 `cs` 작업입니다(명세 순서, 완료 작업 포함). `sql` 작업은 연결 작업이 아니며 테스트로 판정하지 않습니다. **첫 연결 작업의 기준 실행에서 RED**(지정 테스트가 실패하는지)를 확인하고, **마지막 연결 작업에서 GREEN**을 확인합니다. 연결 작업이 하나면 `pelly cs --verify-test`와 같습니다. 연결 작업은 모두 테스트를 돌려 회귀를 보며, 그 수용 기준의 `breaks:`는 회귀에서 뺍니다. 그 사이 작업에서 지정 테스트는 아직 실패하는 것이 정상이라 회귀로 세지 않습니다.
* **`spec check`의 테스트 검사:** 수용 기준에 미완료 연결 작업이 있으면 `test:`·`breaks:` 이름을 못 찾을 때 `TestNotFound`, 여러 테스트와 맞을 때 `TestAmbiguous`가 **오류**입니다(apply가 어차피 종료 4로 멈추므로 미리 알립니다). 연결 작업이 모두 끝났거나 없는 수용 기준이면 같은 코드가 경고입니다. 그 밖에 오류 `TestIsTarget`(미완료 `cs` 작업이 — 연결 여부와 상관없이 — 미완료 연결 작업이 있는 어느 수용 기준의 테스트 메서드를 고침 — 테스트는 사람이 씁니다), `DuplicateTest`(수용 기준에 `test:`가 둘 이상), `InvalidTestName`(이름 형식 오류), `BreaksSameAsTest`(같은 테스트를 `test:`와 `breaks:`에 함께 둠)가 있고, 경고 `TestUnverified`는 `test:`가 있는데 연결 `cs` 작업이 없는 수용 기준(`테스트로 검증되지 않습니다`)입니다.
* **`pelly apply`의 테스트 우선 동작:** 연결 작업은 `pelly cs --verify-test`와 같은 RED→패치→GREEN·회귀 절차로 돌고, 결과는 `apply.json`의 `criteria`(`acId`, `red`, `outcome`: `Met`·`NotMet`·`NotRun`·`Unverified`, `greenRunId`, `breaksExcluded`)에 남습니다. RED에서 이미 통과하면 경고를 보이고, 대화형이면 계속할지 묻습니다(거부하면 종료 2, `TestRedDeclined`). `--yes`이거나 비대화형이면 묻지 않고 기록합니다. `--review-at-end`에서는 작업 중 묻지 않고, 끝의 전체 승인 직전에 「이미 통과한 테스트로 판정된 수용 기준」 목록을 보여 줍니다. `--resume`에서 첫 연결 작업이 이미 `[x]`면 그 수용 기준의 RED는 다시 확인하지 않고(`Skipped`), 마지막 연결 작업이 이전 실행에서 끝났으면 이번에 돌지 않았으므로 `NotRun`입니다. `--continue`에서 GREEN에 실패한 작업은 되돌려져 `[ ]`로 남고 그 수용 기준은 `NotMet`입니다. `--dry-run`은 테스트를 돌리지 않습니다.
* **그룹과 테스트:** 그룹 작업이 연결된 수용 기준이 있으면, 그룹 기준 빌드 뒤에 영향 범위 안 테스트 프로젝트의 기준 테스트를 돌려 그룹 안에 첫 연결 작업이 있는 수용 기준의 RED를 확인합니다. 새 시그니처로 쓴 테스트 프로젝트는 원본에서 빌드되지 않는 **컴파일 RED**로 받습니다. 그룹 빌드가 통과하면 같은 프로젝트를 다시 돌려 마지막 연결 작업이 그룹 안에 있는 수용 기준의 GREEN과 **엄격 회귀**를 봅니다. 새로 실패한 테스트는 어느 작업의 탓인지 알 수 없으므로, GREEN 테스트가 모두 통과하고 테스트 프로젝트에 빌드 오류도 없을 때는 재실행 없이 그룹 전체를 되돌리고 종료 1입니다(`GroupTestRegression`). GREEN도 함께 실패하면 회귀 목록이 GREEN 실패 재실행의 피드백에 담깁니다. GREEN 실패는 그 수용 기준의 그룹 안 마지막 연결 작업만 테스트 피드백과 함께 다시 실행하고(테스트 프로젝트가 아직 빌드되지 않는 수용 기준의 마지막 연결 작업도 빌드 오류 피드백으로 다시 실행합니다), 실패한 테스트를 맡은 그룹 작업이 하나도 없으면 재시도를 쓰지 않고 곧바로 그룹 전체를 되돌리고 종료 1(`GroupTestGreenFailed`)입니다. 재실행은 빌드 오류 재실행과 같은 재시도 예산(`retry.max` 또는 `--max-retries`) 하나를 함께 씁니다. 다 쓰고도 통과하지 않으면 그룹 전체를 되돌리고 종료 1(`GroupTestGreenFailed`)입니다. 그룹의 테스트를 돌릴 수 없으면(편집 전이면 그대로, 편집 뒤면 그룹을 되돌린 뒤) 종료 4(`GroupTestUnverifiable`)입니다. 원본에서도 빌드되지 않던(컴파일 RED) 테스트 프로젝트가 그룹 뒤에도 빌드되지 않는데 그 수용 기준의 GREEN 테스트가 이 그룹에 없으면 `그룹 … 테스트 프로젝트가 그룹 뒤에도 빌드되지 않습니다 — 수용 기준의 마지막 연결 작업까지 같은 그룹에 넣으세요`라고 알리고 같은 종료 4로 멈춥니다. 원본에서는 빌드되던 테스트 프로젝트가 그룹의 편집으로 빌드되지 않으면 GREEN 실패처럼 빌드 오류 피드백으로 다시 실행합니다. 그룹 밖 연결 작업의 실패 유형은 `pelly cs --verify-test`와 같습니다.
* **테스트 변경 보호(V2-2c):** `pelly apply`는 수용 기준의 첫 연결 작업(또는 그 작업이 든 그룹)이 돌기 직전에 지정 테스트의 **메서드 선언**을 해시해 두고, 마지막 연결 작업이 그 값과 비교합니다: 그 사이 다른 작업이 테스트를 바꿨으면 마지막 연결 작업이 되돌려지고 종료 4(`TestFileChanged`, `지정한 테스트 파일이 실행 중에 바뀌었습니다: …`)입니다. 그룹은 기준 테스트 때(그룹 밖에서 시작한 수용 기준은 그 첫 연결 작업 직전에) 잰 값과 GREEN·회귀를 통과한 뒤의 값을 비교해, 다르면 그룹 전체를 되돌리고 `그룹 G1: 지정한 테스트가 실행 중에 바뀌었습니다: <파일>`을 알린 뒤 종료 4(`GroupTestFileChanged`)입니다.
* **알려진 한계(수용 기준 테스트):** 테스트 변경 보호는 지정 테스트 메서드의 선언만 봅니다: 같은 파일의 다른 멤버(도우미·필드·설정 메서드)가 바뀐 것은 보지 않습니다. `--resume`에서 첫 연결 작업이 이미 `[x]`면 작업을 건너는 비교 값이 없어, 작업 하나의 실행 안에서만 봅니다. 대신 `spec check`가 수용 기준의 테스트 메서드를 대상으로 하는 미완료 `cs` 작업을 연결 여부와 상관없이 `TestIsTarget` 오류로 거부합니다.
* **`pelly report`:** 명세 표에 `테스트로 검증되지 않은 수용 기준`(`TestUnverified`에 해당하는 수용 기준 수)과 `breaks로 뺀 테스트` 줄이 더해지고, 수용 기준 충족률은 apply의 `criteria`도 셉니다. 비식별 보고서에는 개수만 나가며 테스트 이름·수용 기준 ID는 나가지 않습니다. 수용 기준 지표는 apply 기록마다 따로 판정합니다: 한 apply에서 RED를 확인했지만 GREEN은 `--resume` 뒤에야 충족한 수용 기준은 처음 기록에서 `NotMet`(마지막 연결 작업이 실패함, 돌지 않았으면 `NotRun`)으로 남고, 이어 한 기록에서는 RED가 `Skipped`라 세지 않습니다. `breaks로 뺀 테스트`는 수용 기준마다 자기 `breaks:`에 맞는 테스트만 셉니다. 편집 뒤 테스트를 판정할 수 없어 되돌린 그룹(`GroupTestUnverifiable`)과 지정한 테스트가 실행 중에 바뀌어 되돌린 그룹(`GroupTestFileChanged`)도 그룹 빌드는 통과했으므로 그룹 빌드 통과율에서는 통과로 셉니다.
* **영향 탐색:** `spec check`는 명세에 작업 그룹이 있으면 미완료 그룹 `cs` 작업의 대상 멤버에서, `--impact`이면 미완료 `cs` 작업 전부에서 시작해 그 멤버(같은 이름의 오버로드 전부)를 구현·재정의하거나 부르거나 참조하는 곳, 그리고 그 멤버가 구현·재정의하는 기반 멤버와 그 멤버의 다른 구현·호출부를 찾습니다. 찾은 곳 `(파일, 멤버)`와 파일·멤버가 같은 작업(완료 작업 포함)이 명세에 없으면 찾은 곳마다 경고 `ImpactMissing` 하나(`작업 T1의 영향 대상이 명세에 없습니다: <파일>#<멤버> (<이유>)`, 이유는 `구현`·`재정의`·`기반 멤버`·`호출`·`멤버 밖`)를 남깁니다. 여러 작업에서 닿은 곳은 첫 작업의 줄에 한 번만, 작업 ID를 모두 적어 남깁니다(`작업 T1, T2의 영향 대상이 …`). 형식 밖(최상위 문 `Program.cs` 등)에서 부르는 곳은 멤버 `<top-level>`, 이유 `멤버 밖`으로 남기며, 어떤 작업도 이 이름을 쓸 수 없으므로 늘 경고로 남습니다. `pelly apply`도 시작할 때 그룹 `cs` 작업에 대해 같은 탐색을 하고, 다른 경고처럼 알린 뒤 계속합니다(대화형이면 계속할지 묻고, `--yes`이면 묻지 않습니다). 의미 분석(프로젝트를 불러 심볼로 찾기)을 쓸 수 없으면 이름 기준으로 대신하고 반드시 `ImpactNameBased`(`영향 탐색: 이름 기준(부정확): 의미 분석 실패 — <유형>`, 예: `복원 안 됨 (dotnet restore 필요)`, `해석률 97.2%`)를 남깁니다. 보조 프로세스 폴더(`BuildHost-*`)가 실행 파일 옆에 없으면 유형은 `빌드 도구 없음`입니다. 일부 폴더·파일을 읽지 못하면 `ImpactIncomplete`, 메서드·속성이 아닌 대상은 `ImpactUnsupported`, 대상 파일이나 멤버를 찾지 못하면 `ImpactSeedNotFound`(`작업 T1: 영향 탐색 시작점을 찾지 못했습니다: <파일>#<멤버>`), 탐색 자체가 실패하면 `ImpactFailed`입니다. `sql` 작업의 탐색은 아래 SQL 영향 탐색을 보십시오. 영향 탐색은 경고만 만들며 종료 코드를 바꾸지 않습니다. `apply.json`에는 `impact`(`semantic`·`name`, 탐색하지 않았으면 `null`)와 `impactFallback`(이름 기준으로 대신한 이유)을 적습니다. 복원 여부는 프로젝트 폴더의 `obj/project.assets.json`으로 보며, 프로젝트나 `Directory.Build.props`의 `BaseIntermediateOutputPath`·`UseArtifactsOutput`(`artifacts/obj/<프로젝트>/`)도 읽어 그 위치를 봅니다(조건부 설정·읽을 수 없는 값은 기본 `obj`만). 대화형 `apply`가 영향 경고를 보이고 계속할지 묻는 동안에는 작업 공간 잠금을 잡고 있으므로, 그동안 같은 작업 공간의 다른 pelly 실행은 거절됩니다.
* **SQL 영향 탐색 (V2-1d):** 미완료 `sql` 작업의 대상 객체(`#객체`, 없으면 파일의 유일한 정의)에서 시작해 ① 그 객체를 참조하는 같은 DB의 모듈(이유 `EXEC`·`참조`), ② 그 이름을 문자열로 쓰는 C# 코드(이유 `이름 호출(문자열)`, 예: `"dbo.usp_X"`, `"EXEC dbo.usp_X @Id"`), ③ 그 객체가 쓰는 컬럼을 읽는 모듈(이유 `같은 컬럼 읽음`, 테이블마다 최대 10개 — 명세의 `sql` 작업이 이미 대상으로 삼은 모듈과 ①로 이미 경고한 모듈은 이 10개에 넣지 않고 요약의 경고 수에도 세지 않습니다)을 찾습니다. `spec check`는 `--impact`일 때만 돌고(운영 연결 `sql.production`이 있으면 운영 DB 기준, 없으면 개발 DB 기준과 경고 `ImpactSqlDevBased`), `apply`는 운영 연결이 설정되어 있을 때만 시작 검사로 돕니다 — 운영 연결이 없으면 `apply`의 출력·질문·DB 조회는 그대로입니다. 운영 연결이 설정되어 있는데 접속할 수 없으면 개발 DB로 대신하지 않고 `ImpactSqlUnavailable`을 남긴 뒤 ②만 합니다. 명세에 없는 SP 대상은 `ImpactMissing`(`… dbo.usp_A (EXEC) — pelly sql pull dbo.usp_A 로 받아 작업 줄에 추가할 수 있습니다 (<sql.pullDirectory>/dbo.usp_A.sql)`; 이름에 공백·`.`·`]` 같은 문자가 있으면 `"[dbo].[My Proc]"`처럼 대괄호와 따옴표로 적고, `pelly sql pull`이 받지 못하는 종류(트리거 등)는 이 안내 없이 대상만 알립니다), 쓰는 테이블마다 요약 `ImpactTableShared` 한 줄을 남기고, 후보 모듈 전체 목록은 `spec check`면 `.pelly/impact/<명세 id>-sql.json`(실행마다 덮어씀), `apply`면 `.pelly/runs/<apply-id>/impact-sql.json`에 둡니다(`--dry-run`은 apply 폴더를 만들지 않고 `spec check`와 같은 `.pelly/impact/<명세 id>-sql.json`에 둡니다)(`apply.json`의 `sqlImpact`에 기준 DB·시작점 수·경고 수·목록 경로). 서버에 아직 없는 새 객체는 `ImpactSeedNotFound`로 알리고 ②만 합니다. 시작점이 암호화되었거나 정의를 읽을 수 없으면 ③을 건너뛰고 `ImpactSqlEncrypted`(`작업 T1: dbo.usp_X는 암호화되었거나 정의를 읽을 수 없어 컬럼 영향을 찾지 못했습니다`)로 알립니다(①·②는 합니다). 운영에는 카탈로그 읽기 조회만 보냅니다. 한계: 동적 SQL(`EXEC(@sql)`, `sp_executesql`)로 부르는 곳과 다른 DB에서 참조하는 곳은 찾지 못합니다. 이름을 상수 클래스에 두고 쓰는 C# 코드는 상수 정의 위치만 찾고 그 상수를 쓰는 곳은 찾지 않습니다. ③은 서버에 있는 **고치기 전** 정의를 기준으로 하므로 이번 수정이 새로 쓰기 시작할 컬럼은 반영되지 않습니다.
* **ASP 영향 탐색 (A2):** 미완료 `sql` 작업의 대상 SP를 부르는 ASP 호출을 찾아 명세에 없으면 경고 `ImpactMissing`을 남깁니다: 「작업 T2의 영향 대상이 명세에 없습니다: admin/order/cancel.asp:57 (usp_CancelOrder 호출, 인수 3개)」(인수를 모르면 「인수 모름」, 여러 작업에서 닿으면 작업 ID를 모두 적음). 사이트 루트(`asp.siteRoot`)의 `asp.extensions` 파일에서 문자열로 쓴 SP 호출(`EXEC x`, `{call x}`, `ADODB.Command`의 저장 프로시저 이름)과 `asp.spCallFunctions`에 적은 공용 래퍼 호출(SP 이름 인수 위치)을 색인해 찾습니다. 명세의 `asp` 작업(완료 작업 포함)이 그 호출이 있는 프로시저나 줄 범위를 고치면 영향 대상으로 세지 않습니다. SP 하나당 경고는 최대 10개이고 넘으면 「외 n곳」 한 줄입니다. 호출이 `.inc`(include) 안이면 줄 끝에 「(include: 그 파일을 쓰는 페이지 k곳)」을 붙입니다. `spec check`는 `--impact`일 때만 돌고, `apply`는 명세에 미완료 `sql` 작업이 있으면 **`sql.production` 설정과 상관없이** 시작 검사로 돌며 경고가 있으면 다른 영향 경고처럼 「계속할까요?」 확인에 합쳐집니다(`--yes`이면 묻지 않음). `spec draft`는 이 경고를 보여 주되 `asp` 작업 줄은 제안하지 않습니다. 후보 전체 목록은 `.pelly/impact/<명세 id>-sql.json`(apply는 `.pelly/runs/<apply-id>/impact-sql.json`)의 `asp` 배열에 둡니다. 한계: 동적으로 만든 SP 이름은 찾지 못하고, `rs.Open "EXEC x @a=1"`처럼 이름 붙은 인수를 문자열에 넣은 호출은 찾지 못하며, SP의 결과 열 변경이 어느 페이지에 닿는지는 추적하지 않습니다.
* **바뀐 SP의 서명을 `asp` 작업에 알림 (A2):** 이번 apply에서 적용했거나 이미 완료한 `sql` 작업의 SP는 **파일**(`.sql`)에서 읽은 매개변수 목록을 `asp` 작업의 모델 입력(바뀐 서명 메모)과 SP 존재·인수 검사에 씁니다. 이 SP는 개발 DB 연결이 없어도 검사됩니다. 파일을 해석하지 못하면 「T2의 SP 파일을 해석하지 못해 개발 DB의 매개변수를 씁니다」 경고를 내고 개발 DB 값으로 대신합니다.
* **매개변수가 바뀐 뒤 고치지 않은 호출 (A2):** 적용한 `sql` 작업이 SP 매개변수를 바꿨는데 그 SP를 부르는 ASP가 이번 apply의 `asp` 작업으로 고쳐지지 않았으면 경고하고 apply는 계속합니다(종료 코드는 바뀌지 않음): 「usp_CancelOrder의 매개변수가 바뀌었습니다(@Reason 추가). 고치지 않은 호출: admin/order/cancel.asp:57 외 2곳」. 기본값이 있는 매개변수만 끝에 더해졌으면 「 — 기존 호출은 그대로 동작할 수 있음」이 붙습니다. `apply.json`의 `aspCallersNotUpdated`에 기록하고 `pelly report`의 명세 표에 「SP 매개변수 변경 뒤 고치지 않은 ASP 호출 N곳」이 나옵니다.
* **그룹 없는 `sql`+`asp`:** `sql` 작업이 적용된 뒤 그룹에 묶이지 않은 `asp` 작업이 실패하면 `sql` 수정은 남기고 경고합니다: 「T2(sql)은 적용됐고 T3(asp)는 실패했습니다. SP와 페이지가 어긋나 있을 수 있습니다. 함께 되돌리려면 [G1]로 묶으십시오」(`[G1]`은 다음 빈 그룹 번호). 함께 되돌리고 싶으면 두 작업을 같은 그룹으로 묶으십시오.
* **`pelly apply`:** 작업마다 성공하면 줄을 `- [x] … <!-- run: <run-id> -->`로, 실패하면 `<!-- failed: <run-id> <종류> -->`로 표시하고 멈춥니다. `--continue`는 실패한 작업을 건너뛰고 나머지를 이어 가며(종료 코드는 1 — 작업별 검토에서 거부한 작업도 실패로 셉니다), `--resume`은 완료 표시가 된 작업을 건너뛰고 첫 미완료 작업부터 이어 갑니다. `[x]`가 있는 명세를 `--resume` 없이 실행하면 종료 코드 4입니다.
* **검토:** 기본은 작업마다 diff 승인을 묻습니다. `--review-at-end`는 전부 끝난 뒤 한 번만 묻고, 거부하면 이번 apply가 바꾼 파일을 모두 되돌리고 이번에 남긴 체크 표시와 상태도 되돌립니다(종료 코드 2). 실행 중 Ctrl+C로 취소해도 같은 방식으로 되돌립니다. 단, apply 도중 pelly 밖에서 바뀐 파일은 덮어쓰지 않고 그대로 두며, 그 파일을 알리고 종료 코드 4로 끝납니다. 그 파일의 작업은 `[x]`를 유지합니다. `--review-at-end`는 대화형 실행에서만 쓸 수 있습니다(비대화형이면 `--yes`가 있어도 시작 전에 종료 코드 4). 명세 파일을 쓸 수 없어 중간에 멈추면 파일을 되돌릴 수 없으므로, 검토하지 않은 변경이 남은 파일을 경고로 알려 줍니다.
* **작업 그룹(`[G1]`)** 은 함께 바뀌어야 하는 작업을 묶습니다. 그룹에는 `cs`·`sql`·`asp`·`js`·`css` 작업을 섞을 수 있고(A2에서 바뀜 — 전에는 `cs`만 넣을 수 있었고 `sql`은 `SqlInGroup` 오류였습니다), 작업 하나라도 실패하면 **그룹 전체 파일을 되돌립니다.** C# 빌드·테스트 검증은 그룹의 **`cs` 작업에만** 하며, `cs` 작업이 없는 그룹(예: `sql`+`asp`)은 빌드 검증을 하지 않습니다. `sql`·`asp` 작업은 자기 검증 절차 안에서 재시도합니다. 아래의 빌드 검증 설명은 그룹의 `cs` 작업에 관한 것입니다. 같은 그룹의 작업은 명세에서 연속해야 하고(`GroupNotContiguous`), `cs` 작업의 시그니처·이름 변경을 마지막 작업 뒤에 한 번만 빌드 검증합니다. 그룹 작업에서는 대상 멤버의 반환 형식·매개변수·한정자·특성·이름을 바꿀 수 있지만, 다른 멤버를 바꾸거나 지울 수 없고 using을 지울 수 없으며 public 멤버를 추가할 수 없습니다(private 도우미는 가능). 이름이 바뀐 대상은 같은 형식에 새로 생긴 같은 종류·같은 접근 수준의 멤버 중 같은 이름, 없으면 같은 자리(앞뒤에 그대로 있는 멤버로 셈)의 멤버로 짝짓습니다. private 대상 바로 옆(위나 아래)에 private 도우미도 새로 생겨 자리로 가를 수 없으면, 짝을 잘못 짓지 않도록 수정을 거절하고 도우미를 대상에서 떨어진 자리에 두라고 피드백합니다(시그니처가 그대로인 쪽을 고르지 않습니다 — 이름과 시그니처를 함께 바꾼 대상 옆에 옛 시그니처의 도우미가 생긴 경우와 구별되지 않기 때문입니다). 그룹의 첫 작업 전에 영향 범위(그룹 파일의 프로젝트와, 그 프로젝트를 직접·간접으로 `ProjectReference`하는 프로젝트 — 후보는 작업 공간 폴더에서 찾은 `.csproj`와 작업 공간의 `.sln`·`.slnx`에 적힌 프로젝트이며, `packages` 폴더는 모든 하위 폴더에 `.nupkg`가 있는 packages.config 복원 폴더일 때만 건너뜁니다)를 기준 빌드하고, 마지막 작업 뒤에 다시 빌드해 새로 생긴 오류만 셉니다(그룹 빌드는 `GenerateFullPaths=true`로 오류 경로를 전체 경로로 받고, 상대 경로로 나온 오류는 그 프로젝트 폴더 기준으로 풉니다). 새 오류가 그룹 작업의 대상 멤버 안이거나, 그 작업이 더한 private 도우미 안이거나, 그 작업 대상이 속한 형식의 선언 줄·기본 형식 목록(예: 인터페이스를 더 이상 구현하지 않는 `CS0535`·`CS0738`)이면 그 작업이 빌드 피드백을 받아 다시 실행됩니다(그룹 전체 `retry.max` 또는 `--max-retries`; 재실행에서는 그 작업이 더한 도우미도 고칠 수 있습니다). 재실행을 다 써도 새 오류가 남으면 그룹 전체를 되돌리고 종료 코드 1(`GroupBuildFailed`)입니다. 그 밖의 오류(다른 멤버, 멤버 밖, 그룹 밖 파일)는 명세에 빠진 영향 대상이므로 그룹 전체를 되돌리고 위치를 알린 뒤 종료 코드 1(`GroupMissingTarget`)입니다. 위치는 `<파일>#<멤버>`, 멤버 밖이거나 파일을 읽을 수 없으면 `<파일>:<줄>`, 파일이 없는 오류는 오류 코드입니다(예: `그룹 G1: 명세에 빠진 영향 대상: src/Web/OrdersController.cs#Cancel, src/App/Mapper.cs:12`). 검증할 수 없으면 파일을 고치기 전에 종료 코드 4(`GroupUnverifiable`)입니다: 그룹 파일이 속한 `.csproj`가 없음, 영향 범위를 확정할 수 없음(작업 공간 폴더가 2000개를 넘음, 목록이나 속성을 읽을 수 없는 폴더, 읽을 수 없는 솔루션·프로젝트 파일, 작업 공간 밖을 가리키거나 대상을 알 수 없는 링크·정션 폴더 — 작업 공간 안을 가리키는 링크는 건너뜁니다, `$(…)` 속성으로 경로를 만든 `ProjectReference`, 범위 안 프로젝트의 `ProjectReference`가 없는 `.csproj`를 가리킴), 레거시 프로젝트를 Windows 밖에서 빌드, 빌드 도구 없음, 기준 빌드의 비컴파일러 오류·시간 초과·시작 실패, 범위 안 다른 프로젝트가 참조하는 프로젝트에 기준 빌드부터 컴파일 오류가 있음(그 프로젝트를 참조하는 프로젝트는 컴파일되지 않아 깨져도 보이지 않습니다). 메시지는 `그룹 빌드 검증 불가 (<프로젝트>): <원인>` 꼴이며, 시간 초과는 `build.timeoutSeconds`의 초를, 비컴파일러 오류는 오류 코드·메시지와 조치를 적습니다. 편집 뒤의 검증 빌드가 검증할 수 없는 상태가 되면(시간 초과 등) 그룹을 되돌리고 종료 코드 4입니다. 그룹 승인은 빌드가 통과한 뒤 한 번 묻고(`--yes`·`--review-at-end`이면 묻지 않음) 거부하면 그룹을 되돌립니다(종료 코드 2). 성공하면 그룹의 모든 작업이 함께 `[x]`가 되고, 실패하면 그룹의 모든 작업에 `<!-- failed: <run-id> <종류> -->`가 붙습니다(종류: `GroupUnverifiable`, `GroupBuildFailed`, `GroupMissingTarget`, `GroupRejected`, `GroupTestGreenFailed`, `GroupTestRegression`, `GroupTestUnverifiable`, `GroupTestFileChanged` 또는 작업의 실패 종류). 되돌리는 중 pelly 밖에서 바뀐 파일은 덮어쓰지 않고 알리며(종료 코드 4), 그 파일의 작업은 `[x]`를 유지합니다(표시가 파일 상태와 맞도록). `--resume`은 `[x]`인 작업을 건너뛰고 첫 미완료 작업부터 이어 가므로, 실패한 그룹은 `[x]`가 아닌 작업만 한 그룹으로 다시 실행합니다. 그룹 일부가 `[x]`로 남았다면 그 작업들은 다시 실행되지 않으니 그 파일을 먼저 직접 확인하십시오. `spec check`와 `--resume`은 완료된 작업의 심볼·요청문 이름을 검사하지 않습니다(그룹이 이름을 바꿨을 수 있으므로). `--continue`는 실패한 그룹을 되돌린 뒤 다음 작업으로 가되, 그 그룹의 파일을 대상으로 하는 뒤 작업이 있으면 거기서 멈춥니다(검증할 수 없는 그룹이면 바로 멈춥니다). 첫 미완료 작업이 그룹 작업이면 `--dry-run`은 그 작업 하나만 그룹 모드(시그니처·이름 변경 허용)로 실행해 diff를 보여 줍니다. 영향 범위 탐색·기준 빌드·검증 빌드는 하지 않으므로 위의 종료 코드 4 사유도 검사하지 않고(dry-run이 통과해도 실제 apply가 검증 불가로 멈출 수 있습니다), 그룹의 나머지 작업은 실행하지 않으며, 파일과 명세 파일을 바꾸지 않습니다. 그룹 백업은 `.pelly/backup/<apply-id>.<그룹 ID>/`에 둡니다. 명세에 빠진 영향 대상은 위의 영향 탐색으로 미리 찾을 수 있습니다.
* **`--dry-run`:** 첫 미완료 작업 하나만 실행해 diff를 보여 주고, 파일과 명세 파일 어느 것도 쓰지 않습니다. 비대화형 실행에서도 `--yes` 없이 쓸 수 있습니다.
* **비대화형 실행에는 `--yes`가 필요합니다**(`--dry-run` 제외). 없으면 파일을 건드리기 전에 종료 코드 4입니다.
* **전체 작업이 성공하면** 명세를 `status: done`으로 바꿔 `changes/archive/<yyyy-MM-dd>-<id>.md`로 옮깁니다(같은 이름이 있으면 `-2`, `-3`). 하나라도 실패하면 보관하지 않고 `status: in-progress`로 둡니다.
* **기록:** 백업은 `.pelly/backup/<apply-id>/`, 요약은 `.pelly/runs/<apply-id>/apply.json`입니다. 작업별 실행 기록은 기존 `.pelly/runs/<run-id>/`입니다. apply 한 번이 작업 공간 잠금을 끝까지 잡습니다.
* **종료 코드:** 0 전부 성공, 1 검증 실패, 2 사용자 거부, 3 대상 찾기 실패, 4 환경·입력 오류(명세 오류, 실행 중 명세 파일을 바깥에서 고친 경우 포함), 5 안전 거부. `--continue`에서 전역적 오류(4)가 나면 거기서 멈춥니다.
* 명세 파일이 실행 중에 사람의 손으로 바뀌면 덮어쓰지 않고 종료 코드 4로 멈춥니다.

### `pelly spec draft` — 모델이 명세 초안을 쓰고 사람이 승인 (V2-4)
```bash
pelly spec draft <id> ["<요청>"] --scope <경로> [--scope <경로>…] [--dry-run] [--yes] [--force] \
                 [--config <경로>] [--model <태그>] [--endpoint <URL>] [--pause-on-exit]
```
변경 요청과 `--scope` 범위의 코드 색인(파일 경로, 멤버 시그니처, 본문 없음)을 모델에게 주고 작업 줄 초안을 받습니다. 초안을 `pelly spec check`와 같은 검사(문법, 대상 파일·멤버 존재, 문구 안 이름)와 영향 탐색에 통과시켜 보여 주고, **사람이 승인하면** `changes/<id>.md`(`status: draft`)로 씁니다. 실행은 하지 않습니다. 쓴 뒤에는 늘 하던 대로 `pelly spec check`로 확인하고 `pelly apply`로 실행합니다.

```text
pelly spec draft cancel-shipped "배송된 주문은 취소할 수 없게 하고, 취소 SP도 배송 완료 주문은 건너뛰게 해줘" --scope src/Orders --scope db/sp/usp_CancelOrder.sql
```

* **`--scope`(필수, 여러 번 가능):** 폴더, `.csproj`, `.cs`, `.sql`. 폴더는 그 아래 `.cs`·`.sql`을 모두 넣고(`bin`·`obj`·`.pelly` 등 기존 탐색이 빼는 폴더 제외, `AssemblyInfo.cs` 제외), `.csproj`는 그 파일이 있는 폴더를 폴더처럼 읽습니다. 모두 작업 공간 안이어야 하며, 작업 공간 밖이거나 없거나 지원하지 않는 형식이면 모델을 부르기 전에 경로를 밝히고 종료 코드 4입니다. 폴더가 2000개를 넘으면 색인이 일부만 되는 일이 없도록 「범위가 너무 넓어」 안내와 함께 종료 코드 4입니다. 해석할 수 없는 파일(파싱되지 않는 `.sql` 등)은 색인에서 빼고 「읽거나 해석할 수 없어 색인에서 뺀 파일」 경고로 알립니다. 색인할 `.cs`·`.sql` 파일이 하나도 없으면 「범위에 .cs·.sql 파일이 없습니다」로, 작업 공간 루트에 `.git`·`.svn`·`*.sln`이 없으면 그렇게 밝히고, 둘 다 모델을 부르기 전에 종료 코드 4입니다.
* **예산:** 첫 입력(시스템 프롬프트 + 색인 + 요청)이 4096 토큰을 넘으면 색인을 자르지 않고 모델을 부르지 않은 채 종료 코드 4입니다. 색인이 큰 파일 몇 개를 알려 주니 `--scope`를 좁히십시오. 계획 호출은 생각 2048 + 답 1024 토큰을 더하며 전체가 `num_ctx` 8192 안입니다(S6가 잰 조건, PRD §12.1의 예외). 생각 단계 옵션(`--think`)은 없습니다.
* **초안과 버린 줄:** 모델 답에서 작업 줄 문법(`- [ ] T<n> <cs|sql> <파일>[#심볼] <요청>`)에 맞는 줄만 씁니다. 작업 줄처럼 시작(`- [`)하는데 문법에 맞지 않는 줄은 고쳐 쓰지 않고 「형식에 맞지 않아 버린 줄」로 원문 그대로 보입니다. 설명 문장이나 코드 펜스는 조용히 무시합니다. 받은 작업 줄이 하나도 없으면 「초안에 작업이 없습니다」로 안내하고 승인 질문 없이 종료 코드 1입니다.
* **검사와 영향 탐색:** 오류가 있으면 영향 탐색을 하지 않고 그렇게 알립니다. 영향 탐색의 출발점은 **그룹(`[G1]`) 작업**뿐입니다. 거기서 찾은 C# 의미 기반 대상 중 멤버 안이고, 그 파일에 같은 이름의 멤버가 하나뿐이고, 출발 그룹이 하나인 대상은 코드가 그 그룹 끝에 작업 줄로 더합니다. 번호는 기존 최대 번호 다음부터 매기고, 요청문은 정해진 문구 「T<n>의 변경에 맞춰 이 코드를 고친다 (영향 탐색으로 추가)」입니다. 그룹 작업이면 시그니처를 바꾸는지와 상관없이 탐색합니다. 더할 줄을 합쳐 작업 수 상한 20을 넘으면 하나도 더하지 않습니다. SQL 대상, 이름 기준(부정확) 결과, 문자열 이름 호출 대상, SP를 부르는 ASP 호출은 더하지 않고 경고(SQL은 `pelly sql pull` 안내)만 냅니다 — 초안은 `asp` 작업을 제안하지 않습니다. 모델이 sql 작업에 `[G1]`을 달아 전에는 `SqlInGroup` 오류로 실행되지 않던 초안도 이제 그대로 검사·실행될 수 있습니다. SQL 영향 탐색은 `spec check --impact`와 같은 DB 선택을 따르고 `.pelly/impact/<id>-sql.json`을 씁니다.
* **승인:** 초안과 검사 결과를 보여 준 뒤 `changes/<id>.md에 쓸까요?` `[y/N]`를 묻습니다(기본 N). 승인하기 전에는 `changes/` 아래 아무것도 쓰거나 바꾸지 않습니다. 검사 오류가 있으면 질문 전에 「오류가 있는 명세는 `pelly apply`가 실행하지 않습니다. 쓴 뒤 고치십시오.」를 보이고, 쓴 파일의 `## 작업` 아래에 검사 결과를 HTML 주석으로 남깁니다. 초안의 `## 수용 기준`은 템플릿 자리(`- AC1: …`)입니다.
* **옵션:** `--dry-run`은 초안과 검사 결과만 보이고 쓰지 않습니다(종료 코드 0). `--yes`는 묻지 않고 씁니다. 비대화형 실행에는 `--yes` 또는 `--dry-run`이 필요하고 없으면 종료 코드 4입니다. 요청문을 생략하면 대화형에서 입력받고 비대화형이면 종료 코드 4입니다. 이미 `changes/<id>.md`가 있으면 종료 코드 4이고 `--force`로만 덮어쓰며(질문에 「이미 있는 파일을 덮어씁니다」가 붙습니다), 모델이 도는 사이에 파일이 생겨도 `--force` 없이는 덮어쓰지 않습니다.
* **종료 코드:** 0 쓰기 완료 또는 `--dry-run`, 1 초안에 작업이 없음, 2 승인 거부 또는 Ctrl+C, 4 환경·입력 오류(`--scope` 문제, 예산 초과, 이미 있는 파일, 요청문 오류, LLM 서버 사용 불가).
* **기록:** 초안(작업 1개 이상)이 나온 실행은 `.pelly/runs/<run-id>/draft.json`에 요청, 범위, 색인 요약, 프롬프트, 생각, 답, 버린 줄, 더한 줄, 검사 결과, 결과(`written`·`rejected`·`dryRun` 등)를 남깁니다. 작업이 하나도 없는 실행은 기록을 남기지 않고 버린 줄은 화면에만 나옵니다. 초안을 얼마나 고쳐 썼는지 나중에 재려면 이 원문이 필요합니다. 승인 전 검사에 쓰는 임시 파일은 `.pelly/drafts/<id>.md`이며 검사가 끝나면 지웁니다.
* **알려진 한계:** 생성자는 색인에 없습니다(S6가 잰 색인과 맞추려고 그대로 둠). 모델은 수용 기준과 `test:` 줄을 쓰지 않습니다. 모델이 지어낸 이름이나 이미 있는 것을 다시 하라는 지시는 검사가 일부만 잡으니 **승인 전에 초안을 직접 읽으십시오.** 한 번 부르고 끝이며 고쳐 쓰는 대화는 없습니다(요청을 고쳐 다시 실행하거나 파일을 손으로 고칩니다). 모델은 시그니처를 바꾸지 않는 한 곳의 동작 변경에도 `[G1]`을 자주 달아, 영향 탐색이 인터페이스·호출부·테스트 줄을 필요 없이 더할 수 있으니 승인 전에 지우십시오. sql 작업에 `[G1]`을 달면 `SqlInGroup` 오류로 초안의 영향 탐색이 건너뛰어지니, sql 줄에서 `[G…]`를 지우고 `pelly spec check --impact`를 돌리십시오. 모델의 작업과 영향 줄을 합쳐 작업 수 상한 20을 넘으면 영향 줄을 하나도 더하지 않고 「영향 대상 N개를 더하면 작업 수 상한 20을 넘어 더하지 않았습니다」로 알립니다(대상별 경고는 남음). 실측은 [사전 측정](docs/spec-draft-premeasure.md)을 보십시오. 모델 한 번 실행에 수십 초 걸립니다. Windows 미확인.

### `pelly test` — 메서드의 현재 동작을 고정하는 테스트 만들기 (V2-5)
```bash
pelly test <파일> ["<힌트>"] [--class <클래스>] [--method <메서드>] [--line N] [--count 1-5] \
           [--test-project <경로>] [--yes] [--dry-run] [--max-retries N] [--verbose] [--pause-on-exit] \
           [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
```
테스트가 없는 C# 메서드 하나의 **지금 동작**을 고정하는 테스트(특성 테스트) 1~5개(기본 3)를 모델이 쓰고, 기존 테스트 프로젝트의 `<타입>Tests` 클래스에 넣습니다(없으면 `<타입>Tests.cs`를 새로 만듭니다). 기대값은 사람이 정하지 않습니다. 지금 코드가 내는 값이 정답입니다. 이후 `pelly cs`(`--test`)나 사람의 수정이 기존 동작을 깨면 이 테스트가 잡습니다. 명령 이름이 `pelly cs --test`와 비슷하지만 하는 일이 다릅니다. `cs --test`는 수정을 검증하고, `pelly test`는 수정 없이 테스트만 더합니다.

```text
pelly test Shop.Api/Services/OrderService.cs --method SearchAsync --count 3
```

* **판정(앞이 실패하면 뒤는 하지 않음):** (1) 형태: 테스트 메서드 수가 1~`--count`, 건너뛰기 없음, 이름 겹침 없음. (2) 단언·호출: 각 테스트가 대상을 부르고 의미 있는 단언이 있음(`Assert.True(true)` 같은 리터럴만의 단언은 거부). (3) 환경 의존 금지: 시간·난수·파일·DB·네트워크·`Environment`. (4) 빌드. (5) 현재 코드에서 통과(기존 테스트는 원본 상태와 비교해 새 실패 없음). (6) 한 번 더 실행해 같은 결과. (7) **변이 검출**: 대상 본문을 `throw new System.NotImplementedException("pelly-mutation");`으로 바꿔 빌드·실행했을 때 새 테스트가 각각 실패. 어긋나면 이유를 피드백으로 주고 재시도합니다(`retry.max`). 변이는 판정 1~6을 통과한 뒤 시도당 한 번입니다.
* **승인:** 통과하면 테스트 파일 diff를 보이고 `남길까요? [y/N]`를 묻습니다. 거부, 재시도 소진, 취소면 테스트 파일(과 레거시 csproj)을 원래대로 되돌리고 새로 만든 파일은 지웁니다. `--yes`는 묻지 않고 남기며, `--dry-run`은 판정까지 하고 diff만 보인 뒤 되돌립니다(종료 0).
* **대상 조건:** 메서드만 받습니다. `public`이거나, `internal`이고 운영 어셈블리에 테스트 어셈블리를 가리키는 `InternalsVisibleTo`가 있어야 합니다. 추상·제네릭 타입, 반복기(`yield`), `extern`·본문 없는 메서드는 종료 3입니다. 테스트 프로젝트가 없거나 여럿인데 `--test-project`가 없어도 종료 3입니다. 이 명령은 테스트 프로젝트를 만들지 않습니다.
* **테스트 프레임워크:** 테스트 프로젝트의 패키지 참조로 xUnit(v2·v3)·MSTest·NUnit 중 하나를 정합니다. 없거나 둘 이상이면 종료 4입니다. 테스트별 결과(TRX)를 읽을 수 없는 실행 방식(`MSTest.Sdk` 등 다른 Testing Platform, `{trx}` 없는 레거시 `test.command`)은 판정할 수 없어 종료 4입니다.
* **운영 코드 보호:** 변이는 작업 사본의 운영 파일을 **잠깐** 바꿉니다(빌드가 실제 프로젝트를 써야 하므로). 끝나면 성공·실패·Ctrl+C 모두 원래 바이트로 되돌리고 해시를 확인합니다. IDE가 파일 변경을 감지해 다시 읽을 수 있고, 변이 빌드의 결과물은 다음 빌드까지 `bin`·`obj`에 남으니 끝난 뒤 한 번 다시 빌드하십시오. 프로세스가 강제로 죽어 되돌리지 못하면 `.pelly/runs/<run-id>/chartest.pending`이 남고, `pelly test`·`cs`·`sql`·`apply`는 「이전 `pelly test` 실행이 끝나지 않았습니다」와 백업 경로를 보이고 종료 4로 멈춥니다. 자동 복원하지 않습니다. 파일을 확인하고 표시 파일을 지우면 풀립니다.
* **종료 코드:** 0 남김(또는 `--dry-run`), 1 재시도 소진, 2 거부 또는 취소, 3 대상·테스트 프로젝트·테스트 클래스 문제, 4 환경(원본 테스트 판정 불가, 변이 빌드 실패, 미완료 표시, 실행 방식, 프레임워크 판별 실패). 변이가 빌드되지 않으면 통과로 치지 않고 종료 4입니다.
* **기록:** `summary.json`의 `charTest` 블록(`requested`, `generated`, `kept`, `passedOnCurrent`, `stableOnRerun`, `killedByMutation`, `rejectedBy`, `framework`, `newFile`)과 실패 유형 `CharTestShape`·`CharTestNoAssert`·`CharTestEnvDependent`·`CharTestBuild`·`CharTestFailsOnCurrent`·`CharTestRegression`·`CharTestFlaky`·`CharTestSurvivesMutation`·`CharTestMutationUnbuildable`·`CharTestBaselineUnusable`·`CharTestPending`. `pelly report`에 「특성 테스트」 줄(실행 수, 성공률, 평균 시도, 실패 유형)이 나옵니다.
* **알려진 한계:** 테스트는 현재 동작을 그대로 고정하므로 **지금 코드에 버그가 있으면 그 버그가 정답으로 굳습니다.** 남기기 전에 diff의 기대값을 읽으십시오. 변이는 「본문 → 예외」 하나뿐이라 조건 하나를 뒤집는 수정은 못 잡을 수 있습니다. 의존성이 많은 메서드(DB 직접 호출 등)는 환경 의존 금지에 걸려 재시도가 소진되기 쉽습니다. 레거시 .NET Framework 테스트 실행은 Windows에서만 되고 **Windows 미확인**입니다. 실측은 [예비 측정](docs/chartest-premeasure.md)(Mac, Modern만)을 보십시오.

### `pelly asp` — Classic ASP(VBScript) 함수 하나나 줄 범위 수정 (V2-6)
```bash
pelly asp <파일.asp|.inc> ["<요청>"] [--proc <이름> | --line N | --lines A-B] [--yes] [--dry-run] [--max-retries N] \
          [--verbose] [--pause-on-exit] [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
```
Classic ASP 파일(`.asp`·`.inc`) 하나에서 대상 하나를 모델이 고치고, 코드가 실행 없이 할 수 있는 판정을 모아 확인합니다. 흐름은 `cs`·`sql`과 같습니다(PREFLIGHT → LOCATE → SLICE → EDIT → APPLY → VERIFY → REVIEW, 실패하면 피드백으로 재시도, 끝내 실패하거나 거부하면 원래 바이트로 되돌림). 모델 출력 형식은 `sql`과 같은 SEARCH/REPLACE 블록이고 프롬프트 판은 `asp-v2`입니다.

```text
pelly asp admin/order/list.asp "검색 조건에 주문 상태를 추가해 줘" --lines 24-37
pelly asp inc/func_order.inc "상태 코드 4는 반품으로 표시" --proc StatusName
pelly asp admin/member/edit.asp "휴대폰 번호 저장 전에 하이픈 제거" --line 88
```

* **대상:** `--proc <이름>`은 그 이름의 `Function`·`Sub`·`Property`(대소문자 무시, 클래스 안이면 `클래스.이름`). `--line N`은 N번 줄을 감싸는 프로시저이고, 감싸는 것이 없으면(페이지 본문 코드) 「`--lines A-B`로 범위를 주십시오」와 함께 종료 3입니다. `--lines A-B`는 줄 범위 그대로이며 HTML과 `<% %>`가 섞여도 됩니다. 범위 안의 지시문(`<%@ %>`)·include 줄은 잠가 편집 영역에서 뺍니다. 셋 다 없으면 프로시저가 하나일 때 그것, 여럿이면 선택 화면(비대화형이면 종료 3)입니다.
* **받지 않는 파일(종료 3):** `asp.extensions`에 없는 확장자, `global.asa`(확장자 설정과 상관없이), 서버 JScript 페이지·블록(`AspUnsupportedLanguage`), 대상 범위가 닫히지 않은 `<%`·`<script runat>` 안에 걸친 경우.
* **판정(앞이 실패하면 뒤는 하지 않음, 모두 원본 기준선 대비 새로 생긴 것만 셈):**

  | 순서 | 검사 | 실패하면 |
  | :-: | :--- | :--- |
  | 1 | 패치 적용: 편집 영역 밖 SEARCH, 일치 실패, 변경 없음 | 재시도 |
  | 2 | 인코딩: 새 텍스트를 파일 인코딩으로 쓸 수 있는가(예: CP949 파일의 이모지) | 재시도, 끝내 `AspEncoding` |
  | 3 | 구조: `<% %>`·`<script runat>` 짝, VBScript 블록 짝(`If`·`For`·`Do`·`Select`·`With`·`Sub`·`Function`·`Class` 등), 닫히지 않은 문자열, HTML 태그 짝 | 재시도, 끝내 `AspStructureErrors` |
  | 3′ | 모양: 지시문·include 줄 불변, 프로시저 대상이면 이름·종류·매개변수 그대로(요청과 상관없이) | 재시도, 끝내 `ShapeRejected` |
  | 4 | 안전: `ShellObject`, `DynamicCode`, `OnErrorResumeNext`, `SqlConcatRequest`(아래) | 재시도, 끝내 종료 5 `AspSafetyRejected` |
  | 5 | 이름: 새로 쓴 이름이 VBScript 내장, ASP 내장 개체, 파일과 include의 선언, 매개변수 어디에도 없으면 오류 | 재시도, 끝내 `AspUnknownNames` |
  | 6 | 규칙: 프로젝트 규칙의 금지 패턴(`pelly rules`) | 재시도 |
  | 7 | VBScript 컴파일(Windows `cscript`, 실행하지 않고 컴파일만) | 재시도, 끝내 `AspCompileErrors` |
  | 8 | SP 확인: 새로 부르는 SP가 개발 DB에 있는가(`sql.connectionString`이 있을 때만) | 재시도, 끝내 `AspSpNotFound` |

* **클라이언트 `<script>`·`<style>` 블록에 걸친 범위(V2-7 W2):** 고칠 범위(`--lines`, `--proc` 모두 프로시저 안 HTML 포함 편집 영역)가 클라이언트 블록과 겹치면, 겹친 블록마다 `pelly js`·`pelly css`와 같은 JS·CSS 판정을 위 표에 행으로 더합니다. 막는 행: `JsSyntax`(새 문법 오류), `JsLevel`(문법 수준 초과), `JsSafety`(`eval` 등, 끝내 남으면 종료 5), `CssSyntax`, `CssSafety`. 경고만 하는 행: `JsNames`, `JsApi`. 페이지에 `<script>` 블록이 있으면 범위와 상관없이 경고 행 `JsElements`(요소 검사: 편집 때문에 새로 끊긴 id·name 참조, HTML만 고쳐 id를 지운 경우 포함)도 붙습니다. 범위가 `<script>` 블록에 걸치고 그 안 함수의 이름·매개변수가 바뀌면 경고 행 `JsSignature`(검토 줄 「JS 서명」: 「함수 서명 변경: goPage(n) → goTo(n), 부르는 곳 N곳」)가 붙습니다(W3). 막지는 않으니, 부르는 곳을 함께 고치려면 명세로 하십시오. 고친 파일에 폼·ajax·`Request` 읽기가 있으면 경고 행 `FormFields`(검토 줄 「폼 필드」)가 붙습니다(W3b). 폼 필드 경고는 편집 때문에 서버 페이지가 읽는 키(`Request.Form("k")`·`Request("k")`)와 그 페이지로 보내는 폼(`<form>` 필드, JS가 정하는 `frm.action = "save.asp"` 포함)·jQuery ajax(`$.ajax`·`$.post`·`$.get`의 `data` 객체)의 필드 이름이 새로 어긋날 때 나옵니다. 예: 「admin/order/save.asp가 읽는 custNo(줄 14)를 admin/order/list.asp의 폼(줄 40)이 보내지 않음, 보내는 비슷한 이름: custId」, 「…가 읽는 writer(줄 6)를 보내는 폼·ajax가 없음(보내는 곳 1곳)」. 확실한 짝만 봅니다: 키가 변수인 읽기(`Request(name)`, `For Each … In Request.Form`)가 있는 페이지(include 포함), 필드를 다 알 수 없는 보내는 쪽(`serialize()`, 이름을 VBScript로 이어 붙인 필드, 폼 안의 함수 호출·include, JS가 필드를 더하는 폼), 주소를 모르는 보내는 쪽(변수, `<%`)은 건너뜁니다. 보내는 쪽이 하나도 없는 페이지와 편집 전부터 있던 어긋남은 경고하지 않습니다. 블록에 걸치지 않는 범위에는 행이 생기지 않고, 블록 안 `<%= %>`가 바뀌어도 됩니다(서버 코드는 ASP 판정이 봅니다). 새로 넣은 블록의 문법 수준 기준선은 `web.jsTarget`(`auto`면 페이지의 다른 블록 중 가장 낮은 수준, 없으면 ES5)입니다. 모델에게는 줄 범위가 블록에 걸칠 때만 그 블록의 문법 수준과 jQuery 버전을 알립니다.
* **한계 — 규칙 검사는 페이지 안 JS를 보지 않음:** `pelly asp`의 규칙 검사(위 표 6번)는 지금처럼 VBScript만 드러내고 클라이언트 JS·CSS는 가립니다. 프로젝트 규칙의 금지 패턴에는 언어 구분이 없어, 블록을 드러내면 VBScript용 패턴이 JS에 걸려 기존 결과가 바뀌기 때문입니다. 그래서 `pelly asp`로 고친 페이지 안 JS에는 프로젝트 규칙이 걸리지 않습니다. 프로젝트 규칙까지 JS에 걸려면 `pelly js`로 고치십시오(`pelly js`의 규칙 검사는 블록 텍스트에 적용).
* **「실행 검증 없음」:** 어떤 검사도 페이지를 실행하지 않습니다. 그래서 검토 화면에는 diff와 함께 판정 줄(예: `검사: 구조 통과, 안전 통과, 인코딩 통과, 이름 통과, 컴파일 건너뜀(Windows가 아님), SP 건너뜀(…), 논리 통과`)과 고정 문구 「ASP는 실행해서 확인하지 않았습니다. 화면에서 직접 확인하십시오.」가 나옵니다. `--yes`여도 이 두 줄은 화면과 실행 기록에 남습니다. 승인한 뒤 **화면을 직접 띄워 확인하십시오.**
* **「논리」 경고(거부하지 않음):** 대상 범위 안에서 새로 생긴 것만 검토 판정 줄에 「논리 경고(…)」로 보입니다. (1) `If`·`ElseIf` 한 조건에서 같은 식을 `IsNumeric`·`IsDate`로 검사하면서 `CLng`·`CInt`·`CDbl`·`CDate`로 변환함(`And`·`Or`로 이어짐) → 「And/Or는 단축 평가를 하지 않습니다: <식>를 같은 조건에서 검사·변환합니다」. VBScript는 단축 평가를 하지 않아 숫자가 아닌 값에서 페이지가 멈춥니다. (2) 원본 대상에서 `Request…`를 읽어 대입하던 변수에 고친 뒤 그런 대입이 없음 → 「기존 입력값 읽기 줄이 사라졌습니다: <변수>」. 둘 다 컴파일·이름 검사로는 잡히지 않으니 diff에서 직접 확인하십시오.
* **건너뛰는 검사:** Windows가 아니거나 `cscript`·VBScript를 쓸 수 없으면(Windows 11 24H2부터 VBScript는 선택적 기능) 컴파일 검사는 실패가 아니라 「건너뜀」입니다. 개발 DB 연결이 없으면 SP 확인과 SP 문맥도 건너뜁니다. 운영 연결(`sql.production`)은 ASP에서 쓰지 않습니다. 못 찾은 include가 있거나 `Execute`·동적 include가 있는 파일은 이름을 확신할 수 없어 이름 검사를 오류가 아니라 경고로 낮춥니다.
* **ADO 상수(`adOpenStatic`·`adCmdStoredProc` 등):** 다음 중 하나면 정의된 이름으로 봅니다 — 페이지나 include에 `adovbs.inc` 또는 typelib `METADATA`가 있음, `<asp.siteRoot>/global.asa`에 typelib `METADATA`가 있음(읽기만 함), `asp.adoConstants`가 `true`, 원본 페이지가 이미 선언 없이 ADO 상수를 씀. 그 밖에는 새로 쓴 ADO 상수도 모르는 이름입니다.
* **SQL 문자열 연결(`SqlConcatRequest`):** `SELECT`·`INSERT`·`UPDATE`·`DELETE`·`EXEC`·`EXECUTE`·`MERGE`를 단어로 담은 문자열에 `Request(…)`·`Request.Form`·`Request.QueryString`·`Request.Cookies` 값, 또는 그 값을 받은 변수를 `&`(문자열이나 SQL 변수와 함께 쓴 `+` 포함)로 붙이면 거부합니다.
  * 허용하는 감싸기: `CLng`·`CInt`·`CDbl`·`CBool`·`CDate`, `Replace(x, "'", "''")`(두 번째·세 번째 인수가 정확히 그 문자열), `asp.sqlSanitizers`의 현장 함수(`Util.SafeSQL(x)`처럼 점이 붙어도 마지막 이름으로 봄). `ADODB.Command`의 `CreateParameter`로 넘긴 값은 연결이 아니라 처음부터 대상이 아닙니다.
  * `Trim(Request("id"))`처럼 허용 목록 밖의 함수로 감싼 값은 여전히 위험한 값입니다.
  * 순서를 봅니다. 변수의 상태는 그 문장 앞의 마지막 대입으로 정해지므로 `id = CLng(id)` 뒤의 `id`는 안전합니다. 다만 조건 안(`If … Then` 한 줄, `If`·`Select Case` 블록)의 대입은 위험을 더할 수만 있고 지우지는 못합니다.
  * `sql = sql & " AND x='" & Request("x") & "'"`처럼 같은 범위에서 SQL 문자열을 받은 변수에 이어 붙이는 것도 연결로 셉니다.
  * `w = " WHERE 1=1"` … `w = w & " AND b='" & Request("b") & "'"` … `sql = "SELECT …" & w`처럼 나중에 SQL 문자열과 이어지는 변수(WHERE 조각 변수)는 처음 대입부터 SQL 변수로 보므로, 조각을 붙이는 줄마다 따로 셉니다.
  * 범위는 `Sub`·`Function` 하나(또는 페이지 수준 코드)입니다. 페이지 수준(`Sub`·`Function` 밖)에서 위험한 값을 받은 전역 변수를 `Sub`·`Function` 안에서 쓰는 경우는 따라가지 않습니다.
  * 원본에 이미 있던 같은 문장은 막지 않습니다(「새로 늘리지 않음」). 하지만 **이미 위험한 줄을 고치면 그 줄은 새 문장**이 되어 거부되고, 피드백이 그 값을 감싸라고 알려 줍니다.
* **그 밖의 안전 규칙:** `CreateObject`·`Server.CreateObject`의 `WScript.Shell`·`Shell.Application`·`WScript.Network`, 원본 파일이 쓰지 않던 `Scripting.FileSystemObject`(`ShellObject`), `Execute`·`ExecuteGlobal`·`Eval`(`DynamicCode`), 새 `On Error Resume Next`(`OnErrorResumeNext`).
* **인코딩과 줄 끝:** 편집 영역 밖은 원본 **바이트 그대로**입니다(줄 끝이 섞인 파일, 끝 줄바꿈 유무 포함). 인코딩은 BOM → `<%@ … CodePage=N %>`(65001 → UTF-8, 949 → CP949, 그 밖은 종료 4) → ASCII 아닌 바이트가 엄격한 UTF-8이면 UTF-8, 아니면 CP949 → 순수 ASCII면 `asp.encoding`(기본 `cp949`) 순서로 정합니다. 그래서 **`asp.encoding`은 사이트의 실제 인코딩과 같아야 합니다**: 순수 ASCII 파일에 한글을 넣으면 이 값으로 저장됩니다(include 파일은 각자 자기 바이트로 판정). 정한 인코딩으로 원본을 엄격하게 읽지 못하면(예: `CodePage=949`인데 UTF-8 한글) 종료 4이고, UTF-16 파일은 지원하지 않습니다(종료 4).
* **해석기 동작:** 블록 짝은 HTML을 사이에 두고 파일 전체로 셉니다(`<% If a Then %> … <% Else %> … <% End If %>`의 일부만 줄 범위로 고쳐도 됩니다). IIS와 같이 VBScript 문자열 안의 `%>`도 코드 블록을 끝냅니다(그래서 레거시 코드에 `"%" & ">"`가 쓰입니다).
* **설정(`asp.*`, 작업 공간 `pelly.json`):**

  | 키 | 기본 | 뜻 |
  | :--- | :--- | :--- |
  | `asp.siteRoot` | 작업 공간 루트 | `<!--#include virtual="…"-->`의 기준 폴더. 상대 경로는 작업 공간 루트 기준 |
  | `asp.encoding` | `cp949` | 순수 ASCII 파일을 쓸 인코딩(`cp949`·`utf-8`) |
  | `asp.extensions` | `[".asp", ".inc"]` | 대상 확장자(대소문자 무시). `global.asa`는 넣어도 거부 |
  | `asp.sqlSanitizers` | `[]` | SQL 문자열에 넣어도 되는 현장 처리 함수 이름(예: `SafeSQL`). 프롬프트에도 알려 줌 |
  | `asp.spCallFunctions` | `[]` | SP를 부르는 공용 래퍼, 예: `[{"name":"ExecSP","argument":1}]`(SP 이름 인수 위치, 1부터) |
  | `asp.adoConstants` | `false` | `true`면 ADO 상수(`adOpenStatic` 등)를 어디서나 정의된 이름으로 봄(사이트가 다른 방법으로 typelib을 쓸 때) |

  ```json
  { "asp": { "siteRoot": ".", "encoding": "cp949", "sqlSanitizers": ["SafeSQL"] } }
  ```
  ASP 사이트에는 `.git`·`.svn`·`.sln`이 없을 수 있어 작업 공간 루트가 파일 폴더로 떨어질 수 있습니다. 그때 `virtual=` include를 못 찾으면 실행 메시지가 `asp.siteRoot`를 안내합니다. `pelly doctor`는 설정에 `asp` 절이 있을 때만 「ASP 구문 검사(cscript)」, 「ASP 사이트 루트」, 「ASP SQL 처리 함수」 행을 보입니다.
* **cscript 확인 방법(Windows, cmd):** (1) `winver` — Windows 11 24H2 이상·Server 2025면 VBScript는 선택적 기능이라 PowerShell `Get-WindowsCapability -Online | Where-Object Name -like 'VBSCRIPT*'`가 `Installed`여야 합니다. (2) `echo WScript.Echo "ok" > %TEMP%\t.vbs` 뒤 `cscript //Nologo %TEMP%\t.vbs` → `ok`. (3) `echo If x Then > %TEMP%\bad.vbs` 뒤 `cscript //Nologo %TEMP%\bad.vbs` → `…\bad.vbs(2, 1) Microsoft VBScript 컴파일 오류: 'End'가 필요합니다.`(영어 Windows는 `compilation error`). pelly는 서버 코드 조각을 한 줄에 하나씩(`:`로 잇지 않음) 임시 `.vbs`(`.pelly/runs/<run-id>/asp-compile-<n>.vbs`)로 옮기고, `Option Explicit` 문장 바로 다음 줄(없으면 첫 줄)에 `WScript.Quit 0`을 따로 둔 뒤 `cscript //Nologo //T:10 //E:vbscript`로 컴파일만 합니다. 오류 줄은 스크립트 줄에서 페이지 줄로 되돌려 보입니다. include는 합치지 않고 대상 파일만 컴파일합니다. `cscript`는 첫 컴파일 오류에서 멈추므로, 원본에 이미 컴파일 오류가 있으면 새 오류가 없어도 컴파일 검사는 「원본에 컴파일 오류가 있어 첫 오류 뒤는 검사하지 못했습니다」 경고입니다.
* **종료 코드:** 0 적용(또는 `--dry-run`), 1 재시도 소진, 2 거부·중단, 3 대상 못 찾음·모호·받지 않는 파일, 4 환경(인코딩 판정 실패, 모델·설정, 잘못된 인자), 5 안전 거부.
* **기록:** `summary.json`의 `asp` 블록(`unit`: `Procedure`·`Lines`, `encoding`, `checks`: 검사별 `passed`·`warned`·`skipped`·`failed`, `storedProcedures`, `includesFound`, `includesMissing`, `executed`: 늘 `false`)과 실패 유형 `AspStructureErrors`·`AspUnknownNames`·`AspCompileErrors`·`AspSafetyRejected`·`AspEncoding`·`AspSpNotFound`·`AspUnsupportedLanguage`·`AspUnsupportedFile`. `pelly report`에 「ASP: 실행 N건, 성공 K건, 컴파일 검사 건너뜀 M건, 실패 유형 …」 줄이 나옵니다.
* **하지 않는 것:** 실행 검증(IIS·브라우저), `global.asa`, 서버 JScript, 여러 파일 동시 수정·새 파일·include 추가·지시문 변경, XSS 검사, `pelly ui asp`. 명세에서 SP와 함께 고치는 일은 `pelly spec`·`pelly apply`의 `asp` 작업(A2)이 맡습니다.
* **알려진 한계:** 실행하지 않으므로 화면 결과·런타임 오류·세션 의존 동작은 확인하지 못합니다. 컴파일 검사는 Windows에서만 되고, 현장 개발 PC의 `cscript`는 아직 확인하지 않았습니다(**Windows 현장 미확인**, 별도 Windows 11 23H2 PC에서만 확인). 실측은 [예비 측정](docs/asp-premeasure.md)(Mac, 컴파일 검사 건너뜀)을 보십시오.

### `pelly js`·`pelly css` — 클라이언트 JavaScript·CSS 함수·규칙 하나나 줄 범위 수정 (V2-7)
```bash
pelly js  <파일.js|페이지>  ["<요청>"] [--function <이름> | --line N | --lines A-B] [--signature] [--yes] [--dry-run] [--max-retries N] \
          [--verbose] [--pause-on-exit] [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
pelly css <파일.css|페이지> ["<요청>"] [--selector "<선택자>" | --line N | --lines A-B] [그 밖의 옵션은 pelly js와 같음]
```
따로 있는 `.js`·`.css` 파일 하나, 또는 페이지 하나의 `<script>`·`<style>` 블록 하나에서 대상 하나를 모델이 고치고, 코드가 **브라우저에서 실행하지 않고, 외부 도구(Node·ESLint) 없이** 할 수 있는 판정을 모아 확인합니다. 흐름은 `cs`·`sql`·`asp`와 같습니다(PREFLIGHT → LOCATE → SLICE → EDIT → APPLY → VERIFY → REVIEW, 실패하면 피드백으로 재시도, 끝내 실패하거나 거부하면 원래 바이트로 되돌림). 모델 출력 형식은 SEARCH/REPLACE 블록이고 프롬프트 판은 `js-v1`·`css-v1`(페이지는 `js-page-v1`·`css-page-v1`)입니다. JS 해석은 pelly에 들어 있는 `Acornima` 1.9.0 버전이 합니다.

```text
pelly js  js/order/list.js "검색어가 비어 있으면 알림을 띄우고 멈춰 줘" --function fnSearch
pelly js  js/common.js "날짜 입력 검사에서 빈 값은 통과" --line 9
pelly js  js/order/list.js "Esc 키를 누르면 검색어 입력란을 비워 줘" --lines 45-50
pelly css css/admin.css "목록 칸 위아래 여백을 6px로" --selector ".list_tbl td"
pelly css css/admin.css "인쇄할 때 파란 버튼 숨기기" --lines 12-14
pelly js  admin/order/list.asp "검색 버튼 누를 때 주문 상태도 넘겨 줘" --function fnSearch
pelly js  admin/member/edit.asp "휴대폰 번호 하이픈 자동 입력" --lines 140-175
pelly css admin/order/list.asp "목록 표 머리글 높이 32px" --selector ".list_tbl th"
```

* **페이지 안 블록 (W2):** 페이지는 `asp.extensions`(기본 `.asp`·`.inc`)와 `.htm`·`.html`(대소문자 무시)입니다. 대상은 **클라이언트 `<script>`(JavaScript) 또는 `<style>` 블록 안**에 있어야 합니다.
  * **블록으로 보는 것:** `runat="server"`가 없고 `type`·`language`가 JavaScript인(없어도 되는) `<script>`, `type`이 없거나 `text/css`인 `<style>`. 일반 HTML 주석 안, ASP 코드 안의 태그, 클라이언트 VBScript, `text/template` 같은 JS가 아닌 `<script>`는 블록이 아닙니다. 블록은 `</script`(`</style`)에서 끝나므로 JS 문자열 안의 `"</script>"`도 블록을 끝냅니다(브라우저와 같음).
  * **대상 지정:** `--function`은 페이지의 모든 `<script>` 블록에서 찾고(같은 이름이 여러 블록에 있으면 줄 번호와 함께 선택 화면, 비대화형이면 종료 3), include한 `.inc`의 함수는 찾지 않습니다. `--line N`은 N번 줄이 블록 안이어야 하고, `--lines A-B`는 **블록 하나의 내용 안에 완전히** 들어가야 합니다. 블록 밖이거나 걸치면 「N번 줄은 <script> 블록 안이 아닙니다. pelly asp를 쓰십시오」·「줄 A-B 범위가 <script> 블록 밖에 걸칩니다. pelly asp --lines를 쓰십시오」와 함께 종료 3(`WebOutsideBlock`)입니다. 셋 다 없을 때 블록이 없으면 「이 페이지에는 고칠 수 있는 <script> 블록이 없습니다」(종료 3), 닫히지 않은 블록은 「줄 N의 <script>가 닫히지 않았습니다」(종료 3)입니다.
  * **블록마다 따로 판정합니다.** 브라우저가 `<script>` 블록마다 따로 실행하므로 문법 수준·원본 해석 결과·안전 기준선을 블록마다 따로 정하고, 새로 생긴 것만 셉니다. 다른 블록의 원래 오류나 낡은 문법 때문에 막히지 않습니다. 결과의 줄 번호는 페이지 줄 번호 그대로입니다.
  * **블록 안 서버 코드:** `<%= 식 %>`는 해석할 때 자리 표시 이름으로 바꾸어 보므로(문자열 안이면 문자열의 일부, 밖이면 식별자) `var cnt = <%= cnt %>;`가 든 정상 스크립트를 문법 오류로 막지 않습니다. `<% If … %> … <% End If %>`처럼 서버 **문장**이 섞인 블록은 분기마다 다른 JS가 가므로 문법·문법 수준 검사를 「건너뜀(서버 코드가 섞인 스크립트)」으로 하고 괄호·문자열·주석 짝 비교와 텍스트 기준 안전 검사로 낮춥니다.
  * **블록 경계와 서버 코드는 읽기 전용입니다.** APPLY가 쓰기 전에 (1) 수정한 페이지의 블록 목록·여는/닫는 태그·대상 블록 밖 모든 바이트가 원본과 같은지(다르면 `WebBlockChanged`, 예: 문자열 안 `</script>` → 모델에게 `<\/script>`를 쓰라고 알리고 재시도), (2) 대상 블록 안 `<% %>`·`<%= %>`의 순서와 내용이 원본과 같은지(다르면 `WebServerCodeChanged`, 재시도하고 끝내 안 되면 종료 1)를 확인합니다. 끝내 실패하면 REVIEW 문구는 「서버 값이 필요하면 pelly asp --lines를 쓰십시오」입니다. 서버 값이 새로 필요하면 `pelly asp --lines`로 고치고, 이때 걸친 블록에는 JS·CSS 판정이 더해집니다.
  * **인코딩:** 블록 밖은 바이트 그대로입니다. 인코딩은 `pelly asp`와 같은 순서(BOM → `CodePage` → 엄격한 UTF-8 → CP949 → 순수 ASCII는 `asp.encoding`)이고, `.htm`·`.html`에는 `CodePage`가 없으므로 BOM 다음에 `<meta charset="…">`·`<meta http-equiv="Content-Type" content="…; charset=…">`를 읽습니다(`utf-8` → UTF-8, `euc-kr`·`ks_c_5601-1987` 등 → CP949, 그 밖은 종료 4).
  * **이름과 jQuery:** 페이지 대상의 이름 검사(경고만)는 W1 목록에 같은 페이지의 다른 블록 선언, include한 파일의 블록 선언, 페이지가 `<script src>`로 부르는 `.js`(include 안의 것 포함, 외부 주소와 `<%= %>` 주소는 무시)의 최상위 선언을 더합니다. 다른 페이지에만 있는 함수는 그 페이지에서 보이지 않으므로 경고합니다. 공용 `.inc`가 대상이면 그 `.inc`를 include하는 페이지들에서 보이는 이름의 합집합과, 그 페이지들이 부르는 jQuery 중 가장 낮은 버전을 씁니다. `.js` 파일 대상은 모든 페이지 블록의 선언도 아는 이름으로 칩니다(공용 `.js`가 페이지 안 전역 함수를 부르는 일이 흔하므로). jQuery 기준 버전은 `web.jqueryVersion` 설정 → 페이지가 부르는 jQuery 본체의 버전 → 사이트에서 찾은 가장 낮은 버전 순이고, 어디서 정했는지(`setting`·`page`·`site`)를 검토와 기록에 적습니다.
  * **모델 입력:** 프롬프트는 `js-page-v1`·`css-page-v1`이고 [READ-ONLY: env]에 「ASP 페이지의 <script> 블록」·블록 문법 수준·jQuery 버전과 출처·「`<%= … %>`와 `<% … %>`는 서버 코드이므로 바꾸지 않는다」·「`</script>`를 문자열에 쓰지 않는다(`<\/script>`)」를 넣고, [READ-ONLY: page](같은 페이지 다른 블록·include 블록의 최상위 선언)와 [READ-ONLY: html](대상이 쓰는 id·name이 붙은 요소의 태그 줄, 최대 20줄)을 더합니다. 입력 칸 이름을 모델이 지어내지 않게 하려는 것입니다.
  * **검토:** diff 위에 대상 블록 위치 한 줄(예: `admin/order/list.asp 120–260줄 <script> 블록`)을 더 보입니다. 규칙 검사(7번)는 블록 텍스트에 적용되어 프로젝트 규칙의 JS 금지 패턴이 페이지 안 JS에도 걸립니다.
  * **대상이 아닌 것:** 인라인 이벤트 속성(`onclick="fnSave()"`)·`style="…"` 속성·`href="javascript:…"`, VBScript 문자열로 출력하는 스크립트(`Response.Write "<script>…"`), 서버 코드 바꾸기. 이런 곳은 `pelly asp --lines`로 고칩니다(텍스트로). 블록 안 `<!-- … //-->` 옛 숨김 감싸기는 해석할 때만 공백으로 바꿉니다.
* **`pelly js` 대상:** `--function <이름>`은 `function 이름(…)` 선언, `var|let|const 이름 = function …`·화살표 함수, `이름: function …`, `객체.이름 = function …`, `객체.prototype.이름 = function …`입니다(`--function Order.save`처럼 `객체.이름`으로도 씀). 같은 이름이 여럿이면 줄 번호와 함께 선택 화면(비대화형이면 종료 3)입니다. `--line N`은 N번 줄을 감싸는 **가장 안쪽의 이름 있는 함수**이고, jQuery의 `$(function () { … })`·`.click(function () { … })` 같은 이름 없는 함수는 건너뜁니다. 이름 있는 함수가 없으면 「N번 줄을 감싸는 이름 있는 함수가 없습니다. --lines A-B로 범위를 주십시오」와 함께 종료 3입니다(수백 줄짜리 `ready` 함수 전체를 대상으로 잡지 않습니다). `--lines A-B`는 줄 범위 그대로이고, 처음·끝이 문장 중간을 자르면 「줄 A-B 범위가 문장 중간을 자릅니다. 문장 경계로 범위를 주십시오」와 함께 종료 3입니다. 셋 다 없으면 이름 있는 함수가 하나일 때 그것, 여럿이면 선택 화면입니다.
* **`pelly css` 대상:** `--selector "<선택자>"`는 선택자 목록이 같은 규칙입니다(공백·쉼표 뒤 공백을 정규화, 대소문자 그대로, `a, b { }`는 `a`만 줘도 맞음, `@media` 안도 찾음). 여럿이면(예: 같은 선택자가 `@media print` 안에도 있음) 줄 번호·감싼 `@media`와 함께 선택 화면(비대화형이면 종료 3)입니다. `--line N`은 N번 줄을 감싸는 가장 안쪽 규칙(`@media` 같은 묶음 규칙 자체일 수 있음)입니다. `--lines A-B`는 줄 범위이며, 한 규칙 안의 선언 몇 줄만 주는 것은 되지만 **규칙 중간을 자르면** 「줄 A-B 범위가 규칙 중간을 자릅니다」와 함께 종료 3입니다. 셋 중 하나는 꼭 줘야 합니다.
* **받지 않는 파일(종료 3):** 확장자가 `.js`·`.css`(또는 페이지 확장자)가 아님(`WebUnsupportedFile`), 압축 파일 — 이름이 `*.min.js`·`*.min.css`이거나 평균 줄 길이가 500자를 넘거나 1,000자 넘는 줄이 절반 이상(`WebMinifiedFile`), 라이브러리 파일 — 사이트 루트 기준 경로가 `web.vendorPaths`에 맞거나 첫 주석에 `jQuery JavaScript Library`·`@license`·`(c) … license` 같은 배포본 머리가 있음(`WebVendorFile`). 경로 때문에 거절되면 「`web.vendorPaths`에서 빼면 고칠 수 있습니다」가, 머리 주석 때문이면 설정과 무관하게 고치지 않는다는 안내가 나옵니다.
* **판정(앞이 실패하면 뒤는 하지 않음, 모두 원본 기준선 대비 새로 생긴 것만 셈 — 위치가 아니라 내용의 개수):**

  | 순서 | 검사 (검토 줄 이름) | `pelly js` | `pelly css` | 실패하면 |
  | :-: | :--- | :--- | :--- | :--- |
  | 1 | 패치 적용 | 편집 영역 밖 SEARCH, 일치 실패, 변경 없음 | 같음 | 재시도 |
  | 2 | 인코딩 | 새 텍스트를 파일 인코딩으로 쓸 수 있는가 | 같음 | 재시도, 끝내 `WebEncoding` |
  | 3 | 문법 | 고친 **파일 전체**를 목표 수준으로 해석(오류 줄·열과 해석기 메시지) | 구조: `{}`·`()`·`[]` 짝, 닫히지 않은 문자열·주석, 선언 끝 `;` 빠짐, 규칙 밖 선언 | 재시도, 끝내 `JsSyntaxErrors`·`CssStructureErrors` |
  | 4 | 문법 수준 | 목표 수준보다 높은 문법(아래) | — | 재시도, 끝내 `JsLevelExceeded` |
  | 5 | 대상 모양 | `--function` 대상이면 같은 이름의 함수 개수가 원본과 같고, 고른 함수(같은 이름 가운데 같은 자리)의 이름·매개변수가 같음(요청과 상관없이). `--signature`나 명세의 `js` 작업이면 이름·매개변수가 바뀌어도 되고(검토에 「함수 서명 변경: … → …」, `summary.json`의 `web.signatureChange`), 함수 밖 새 최상위 선언만 거절 | `--selector` 대상이면 같은 선택자(같은 `@media` 안) 규칙 개수가 원본과 같음 | 재시도, 끝내 `JsTargetChanged`·`CssTargetChanged` |
  | 6 | 안전 | `JsEval`(`eval`), `JsFunctionConstructor`(`new Function`·`Function(…)`), `JsStringTimer`(첫 인수가 문자열·문자열 연결·템플릿 문자열인 `setTimeout`·`setInterval`), `JsDocumentWrite`, `JsExecScript`, `JsActiveX`. 규칙마다 호출 개수가 늘 때만 셈(기존 호출의 인수만 고치면 통과) | `CssExpression`(`expression(…)`), `CssBehavior`(`behavior:`), `CssMozBinding`, `CssJavascriptUrl`(`javascript:` URL) | 재시도, 끝내 종료 5 `JsSafetyRejected`·`CssSafetyRejected` |
  | 7 | 규칙 | 프로젝트 규칙의 금지 패턴(`pelly rules`) | 같음 | 재시도 |
  | 8 | 이름 (**경고만**) | 새로 쓴 자유 이름이 JS 내장·브라우저 전역, `$`·`jQuery`(jQuery를 찾았을 때), 파일 안 선언, 사이트 색인, `web.globals` 어디에도 없음 | — | 막지 않음, 검토 줄에 「이름 경고(…)」 |
  | 9 | API (**경고만**) | 목표 수준에 없는 ES API(`.includes`·`Promise`·`fetch`·`Object.assign` 등), jQuery 기준 버전에 없거나 없어진 메서드(`.on`은 1.7 버전부터, `.live`·`$.browser`는 1.9 버전에서 없어짐 등) | — | 막지 않음 |
  | 10 | 속성·덮어쓰기 (**경고만**) | — | 알려진 표에 없는 속성 이름(원본이 이미 쓰는 벤더 접두사·`*속성`·`_속성`·`filter` 모양은 제외), 새 선언이 같은 파일 뒤쪽의 같은 선택자·같은 `@media` 규칙에 덮임 | 막지 않음 |
  | 11 | 요소 (**경고만**, 페이지 대상만) | 페이지 `<script>`가 `getElementById`·`getElementsByName`·`$("#id")`·`$("[name=x]")`로 찾는 id·name이 페이지·include·이 파일을 include하는 페이지·`<script src>` 파일에 없음. 편집 때문에 새로 끊긴 것만 세고, name만 있거나 대소문자만 다르면 힌트(「id 없음, name="custId"는 있음(줄 98)」). 따로 있는 `.js`는 「건너뜀(페이지가 아님)」 | — | 막지 않음 |

  이름·API·속성은 다른 페이지의 `<script>`나 다른 개체의 같은 이름 메서드일 수 있어 「없다」를 확신할 수 없으므로 막지 않습니다. 원본이 해석되지 않는 JS(깨진 파일, IE 전용 구문)는 멈추지 않고 문법 검사를 괄호·문자열·주석 짝 비교로 낮추며, 문법·문법 수준·이름·API는 「건너뜀(원본이 해석되지 않음: 줄 N)」으로 보입니다. `*zoom: 1`, `_height: 1%`, `filter: progid:DXImageTransform…`, `color: red\9` 같은 CSS의 IE 핵은 구조 문제로 보지 않습니다.
* **문법 수준과 `web.jsTarget`:** 원본의 수준은 원본이 해석되는 가장 낮은 판입니다(`ES5`, `ES2015`, …, `ES2026` 순서. ES3 문법은 ES5 수준에 묶음). 고친 파일은 **목표 수준**으로 해석되어야 합니다. `web.jsTarget`이 `auto`(기본)면 목표 수준은 원본 수준이라, ES5 파일에 `let`·`const`·화살표 함수·템플릿 문자열·`class`가 들어가면 거절하고 재시도합니다(모델에게 주는 피드백은 영어 기능 이름 「arrow functions」 등을 씁니다). 배열·인수 펼치기(`...`)는 ES2015, 객체 펼치기·나머지(`{...a}`)는 ES2018 문법으로 셉니다. `web.jsTarget`을 `es5`·`es2015`~`es2026`·`latest`(대소문자 무시)로 고정하면 목표 수준은 그 값과 원본 수준 중 높은 쪽입니다. 원본이 설정보다 높으면 원본을 막지 않고 「원본이 ES2015 문법을 써서 web.jsTarget(ES5) 대신 원본 수준(ES2015)을 기준으로 봅니다」 알림을 보입니다. 대부분 ES5인데 `let` 하나가 있는 파일은 원본 수준이 ES2015라 화살표 함수를 막지 않습니다.
* **jQuery 버전:** 사이트 루트 아래에서 jQuery 본체를 찾아 머리 주석(`jQuery JavaScript Library v1.8.3`·`jQuery v1.8.3`)이나 파일 이름(`jquery-1.8.3(.min).js`)에서 버전을 읽습니다. 여럿이면 가장 낮은 버전을 기준으로 하고 실행 메시지에 목록을 보입니다. `web.jqueryVersion`으로 직접 줄 수 있습니다. 기준 버전은 모델 입력에 들어가고 API 경고에 쓰입니다. 찾지 못하면 jQuery API 경고는 하지 않습니다.
* **「브라우저 실행 검증 없음」:** 어떤 검사도 JS·CSS를 브라우저에서 실행하지 않습니다. 검토 화면에는 diff와 함께 판정 줄(예: `검사: 인코딩 통과, 문법 통과, 문법 수준 통과, 대상 모양 통과, 안전 통과, 이름 통과, API 통과`)과 고정 문구 「JS·CSS는 브라우저에서 실행해 확인하지 않았습니다. 화면에서 직접 확인하십시오. 브라우저 캐시 때문에 바뀐 파일이 바로 반영되지 않을 수 있습니다.」가 나옵니다. `--yes`여도 이 줄들은 화면과 실행 기록에 남습니다. 승인한 뒤 **화면을 직접 띄워(필요하면 강력 새로 고침) 확인하십시오.**
* **인코딩과 줄 끝:** 편집 영역 밖은 원본 **바이트 그대로**입니다(줄 끝이 섞인 파일, 끝 줄바꿈 유무 포함). 인코딩은 BOM → (CSS) 파일 맨 앞 `@charset "…";`(`utf-8`, 또는 `euc-kr`·`ks_c_5601-1987`·`cp949`·`windows-949`. 그 밖은 종료 4) → ASCII 아닌 바이트가 엄격한 UTF-8이면 UTF-8, 아니면 CP949 → 순수 ASCII면 `asp.encoding`(기본 `cp949`) 순서로 정합니다. JS 파일은 그 파일을 부르는 페이지의 인코딩으로 읽히는데 파일 하나로는 그 페이지를 알 수 없어서, 순수 ASCII 파일에 한글이 들어가면 검토에 「이 파일은 ASCII였고 CP949로 저장했습니다」(또는 UTF-8) 알림을 보입니다. UTF-16 파일은 지원하지 않습니다(종료 4).
* **설정(`web.*`, 작업 공간 `pelly.json`) — ASP 설정도 함께 씀:** JS·CSS 전용 사이트 루트·인코딩 설정은 없고 **`asp.siteRoot`·`asp.encoding`을 그대로 읽습니다**(사이트 색인·jQuery 찾기·`web.vendorPaths`의 기준 폴더, 순수 ASCII 파일의 인코딩).

  | 키 | 기본 | 뜻 |
  | :--- | :--- | :--- |
  | `web.jsTarget` | `auto` | JS 목표 문법 수준: `auto`(원본 수준), `es5`, `es2015`~`es2026`, `latest` |
  | `web.jqueryVersion` | 없음(찾은 것 중 가장 낮은 버전) | jQuery 기준 버전, 예: `"1.8.3"` |
  | `web.globals` | `[]` | 현장 공용 전역 이름(페이지 안 `<script>`나 ASP가 출력하는 함수 등). 이름 경고에서 뺌 |
  | `web.vendorPaths` | `["**/lib/**", "**/plugin*/**", "**/vendor/**", "**/jquery*.js", "**/jquery*.css", "**/bootstrap*"]` | 고치지 않는 라이브러리 경로 패턴(사이트 루트 기준, `/` 구분, 대소문자 무시). 값을 주면 기본값을 대신함 |
  | `asp.siteRoot` | 작업 공간 루트 | (ASP와 공용) 사이트 루트 |
  | `asp.encoding` | `cp949` | (ASP와 공용) 순수 ASCII 파일을 쓸 인코딩 |

  ```json
  { "asp": { "siteRoot": ".", "encoding": "cp949" },
    "web": { "jsTarget": "auto", "globals": ["gfnOpenPopup"] } }
  ```
  사이트 색인(사이트 루트 아래 라이브러리가 아닌 `.js` 파일들의 최상위 함수·`var` 이름)은 `.pelly/cache/web-site-index.json`에 파일 해시와 함께 저장하고 바뀐 파일만 다시 읽습니다.
* **종료 코드:** 0 적용(또는 `--dry-run`), 1 재시도 소진, 2 거부·중단, 3 대상 못 찾음·모호·받지 않는 파일, 4 환경(인코딩 판정 실패, 모델·설정, 잘못된 인자), 5 안전 거부.
* **기록:** `summary.json`의 `web` 블록(`unit`: `function`·`rule`·`lines`, `encoding`, `originalLevel`·`targetLevel`(CSS는 `null`), `jquery`, `jquerySource`: `setting`·`page`·`site`, `container`: `file`(`.js`·`.css`) 또는 `page`, `block`: 페이지일 때만 `{ "kind": "script"|"style", "startLine", "endLine", "serverCode": "none"|"output"|"statements" }`, `checks`: 검사별 `passed`·`warned`·`skipped`·`failed`(키는 ASP처럼 `Syntax`·`Level`·`Shape`·`Safety`·`Names`·`Api`·`Elements`·`Fields`·`Properties`·`Override`·`Encoding`, CSS 구조도 `Syntax`), `levelRejections`, `nameWarnings`·`apiWarnings`·`propertyWarnings`)과 실패 유형 `JsSyntaxErrors`·`JsLevelExceeded`·`JsSafetyRejected`·`JsTargetChanged`·`CssStructureErrors`·`CssSafetyRejected`·`CssTargetChanged`·`WebVendorFile`·`WebMinifiedFile`·`WebUnsupportedFile`·`WebEncoding`, 페이지 안 `WebBlockChanged`·`WebServerCodeChanged`·`WebOutsideBlock`. `pelly asp`의 `asp` 블록 `checks`에는 `JsSyntax`·`JsLevel`·`JsSafety`·`CssSyntax`·`CssSafety`·`JsNames`·`JsApi`·`JsElements`·`JsSignature`·`FormFields`가 생길 수 있습니다. `pelly js`는 고친 `.js`나 페이지에 ajax·폼 action 대입·폼이 있으면 경고 행 `Fields`(「폼 필드」)를 더합니다(W3b). `pelly report`에 「JS: 실행 N건, 성공 K건, 문법 수준 거절 M건, 실패 유형 …」·「CSS: 실행 N건, 성공 K건, 실패 유형 …」 줄이 나오고, 페이지 안 실행이 있으면 줄 끝에 「, 페이지 안 K건」이 붙습니다. `pelly doctor`의 「JS·CSS」 행은 설정에 `web` 또는 `asp` 절이 있을 때 찾은 jQuery 버전 목록·기준 버전·`web.jsTarget`을 보입니다(못 찾으면 Warn).
* **하지 않는 것:** 브라우저 실행·화면 비교, Node·npm·ESLint 같은 외부 도구, 압축·라이브러리 파일, 인라인 이벤트·`style=` 속성, 블록 안 서버 코드 바꾸기, TypeScript·JSX·SCSS·LESS, 서버 JScript, 들여쓰기·따옴표 정리, XSS 검사, 함수 이름·매개변수나 선택자 바꾸기와 부르는 곳 함께 고치기, 명세(`spec`·`apply`)의 `js`·`css` 작업(3단계 W3), 여러 파일 동시 수정·새 파일, `pelly ui js`·`pelly ui css`.
* **알려진 한계:** 실행하지 않으므로 화면 동작·런타임 오류·브라우저별 차이는 확인하지 못합니다. 이름·API·속성은 경고만 합니다. 현장 확인은 아직입니다(`deploy/field-results.md` 17절). 페이지 안 블록(W2)은 Windows 실행과 현장 확인이 모두 미확인입니다. `<% 문장 %>`이 섞인 블록은 짝 비교로 낮아져 문법·문법 수준을 해석기로 보지 못합니다. 실측은 [예비 측정](docs/web-premeasure.md)(Mac)을 보십시오.

### `pelly rules` — 프로젝트 규칙 (AGENTS.md)
```bash
pelly rules init  [--dry-run] [--force] [--yes] [--pause-on-exit]
pelly rules check [--config <경로>] [--pause-on-exit]
```

작업 공간 루트의 `AGENTS.md`에 `## Pelly rules` 절을 두면, 지금 작업과 관련된 짧은 규칙만 골라 프롬프트에 넣고 `forbid` 규칙은 코드로 강제합니다. 작업 공간 루트의 파일만 읽고 **하위 폴더의 `AGENTS.md`는 무시합니다**. 파일이나 절이 없으면 규칙 기능은 꺼진 것과 같습니다.

```markdown
## Pelly rules
- [*] 새 주석은 꼭 필요할 때만 달고, 쓸 때는 한국어로 작성한다
- [dapper,sql] SQL은 Dapper 파라미터로만 전달한다
- [async] .Result나 .Wait()로 기다리지 않는다 | forbid: \.Result\b|\.Wait\(\)
```

* **문법:** `- [<태그>[,<태그>…]] <규칙>[ | forbid: <정규식>]`. 태그는 `*` 하나이거나 `[A-Za-z0-9_.]`로 된 쉼표 목록입니다. 규칙 문장은 100자 이내(`forbid` 부분 제외), 절 하나에 최대 50개, 같은 문장 중복 불가입니다. `AGENTS.md`는 256 KiB까지 읽습니다.
* **절의 범위:** `## Pelly rules` 줄(대소문자 무시)부터 다음 `#`·`##` 머리글 줄 또는 파일 끝까지입니다(`###` 이하는 절 안). 절 안에서 `- `로 시작하는 줄은 규칙 문법을 따라야 하고, `* `로 시작하는 줄은 오류입니다. 설명 문장·`<!-- -->` 주석·빈 줄은 무시됩니다.
* **선택:** `[*]` 규칙은 항상 넣고, 나머지는 태그 중 하나가 이번 SLICE에 들어간 코드 텍스트에 (대소문자 무시) 부분 문자열로 나오면 넣습니다. 파일 순서를 지키고, `budget.rules`(기본 150 토큰)를 넘으면 **아래쪽 규칙부터** 뺍니다. 규칙은 프롬프트의 CONTEXT 뒤, REQUEST 앞(`### RULES`)에 들어갑니다. 규칙 예산은 시스템 예산에서 나눠 `budget.system` 기본값이 600에서 450이 되었습니다. 예전 `pelly.json`에 `budget.system`을 600으로 적어 두었다면 합계가 `budget.inputTotal`을 넘어 종료 코드 4가 되니 450으로 낮추십시오.
* **`forbid` 강제:** 프롬프트에 들어간 규칙뿐 아니라 **절의 모든 `forbid` 규칙**을 검사합니다. 검사 대상은 **원본 파일 대비 새로 추가된 줄의 코드 부분**이고 재시도가 쌓이므로, 앞선 시도에서 남은 위반도 셉니다. 주석과 문자열 리터럴은 검사하지 않습니다(C#은 Roslyn 토큰으로 `//`·`/* */`·`///` 주석, 비활성 `#if` 영역, 문자열·문자 리터럴을 빼며 보간 문자열의 `{식}`은 코드로 검사합니다. SQL은 ScriptDom 토큰으로 `--`·`/* */` 주석과 `'…'`·`N'…'` 문자열을 빼고, `QUOTED_IDENTIFIER OFF`이면 `"…"`도 문자열로 봅니다). 그래서 문자열 안의 SQL 같은 내용은 `forbid`로 막을 수 없습니다. SQL을 토큰으로 나누지 못하면 원문 전체로 검사합니다. 위반하면 `RULE VIOLATION:` 피드백과 함께 재시도하고, 재시도를 소진하면 종료 코드 1(`RulesErrors`)입니다. `--dry-run`은 검증(VERIFY)을 하지 않으므로 forbid 검사도 하지 않습니다. 정규식이 100 ms 안에 끝나지 않으면 검사하지 못한 것이므로 통과시키지 않고 종료 코드 4입니다.
* **규칙 파일 오류:** 절이 문법에 맞지 않거나 규칙에 모델 제어 토큰 문자열이 있으면 서버를 부르기 전에 종료 코드 4(`RulesInvalid`)로 멈춥니다. `pelly rules check`로 원인을 봅니다.
* **`pelly rules init`:** 모델을 부르지 않고 코드베이스의 `.cs`·`.sql`을 분석해 규칙 초안을 만듭니다(결정론적). 감지 항목 9개: async 메서드의 `Async` 접미사, `.Result`/`.Wait()` 미사용, `ConfigureAwait(false)`, Dapper 사용, 한국어 주석, 예외 메시지 언어, `var` 사용 습관, 프로시저의 `SET NOCOUNT ON`, FROM/JOIN 객체의 스키마 이름. 규칙마다 `<!-- 근거: … -->` 줄과 함께 초안을 보여 주고 승인을 받은 뒤에 씁니다. **초안은 반드시 사람이 검토하십시오.** 이미 `Pelly rules` 절이 있으면 `--force` 없이는 종료 코드 4이고, `--force`는 그 절만 바꾸며 파일의 인코딩·BOM·줄바꿈 방식을 유지합니다. `--dry-run`은 초안만 보여 줍니다. 승인을 거부하면 종료 코드 2, 비대화형인데 `--yes`가 없으면 종료 코드 4입니다.
* **`pelly rules check`:** 규칙을 줄·태그·규칙·토큰 수·forbid 표로 보여 줍니다. `[*]` 규칙만으로 `budget.rules`를 넘으면 아래쪽 규칙이 빠진다는 경고를 냅니다. 오류가 있으면 종료 코드 4, 규칙이 없으면 알림과 함께 종료 코드 0입니다.
* **`--verbose`:** 실행 요약에 포함된 규칙(`+`)과 제외된 규칙(`-`)을 보여 주고 `summary.json`에 `rulesIncluded`·`rulesDropped`를 남깁니다. `pelly report --sanitize`의 반출 보고서에는 담기지 않습니다.

### `pelly ui` — 승인 창 (V2-0)
```bash
pelly ui cs  <file.cs>  ["<요청>"] [--class <Name>] [--method <Name>] | [--line <n>]  [cs 옵션]
pelly ui sql <file.sql> ["<요청>"] [--object <Name>] | [--line <n>]  [공통 옵션]
```

`pelly cs`·`pelly sql`과 같은 실행을 작은 창에서 합니다. 요청 입력, 진행 표시, diff 확인, 승인·거부를 창에서 하므로 표준 입력이 필요 없습니다. External Tools의 콘솔이 키 입력을 받지 못하는 IDE(Rider 등)와 `--pause-on-exit`가 필요했던 곳에 씁니다. FSM·검증·롤백 코드는 `cs`·`sql`과 같습니다. `pelly ui`만 쓰면 사용법을 출력합니다.

* **흐름:** 창이 뜨면 대상(파일, 줄 또는 멤버)과 요청 칸이 보입니다. 인수에 요청문을 주었으면 미리 채워 둡니다. 「실행」(Ctrl+Enter)을 눌러야 시작합니다. 진행 줄이 쌓이고, 대상이 여럿이면 목록에서 고르고, 경고는 「계속」/「중단」으로 묻습니다. 검증을 통과하면 diff가 보이고 「적용」/「거부」를 누릅니다. 이때 파일은 이미 바뀐 상태이며 거부하면 원래대로 되돌립니다.
* **끝난 뒤:** 창은 남고 결과(종료 코드와 뜻)를 보여 줍니다. 요청문을 고쳐 다시 실행할 수 있습니다. 프로세스 종료 코드는 **마지막 실행의 종료 코드**이고, 한 번도 실행하지 않고 닫으면 2입니다.
* **취소·닫기:** 실행 중 「취소」나 창 닫기는 Ctrl+C와 같습니다. 원본 복원이 끝날 때까지 창이 「되돌리는 중…」으로 남고 그 뒤 종료 코드 2입니다. 질문을 기다리는 중에 창을 닫아도 답이 아니라 취소(Ctrl+C와 같음)입니다. Esc는 승인·경고 질문이면 거부(또는 중단), 목록 선택이면 취소이고, 질문이 없으면 창 닫기와 같습니다(실행 중이면 취소). 새 질문이 뜬 뒤 0.5초 동안은 Esc도 무시합니다.
* **옵션:** `cs`·`sql`과 같은 뜻입니다. `--yes`와 `--pause-on-exit`는 쓸 수 없고 주면 종료 4입니다(승인은 창에서 하고 창이 남기 때문). `--dry-run`은 diff만 보이고 「확인」 하나만 둡니다(창이 닫힘). `--verbose`는 끝난 뒤 접힌 「실행 요약」을 보입니다. 창에서 옵션을 고르는 기능은 없습니다.
* **오류:** 파일 없음·옵션 충돌·설정 오류 같은 명령 검증 오류는 창 안에 빨갛게 보이고 「실행」이 잠기며 종료 4입니다(출력 창 사용 시 콘솔이 안 보이기 때문). 모르는 옵션·형식 오류(`--line abc`)·값 빠진 옵션은 창 없이 콘솔에 출력하고 종료 4입니다.
* **실수 방지:** 「적용」은 기본 단추가 아니며 다른 질문 단추와 떨어져 있습니다. 새 질문이 뜬 뒤 0.5초 동안은 단추 입력을 무시합니다(더블클릭 방지).
* **반입 배치:** 창을 그리는 네이티브 DLL 셋(`av_libglesv2.dll`, `libHarfBuzzSharp.dll`, `libSkiaSharp.dll`)은 `pelly.exe`에 묶지 않고 **`pelly.exe` 옆에 둡니다.** 없으면 `pelly ui`만 「GUI 구성 요소를 불러오지 못했습니다: <파일>. pelly.exe 옆의 DLL을 함께 두십시오.」를 내고 종료 4이고, 다른 명령은 영향이 없습니다. `pelly.exe`는 +9.3 MB(127,059,367 → 136,359,551 바이트, macOS 교차 publish 측정이며 Windows 값은 CI가 출력합니다)이고 DLL 셋은 합쳐 약 18.8 MB입니다. Windows에서 창이 뜨는지, 한글 IME 입력, 고해상도 표시는 **Windows 미확인**입니다(현장 확인표는 `deploy/field-results.md`). CI는 창을 화면에 띄우지 않고 한 번 그리는 숨은 `pelly ui --selftest`로 DLL 적재만 확인합니다.
* IDE 등록은 [deploy/ide-external-tools.md](deploy/ide-external-tools.md)의 「GUI 항목」을 보십시오. 같은 문서의 설계 배경은 `docs/superpowers/specs/2026-10-26-v2-0-gui-design.md`입니다.
* **개발 메모:** Avalonia 12.1.4. `Avalonia.Headless.XUnit`이 xunit.v3 4.0.2와 맞지 않아 UI 테스트는 `HeadlessUnitTestSession`으로 직접 세션을 띄웁니다. 빌드 시점 사용 정보 수집은 `AVALONIA_TELEMETRY_OPTOUT=1`로 끕니다.

### `pelly completion powershell` — PowerShell 자동완성
```powershell
pelly completion powershell > "$HOME\pelly-completion.ps1"
if (-not (Test-Path $PROFILE)) { New-Item -ItemType File -Path $PROFILE -Force | Out-Null }
Add-Content $PROFILE '. "$HOME\pelly-completion.ps1"'
```

PowerShell 5.1·7에서 Tab으로 명령·옵션 이름과 정해진 값(`--think`)을 완성합니다. 파일 자리는 기본 경로 완성 그대로입니다. 명령 목록(`CliApp.CommandSettings`)과 설정 클래스의 옵션 속성에서 스크립트를 만들므로 명령을 더하면 완성도 따라옵니다. 스크립트는 ASCII만 쓰고 Tab마다 `pelly.exe`를 실행하지 않습니다. `pelly`를 바꾸면 다시 만드십시오. 실제 Tab 결과는 Windows CI의 `PowerShellCompletionTests`가 `TabExpansion2`로 확인합니다. 설계는 `docs/superpowers/specs/2026-10-27-powershell-completion-design.md`입니다.

### `pelly doctor` — 환경 점검
```bash
pelly doctor [--project <csproj>] [--sanitize [--out <폴더>]] [--config <경로>] [--model <태그>] [--endpoint <URL>]
```
Ollama 서버 접속, 모델 존재·로드 여부, chat template, 토크나이저, 응답 시간, pelly 자체와 설정, .NET SDK(`dotnet --version`), 레거시 프로젝트용 MSBuild(Windows에서만), 타겟팅 팩, 의미 분석(영향 탐색용 보조 프로세스 폴더 `BuildHost-*` 유무, `--project`를 주면 불러오기까지), SQL 연결(허용 목록·`VIEW DEFINITION`, 연결 문자열이 설정된 경우), 서버 관리자 확인 체크리스트를 표로 보입니다. 하나라도 Fail이면 종료 코드 4입니다.
* 설정에 `asp` 절이 있으면 「ASP 구문 검사(cscript)」(Windows가 아니거나 `cscript`가 안 되면 Warn, 컴파일 검사는 건너뜀), 「ASP 사이트 루트」(`asp.siteRoot` 폴더가 없으면 Fail, 상대 경로는 `pelly asp`와 같이 작업 공간 루트 기준), 「ASP SQL 처리 함수」(`asp.sqlSanitizers`가 비면 Warn) 행이 더해집니다.
* 설정에 `web` 또는 `asp` 절이 있으면 「JS·CSS」 행이 더해집니다. 사이트 루트(`asp.siteRoot`) 아래에서 찾은 jQuery 버전 목록과 기준 버전, `web.jsTarget`을 보이고, jQuery를 못 찾으면 Warn입니다(`pelly js`의 jQuery API 경고는 건너뜀). 같은 조건으로 「사이트 색인」 행도 더해집니다: 색인한 페이지·`.js` 수와 크기 합계, 1MB가 넘어 뺀 파일 수(예: 「페이지 412개, .js 87개, 합계 23.4MB」). 종류마다 2000개 한도에 걸려 색인이 잘리면 Warn입니다(영향 탐색과 JS 서명·폼 필드 경고가 일부 파일을 보지 못함). 이 행은 작업 공간의 `.pelly/cache/web-site-index.json`을 만들거나 갱신합니다.
* `--project <csproj>`: 그 프로젝트의 .NET Framework 타겟팅 팩이 설치됐는지 확인합니다(Windows에서만, 실기 확인 전). 의미 분석 행도 이 프로젝트를 불러와 봅니다. 불러오지 못하면 Warn이고(종료 코드는 그대로) 영향 탐색은 이름 기준으로 대신합니다. 보조 프로세스 폴더가 없으면 Fail입니다.
* `--sanitize`: 화면 대신 비식별 결과 `json`·`md`를 현재 폴더(또는 `--out`)에 씁니다. 행 이름·상태·버전 같은 짧은 값만 남고 경로·연결 문자열·설명 문장은 나가지 않습니다.

### `pelly report` — 실행 기록 요약
```bash
pelly report [--since 2026-10-01] [--sanitize [--out <폴더>]]
```
현재 작업 공간의 `.pelly/runs/*/summary.json`을 모아 TTFT·대기·패치 포맷 파싱 성공률·최종 성공률·완료 시간 표와 종료 코드·실패 유형·시도 수 분포를 보입니다. `pelly asp` 실행이 있으면 「ASP: 실행 N건, 성공 K건, 컴파일 검사 건너뜀 M건, 실패 유형 …」 줄이 더해집니다. `pelly js`·`pelly css` 실행이 있으면 「JS: 실행 N건, 성공 K건, 문법 수준 거절 M건, 실패 유형 …」·「CSS: 실행 N건, 성공 K건, 실패 유형 …」 줄이 더해지고, 페이지 안 실행이 있으면 줄 끝에 「, 페이지 안 K건」이 붙습니다.
* `--sanitize`: 폐쇄망 밖으로 보낼 수 있는 `pelly-report-<시각>.json`·`.md`를 씁니다(`--out`으로 폴더 지정, 기본은 현재 폴더). **허용 목록 방식**이라 소스·경로·식별자·모델 이름·RunId는 나가지 않고 숫자 지표, 오류 코드(`CS0103`·`Msg208`·`TEST` 등), 실패 유형, 버전만 담깁니다. 반출 가능 여부는 사내 보안 규정을 따르십시오.
`pelly apply`가 남긴 `.pelly/runs/apply-*/apply.json`으로 「명세」 표도 보입니다. 명세 완료율은 `--resume`이 아닌 apply 중 종료 코드 0으로 보관(archive)까지 끝낸 비율이고(이어서 하기는 분모에서 빼고 건수만 따로 보입니다), 작업 3~8개 명세만 따로도 셉니다. 그룹 빌드 통과율은 그룹 빌드까지 간 그룹 중 통과한 비율이며 빌드 검증 불가 그룹은 따로 세고 분모에서 뺍니다. 작업 실행 기록이 없어도 apply 기록만으로 나오고, `--since`는 둘 모두에 적용되며, `--sanitize` 보고서에는 이 비율의 숫자 여섯만 더해집니다.

### 설정 파일
설정은 아래 순서로 합쳐지며 뒤의 것이 앞의 것을 덮어씁니다.

1. 내장 기본값
2. 사용자 설정: `pelly/config.json` (macOS·Linux `~/.config/pelly/config.json` 또는 `~/Library/Application Support/pelly/config.json`, Windows `%APPDATA%\pelly\config.json`)
3. 작업 공간 설정: `<작업 공간 루트>/pelly.json` (`--config`로 경로 지정 가능). 작업 공간 루트는 대상 파일에서 위로 올라가며 처음 만나는 `.git`, `.svn` 또는 `*.sln`이 있는 폴더입니다.
4. 환경 변수 `PELLY__<섹션>__<키>` (예: `PELLY__LLM__MODEL`)
5. 명령줄 옵션 (`--model`, `--endpoint`, `--think`, `--strict-baseline`, `--warn-as-error`)

키 전체는 [v1 설계 §8.1](docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md)을 보십시오.

### 종료 코드
| 코드 | 의미 |
| :-: | :--- |
| 0 | 성공 |
| 1 | 검증 실패 (재시도를 소진해도 구문 검사를 통과하지 못함) |
| 2 | 사용자 거부 또는 중단 (Ctrl+C·SIGTERM·SIGHUP·콘솔 창 닫기 포함, 원본 복원) |
| 3 | 대상 찾기 실패 |
| 4 | 환경 오류 (서버·모델·설정·잘못된 인자·외부 편집 충돌) |
| 5 | 안전 거부 |

* 중단 신호(SIGTERM·SIGHUP)를 받으면 실행을 취소하고 원본 복원이 끝날 때까지 최대 4.5초 기다린 뒤 종료합니다. Windows에서 콘솔 창을 닫는 경우(CTRL_CLOSE, 약 5초 뒤 강제 종료)도 같은 경로를 타도록 했지만 **Windows 미확인**입니다(실제 Windows에서 확인하지 않았습니다).

실제 모델 실행 기록은 [docs/m1-e2e.md](docs/m1-e2e.md)(C# 최소 경로), [docs/m2a-e2e.md](docs/m2a-e2e.md)(빌드 검증·교정 루프), [docs/m2b-e2e.md](docs/m2b-e2e.md)(SQL 경로), [docs/m3a-e2e.md](docs/m3a-e2e.md)(DTO 슬라이스·`--test`·리포트)에 있습니다.

## 반입 패키지 만들기
폐쇄망에 들고 갈 zip은 둘이고 CI의 `package` 잡이 만듭니다(Windows, PowerShell 7). `pelly-0.9.1-win-x64.zip`은 `pelly.exe`와 그 옆 BuildHost 폴더 둘(`BuildHost-net472/`, `BuildHost-netcore/`, 의미 분석 보조 프로세스)·GUI 네이티브 DLL 셋(`av_libglesv2.dll`, `libHarfBuzzSharp.dll`, `libSkiaSharp.dll`, `pelly ui`용)·설치 문서, `pelly-field-kit-0.9.1-win-x64.zip`(현장 키트)은 `pelly-eval.exe`와 같은 BuildHost 폴더 둘·현장 절차 문서·스위트 템플릿입니다. 로컬(Windows)에서 반입 zip을 만들려면 단일 파일 publish 뒤 스크립트를 실행합니다.

```powershell
dotnet publish src/PellyCoder.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=none -o artifacts/publish/win-x64
dotnet tool restore   # CycloneDX 6.3.1 (SBOM)
pwsh ./deploy/build-package.ps1 -PublishDir artifacts/publish/win-x64 -OutDir artifacts/package
```
* zip에는 `deploy/package-files.txt` 허용 목록에 있는 것만 들어갑니다(실행 파일·`pelly.exe` 옆 Avalonia 네이티브 DLL 셋·publish 폴더의 BuildHost 폴더 둘 전체·`LICENSE`(Payletter 사내 전용)·설정 템플릿·문서·`SHA256SUMS.txt`·SBOM·서드파티 라이선스·버전 기록·벤치마크 요약). 허용되지 않은 라이선스가 있으면 스크립트가 실패하며, 예외는 `deploy/license-exceptions.json`에 승인자와 함께 적습니다(현재 둘: SNI와 Avalonia ANGLE 네이티브, 둘 다 사람이 승인. `"packages"`를 적은 예외는 그 zip에서만 적용됩니다).
* 현장 키트 zip은 `eval/PellyCoder.Eval`을 같은 옵션으로 publish한 뒤(그 폴더에 `pelly-eval.exe`와 BuildHost 폴더 둘이 나옵니다) `pwsh ./deploy/build-package.ps1 -Package field-kit -PublishDir <폴더> -OutDir artifacts/package`로 만듭니다. 허용 목록은 `deploy/field-kit-files.txt`이고 사용 순서는 [deploy/field-guide.md](deploy/field-guide.md)입니다.
* `deploy/version-record.json`은 개발 PC의 Ollama에서 `dotnet run --project eval/PellyCoder.Eval -- record --out deploy/version-record.json`으로 다시 만듭니다(현장 모델의 GGUF SHA-256은 `--gguf-sha256 <hex>`, 현장에서는 `pelly-eval record`). 반입 전에 사람이 다시 실행합니다. Apple 칩 Mac에서는 `ollama pull qwen3.6:35b`가 GGUF가 아닌 `nvfp4` 판을 받으므로, GGUF를 받는 방법은 [docs/m4-gguf-baseline.md](docs/m4-gguf-baseline.md) 1절을 따르십시오. 현장 모델은 `qwen3.6:35b-a3b-q4_K_M`입니다([docs/m4-field-model-baseline.md](docs/m4-field-model-baseline.md) 1절).
* 설치·사용·IDE·외부 통신 없음 문서는 [deploy/](deploy/)에 있고 zip의 `docs/`에도 들어갑니다. 현재 CI는 Windows에서만 이 zip을 검증하며, 실제 폐쇄망 PC는 **Windows 미확인**(M4)입니다.

## 벤치마크 실행
작업 50건(C# 30 = Legacy 18 + Modern 12, SQL 20)을 실제 모델로 돌려 KPI 표를 만드는 실행기 `eval/PellyCoder.Eval`이 있습니다. 작업 정의와 판정 규칙은 [eval/benchmark/README.md](eval/benchmark/README.md), 첫 측정은 [docs/m2b-benchmark.md](docs/m2b-benchmark.md), M3a 회귀 측정(30/30)은 [docs/m3a-benchmark.md](docs/m3a-benchmark.md), M3b 50건 기준선(평균 47.67건, `nvfp4` 파일)은 [docs/m3b-baseline.md](docs/m3b-baseline.md), 현장과 같은 모델 파일(GGUF `Q4_K_M`)의 기준선(평균 46.00건)은 [docs/m4-gguf-baseline.md](docs/m4-gguf-baseline.md)입니다.

```bash
# SQL 작업에는 SQL Server와 코퍼스 배포가 필요합니다 (eval/corpus/Sql/deploy.sh). 비밀번호가 있는 연결은 환경 변수로만.
export PELLY_SQL_CONNECTION="Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=<비밀번호>;TrustServerCertificate=True"
# 허용 목록·모델은 --config 파일에 둡니다: {"llm":{"model":"<태그>"},"sql":{"allowedHosts":["localhost"],"allowedDatabases":["PellyCorpus"]}}
dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json>             # 50건 전체
dotnet run --project eval/PellyCoder.Eval -- --tasks sql --config <pelly.json>  # 일부 (id 접두사·글롭, --kind cs|sql)
dotnet run --project eval/PellyCoder.Eval -- --suite <스위트 폴더> --config <pelly.json> --keep-work failed  # 현장 스위트 (suite.json이 있는 폴더)
```
* **스위트:** `--suite`가 없으면 내장 스위트(`eval/benchmark/suite.json`)를 씁니다. 현장 코드베이스는 같은 모양의 폴더를 만들어 지정하고(작업의 `file`은 `<루트 이름>/<경로>`), 형식은 [eval/benchmark/README.md](eval/benchmark/README.md)에 있습니다. `--keep-work <all|failed|none>`은 작업 복사본 보존 범위입니다. 내장 스위트 회귀는 [docs/m4-suite-regression.md](docs/m4-suite-regression.md)(48/50, 위반 0)입니다.
* 결과는 `eval/results/<yyyyMMdd-HHmmss>/`(`--out`으로 변경, git 추적 제외)에 `report.md`(KPI 표·실패 유형·작업별 결과), `results.json`, `work/<id>/`로 남습니다. 코퍼스(`eval/corpus`)는 읽기 전용이며 작업마다 임시 복사본에서 실행합니다.
* **SQL 작업은 허용 목록이 비어 있거나 연결이 없으면 건너뜁니다**(실행기가 허용 목록을 스스로 넓히지 않습니다).
* Legacy 작업은 Windows가 아니면 빌드 판정을 생략합니다. 검증 불변 조건(KPI)의 분모는 **독립 검증이 실제로 판정한 종료 0 실행**만이고, 생략된 실행은 따로 표시됩니다.
* 종료 코드: 0 실행 완료 · 1 검증 불변 조건 위반 · 2 중단 · 4 설정 오류 또는 LLM 서버 연결 불가.
* **검증 불변 조건은 독립 빌드(C#)·NOEXEC 컴파일(SQL)로만 판정**합니다. Modern 12건의 동작 판정(`judge.tests`·`judge.failingTests`) 실패는 모델 실패(`Judge`)로 따로 세고, 판정 도구 오류는 `JudgeTooling`(미판정)입니다.

### 하위 명령 (`validate`, `record`, `export`)
```bash
dotnet run --project eval/PellyCoder.Eval -- validate [--suite <폴더>] [--config <pelly.json>] [--deep]   # 실행 전 정의 점검 (LLM·SQL 접속 없음)
dotnet run --project eval/PellyCoder.Eval -- record --out <파일> [--gguf-sha256 <hex>] [--config <pelly.json>]  # Ollama 버전·모델 digest 기록
dotnet run --project eval/PellyCoder.Eval -- export <결과 폴더> --out <폴더>                               # 비식별 반출본
```
* `validate`는 종료 0 오류 없음 · 4 오류 있음. `--deep`은 판정 시험을 원본에서 실제로 돌립니다(`tests`는 실패, `failingTests`는 통과해야 함. 복원·빌드가 필요해 느림).
* `record`는 종료 0 기록함 · 3 모델이 서버에 없거나 버전·digest를 알리지 않음 · 4 접속 불가·인자 오류. 접속 주소는 쓰지 않습니다.
* `export`는 KPI 필드만 남기고 사유·경로·접속 주소를 지웁니다(종료 0 · 4). 이 하위 명령들은 `pelly-eval.exe`로 현장에서도 같습니다.

### 반복 실행과 비교 (`--repeat`, `compare`)
프롬프트·샘플링을 바꾼 뒤에는 같은 조건으로 3회 반복하고 기준선과 비교합니다(PRD §8.2).
```bash
dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json> --repeat 3
# 결과: <out>/run-1 … run-3, 상위 summary.md · summary.json
dotnet run --project eval/PellyCoder.Eval -- compare docs/m4-field-model-baseline.summary.json <후보 summary.json>
```
* `compare` 종료 코드: 0 채택 · 3 거절 · 4 판정 불가(중단된 반복, 실행 조건·작업 집합이 다른 요약, 모델 digest나 Ollama 버전이 다른 요약 등). 채택 규칙(R4): 평균 성공 +1.0건 이상, 어느 범주(cs-legacy·cs-modern·sql)도 −0.5건 넘게 줄지 않음, 3회 모두 불변 조건 위반 0건, 시간 중위값 1.5배 이하. M3b 후보 셋은 모두 거절됐습니다([docs/m3b-tuning.md](docs/m3b-tuning.md)). 기준선 `docs/m4-gguf-baseline.summary.json`은 현장과 같은 `qwen3.6:35b`(GGUF) 파일로 쟀습니다. 후보도 같은 파일·같은 Ollama 버전으로 재야 판정됩니다.
* SQL은 반복 사이에 다시 배포하지 않으므로, 의심되면 실행 전후 DB 객체를 비교하십시오(기준선은 차이 없음).

## 통합 테스트 환경
환경이 필요한 통합 테스트는 환경 변수가 있을 때만 실행됩니다.

| 분류 | 환경 변수 |
| :--- | :--- |
| SQL Server | `PELLY_TEST_SQL` (연결 문자열) |
| Ollama | `PELLY_TEST_OLLAMA` (엔드포인트), `PELLY_TEST_MODEL` (모델 태그) |
| Windows | Windows에서 자동 실행 |
