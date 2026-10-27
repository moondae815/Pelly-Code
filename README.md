# Pelly-Coder CLI

폐쇄망 로컬 LLM(`Qwen3.6-35B-A3B`, Ollama)으로 C# 메서드와 SQL 저장 프로시저를 수정하고, 컴파일러·DB 엔진으로 검증하는 코딩 CLI입니다.

## 문서
- [PRD](docs/PRD.md)
- [v1 설계](docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md)
- 현장 모델 파일 기준선(M4): [GGUF `Q4_K_M` 50건 × 3회](docs/m4-gguf-baseline.md)
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
* **운영 연결이 있으면(`sql.production`):** 대상 객체를 찾은 뒤 모델을 부르기 전에 운영 정의와 비교합니다(읽기 조회만). `pelly sql pull`로 받은 뒤 운영에서 바뀌었으면 메시지(`받은 뒤 운영에서 <dbo.이름>가 바뀌었습니다 …`)를 보이고 종료 코드 4(`SqlProdChanged`)로 멈춥니다. 그 밖의 경우(받은 기록 없이 다름, 운영에 없음, 운영 접속 불가)는 알림만 보이고 계속합니다. 파일이 운영과 같은데 받은 기록이 운영보다 오래되었으면(고친 파일을 이미 SSMS로 반영한 경우) `받은 기록이 운영보다 오래되었습니다 — 고치기 전에 pelly sql pull <dbo.이름> --force로 다시 받으십시오`를 알리고 계속합니다. 운영 연결이 없으면 동작·출력이 이전과 같습니다.
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
* 파일은 작업 공간의 `db/pulled/<스키마>.<이름>.sql`(`sql.pullDirectory`·`--out`으로 변경, 작업 공간 밖이면 종료 코드 4)에 UTF-8(BOM)로 씁니다. 첫 줄 주석(받은 기록)에 출처·서버·DB·객체·정의 해시·받은 시각(UTC)이 남습니다.
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
* 받은 기록은 `pelly sql pull`이 파일 첫 줄에 남긴 주석입니다. 운영과 같다는 판정이 받은 기록보다 먼저입니다. 운영과 같은데 받은 기록이 운영보다 오래되었으면 `받은 기록이 운영보다 오래되었습니다 — 고치기 전에 pelly sql pull <dbo.이름> --force로 다시 받으십시오`를 함께 보입니다(종료 코드 0). 정의를 읽는 조회가 실패하면(권한 등) 접속 실패처럼 종료 코드 4입니다.
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
- [ ] T2 sql db/sp/usp_CancelOrder.sql#dbo.usp_CancelOrder   배송 완료 제외 조건 추가
```

* **작업 줄:** `- [ ] <ID> <cs|sql> <파일>[#심볼] <요청> [(AC 참조)]`. 필드 사이는 공백 하나 이상입니다. 작업은 최대 20개입니다. 작업 줄은 제목이 정확히 `## 작업`인 절 아래에만 둡니다 — 다른 절(예: `## 작업 (2차)`)에 있는 작업 줄은 `InvalidTaskLine` 오류입니다.
* **`cs` 작업은 `#멤버`가 필수입니다.** 클래스가 둘 이상이거나 멤버가 겹치면 `#클래스.멤버`로 씁니다. `sql` 작업의 `#객체`는 선택입니다.
* **`pelly spec check`:** 문법, 대상 파일·심볼 존재, AC 참조, 작업 수 상한을 검사합니다. 오류가 있으면 종료 코드 4입니다. 작업 그룹 `[G1]`은 아래 `pelly apply`의 그룹 항목을 보십시오.
* **수용 기준의 `test:`·`breaks:` (V2-2b):** 수용 기준 줄 바로 아래에 들여쓴 `test: <이름>`(수용 기준당 하나)과 `breaks: <이름>`(여러 줄 가능)을 적을 수 있고, 순서는 상관없으며 다른 줄이 나오면 거기서 끝납니다. 이름 형식과 찾기 규칙은 `--verify-test`와 같습니다(`[네임스페이스.]클래스.메서드` 또는 `메서드`, 테스트 특성이 붙은 메서드만 찾음). `breaks:`는 이 변경으로 실패해도 되는 기존 테스트로, `--expect-break`와 같이 회귀 판정에서만 빠집니다. `test:`가 없는 수용 기준과 연결 작업이 없는 명세의 동작은 그대로입니다.
* **연결 작업 규칙:** 수용 기준의 연결 작업은 `(AC…)`로 그 수용 기준을 참조하는 `cs` 작업입니다(명세 순서, 완료 작업 포함). `sql` 작업은 연결 작업이 아니며 테스트로 판정하지 않습니다. **첫 연결 작업의 기준 실행에서 RED**(지정 테스트가 실패하는지)를 확인하고, **마지막 연결 작업에서 GREEN**을 확인합니다. 연결 작업이 하나면 `pelly cs --verify-test`와 같습니다. 연결 작업은 모두 테스트를 돌려 회귀를 보며, 그 수용 기준의 `breaks:`는 회귀에서 뺍니다. 그 사이 작업에서 지정 테스트는 아직 실패하는 것이 정상이라 회귀로 세지 않습니다.
* **`spec check`의 테스트 검사:** 수용 기준에 미완료 연결 작업이 있으면 `test:`·`breaks:` 이름을 못 찾을 때 `TestNotFound`, 여러 테스트와 맞을 때 `TestAmbiguous`가 **오류**입니다(apply가 어차피 종료 4로 멈추므로 미리 알립니다). 연결 작업이 모두 끝났거나 없는 수용 기준이면 같은 코드가 경고입니다. 그 밖에 오류 `TestIsTarget`(미완료 `cs` 작업이 — 연결 여부와 상관없이 — 미완료 연결 작업이 있는 어느 수용 기준의 테스트 메서드를 고침 — 테스트는 사람이 씁니다), `DuplicateTest`(수용 기준에 `test:`가 둘 이상), `InvalidTestName`(이름 형식 오류), `BreaksSameAsTest`(같은 테스트를 `test:`와 `breaks:`에 함께 둠)가 있고, 경고 `TestUnverified`는 `test:`가 있는데 연결 `cs` 작업이 없는 수용 기준(`테스트로 검증되지 않습니다`)입니다.
* **`pelly apply`의 테스트 우선 동작:** 연결 작업은 `pelly cs --verify-test`와 같은 RED→패치→GREEN·회귀 절차로 돌고, 결과는 `apply.json`의 `criteria`(`acId`, `red`, `outcome`: `Met`·`NotMet`·`NotRun`·`Unverified`, `greenRunId`, `breaksExcluded`)에 남습니다. RED에서 이미 통과하면 경고를 보이고, 대화형이면 계속할지 묻습니다(거부하면 종료 2, `TestRedDeclined`). `--yes`이거나 비대화형이면 묻지 않고 기록합니다. `--review-at-end`에서는 작업 중 묻지 않고, 끝의 전체 승인 직전에 「이미 통과한 테스트로 판정된 수용 기준」 목록을 보여 줍니다. `--resume`에서 첫 연결 작업이 이미 `[x]`면 그 수용 기준의 RED는 다시 확인하지 않고(`Skipped`), 마지막 연결 작업이 이전 실행에서 끝났으면 이번에 돌지 않았으므로 `NotRun`입니다. `--continue`에서 GREEN에 실패한 작업은 되돌려져 `[ ]`로 남고 그 수용 기준은 `NotMet`입니다. `--dry-run`은 테스트를 돌리지 않습니다.
* **그룹과 테스트:** 그룹 작업이 연결된 수용 기준이 있으면, 그룹 기준 빌드 뒤에 영향 범위 안 테스트 프로젝트의 기준 테스트를 돌려 그룹 안에 첫 연결 작업이 있는 수용 기준의 RED를 확인합니다. 새 시그니처로 쓴 테스트 프로젝트는 원본에서 빌드되지 않는 **컴파일 RED**로 받습니다. 그룹 빌드가 통과하면 같은 프로젝트를 다시 돌려 마지막 연결 작업이 그룹 안에 있는 수용 기준의 GREEN과 **엄격 회귀**를 봅니다. 새로 실패한 테스트는 어느 작업의 탓인지 알 수 없으므로, GREEN 테스트가 모두 통과하고 테스트 프로젝트에 빌드 오류도 없을 때는 재실행 없이 그룹 전체를 되돌리고 종료 1입니다(`GroupTestRegression`). GREEN도 함께 실패하면 회귀 목록이 GREEN 실패 재실행의 피드백에 담깁니다. GREEN 실패는 그 수용 기준의 그룹 안 마지막 연결 작업만 테스트 피드백과 함께 다시 실행하고(테스트 프로젝트가 아직 빌드되지 않는 수용 기준의 마지막 연결 작업도 빌드 오류 피드백으로 다시 실행합니다), 실패한 테스트를 맡은 그룹 작업이 하나도 없으면 재시도를 쓰지 않고 곧바로 그룹 전체를 되돌리고 종료 1(`GroupTestGreenFailed`)입니다. 재실행은 빌드 오류 재실행과 같은 재시도 예산(`retry.max` 또는 `--max-retries`) 하나를 함께 씁니다. 다 쓰고도 통과하지 않으면 그룹 전체를 되돌리고 종료 1(`GroupTestGreenFailed`)입니다. 그룹의 테스트를 돌릴 수 없으면(편집 전이면 그대로, 편집 뒤면 그룹을 되돌린 뒤) 종료 4(`GroupTestUnverifiable`)입니다. 원본에서도 빌드되지 않던(컴파일 RED) 테스트 프로젝트가 그룹 뒤에도 빌드되지 않는데 그 수용 기준의 GREEN 테스트가 이 그룹에 없으면 `그룹 … 테스트 프로젝트가 그룹 뒤에도 빌드되지 않습니다 — 수용 기준의 마지막 연결 작업까지 같은 그룹에 넣으세요`라고 알리고 같은 종료 4로 멈춥니다. 원본에서는 빌드되던 테스트 프로젝트가 그룹의 편집으로 빌드되지 않으면 GREEN 실패처럼 빌드 오류 피드백으로 다시 실행합니다. 그룹 밖 연결 작업의 실패 유형은 `pelly cs --verify-test`와 같습니다.
* **테스트 변경 보호(V2-2c):** `pelly apply`는 수용 기준의 첫 연결 작업(또는 그 작업이 든 그룹)이 돌기 직전에 지정 테스트의 **메서드 선언**을 해시해 두고, 마지막 연결 작업이 그 값과 비교합니다: 그 사이 다른 작업이 테스트를 바꿨으면 마지막 연결 작업이 되돌려지고 종료 4(`TestFileChanged`, `지정한 테스트 파일이 실행 중에 바뀌었습니다: …`)입니다. 그룹은 기준 테스트 때(그룹 밖에서 시작한 수용 기준은 그 첫 연결 작업 직전에) 잰 값과 GREEN·회귀를 통과한 뒤의 값을 비교해, 다르면 그룹 전체를 되돌리고 `그룹 G1: 지정한 테스트가 실행 중에 바뀌었습니다: <파일>`을 알린 뒤 종료 4(`GroupTestFileChanged`)입니다.
* **알려진 한계(수용 기준 테스트):** 테스트 변경 보호는 지정 테스트 메서드의 선언만 봅니다: 같은 파일의 다른 멤버(도우미·필드·설정 메서드)가 바뀐 것은 보지 않습니다. `--resume`에서 첫 연결 작업이 이미 `[x]`면 작업을 건너는 비교 값이 없어, 작업 하나의 실행 안에서만 봅니다. 대신 `spec check`가 수용 기준의 테스트 메서드를 대상으로 하는 미완료 `cs` 작업을 연결 여부와 상관없이 `TestIsTarget` 오류로 거부합니다.
* **`pelly report`:** 명세 표에 `테스트로 검증되지 않은 수용 기준`(`TestUnverified`에 해당하는 수용 기준 수)과 `breaks로 뺀 테스트` 줄이 더해지고, 수용 기준 충족률은 apply의 `criteria`도 셉니다. 비식별 보고서에는 개수만 나가며 테스트 이름·수용 기준 ID는 나가지 않습니다. 수용 기준 지표는 apply 기록마다 따로 판정합니다: 한 apply에서 RED를 확인했지만 GREEN은 `--resume` 뒤에야 충족한 수용 기준은 처음 기록에서 `NotMet`(마지막 연결 작업이 실패함, 돌지 않았으면 `NotRun`)으로 남고, 이어 한 기록에서는 RED가 `Skipped`라 세지 않습니다. `breaks로 뺀 테스트`는 수용 기준마다 자기 `breaks:`에 맞는 테스트만 셉니다. 편집 뒤 테스트를 판정할 수 없어 되돌린 그룹(`GroupTestUnverifiable`)과 지정한 테스트가 실행 중에 바뀌어 되돌린 그룹(`GroupTestFileChanged`)도 그룹 빌드는 통과했으므로 그룹 빌드 통과율에서는 통과로 셉니다.
* **영향 탐색:** `spec check`는 명세에 작업 그룹이 있으면 미완료 그룹 `cs` 작업의 대상 멤버에서, `--impact`이면 미완료 `cs` 작업 전부에서 시작해 그 멤버(같은 이름의 오버로드 전부)를 구현·재정의하거나 부르거나 참조하는 곳, 그리고 그 멤버가 구현·재정의하는 기반 멤버와 그 멤버의 다른 구현·호출부를 찾습니다. 찾은 곳 `(파일, 멤버)`와 파일·멤버가 같은 작업(완료 작업 포함)이 명세에 없으면 찾은 곳마다 경고 `ImpactMissing` 하나(`작업 T1의 영향 대상이 명세에 없습니다: <파일>#<멤버> (<이유>)`, 이유는 `구현`·`재정의`·`기반 멤버`·`호출`·`멤버 밖`)를 남깁니다. 여러 작업에서 닿은 곳은 첫 작업의 줄에 한 번만, 작업 ID를 모두 적어 남깁니다(`작업 T1, T2의 영향 대상이 …`). 형식 밖(최상위 문 `Program.cs` 등)에서 부르는 곳은 멤버 `<top-level>`, 이유 `멤버 밖`으로 남기며, 어떤 작업도 이 이름을 쓸 수 없으므로 늘 경고로 남습니다. `pelly apply`도 시작할 때 그룹 `cs` 작업에 대해 같은 탐색을 하고, 다른 경고처럼 알린 뒤 계속합니다(대화형이면 계속할지 묻고, `--yes`이면 묻지 않습니다). 의미 분석(프로젝트를 불러 심볼로 찾기)을 쓸 수 없으면 이름 기준으로 대신하고 반드시 `ImpactNameBased`(`영향 탐색: 이름 기준(부정확): 의미 분석 실패 — <유형>`, 예: `복원 안 됨 (dotnet restore 필요)`, `해석률 97.2%`)를 남깁니다. 보조 프로세스 폴더(`BuildHost-*`)가 실행 파일 옆에 없으면 유형은 `빌드 도구 없음`입니다. 일부 폴더·파일을 읽지 못하면 `ImpactIncomplete`, 메서드·속성이 아닌 대상은 `ImpactUnsupported`, 대상 파일이나 멤버를 찾지 못하면 `ImpactSeedNotFound`(`작업 T1: 영향 탐색 시작점을 찾지 못했습니다: <파일>#<멤버>`), 탐색 자체가 실패하면 `ImpactFailed`입니다. `sql` 작업의 탐색은 아래 SQL 영향 탐색을 보십시오. 영향 탐색은 경고만 만들며 종료 코드를 바꾸지 않습니다. `apply.json`에는 `impact`(`semantic`·`name`, 탐색하지 않았으면 `null`)와 `impactFallback`(이름 기준으로 대신한 이유)을 적습니다. 한계: 복원 여부는 프로젝트 폴더의 `obj/project.assets.json`으로 보므로, 중간 출력 폴더를 옮긴 프로젝트(`UseArtifactsOutput`, `BaseIntermediateOutputPath` 변경)는 복원했어도 늘 `복원 안 됨`으로 이름 기준이 됩니다. 대화형 `apply`가 영향 경고를 보이고 계속할지 묻는 동안에는 작업 공간 잠금을 잡고 있으므로, 그동안 같은 작업 공간의 다른 pelly 실행은 거절됩니다.
* **SQL 영향 탐색 (V2-1d):** 미완료 `sql` 작업의 대상 객체(`#객체`, 없으면 파일의 유일한 정의)에서 시작해 ① 그 객체를 참조하는 같은 DB의 모듈(이유 `EXEC`·`참조`), ② 그 이름을 문자열로 쓰는 C# 코드(이유 `이름 호출(문자열)`, 예: `"dbo.usp_X"`, `"EXEC dbo.usp_X @Id"`), ③ 그 객체가 쓰는 컬럼을 읽는 모듈(이유 `같은 컬럼 읽음`, 테이블마다 최대 10개 — 명세의 `sql` 작업이 이미 대상으로 삼은 모듈과 ①로 이미 경고한 모듈은 이 10개에 넣지 않고 요약의 경고 수에도 세지 않습니다)을 찾습니다. `spec check`는 `--impact`일 때만 돌고(운영 연결 `sql.production`이 있으면 운영 DB 기준, 없으면 개발 DB 기준과 경고 `ImpactSqlDevBased`), `apply`는 운영 연결이 설정되어 있을 때만 시작 검사로 돕니다 — 운영 연결이 없으면 `apply`의 출력·질문·DB 조회는 그대로입니다. 운영 연결이 설정되어 있는데 접속할 수 없으면 개발 DB로 대신하지 않고 `ImpactSqlUnavailable`을 남긴 뒤 ②만 합니다. 명세에 없는 SP 대상은 `ImpactMissing`(`… dbo.usp_A (EXEC) — pelly sql pull dbo.usp_A 로 받아 작업 줄에 추가할 수 있습니다 (<sql.pullDirectory>/dbo.usp_A.sql)`; 이름에 공백·`.`·`]` 같은 문자가 있으면 `"[dbo].[My Proc]"`처럼 대괄호와 따옴표로 적고, `pelly sql pull`이 받지 못하는 종류(트리거 등)는 이 안내 없이 대상만 알립니다), 쓰는 테이블마다 요약 `ImpactTableShared` 한 줄을 남기고, 후보 모듈 전체 목록은 `spec check`면 `.pelly/impact/<명세 id>-sql.json`(실행마다 덮어씀), `apply`면 `.pelly/runs/<apply-id>/impact-sql.json`에 둡니다(`apply.json`의 `sqlImpact`에 기준 DB·시작점 수·경고 수·목록 경로). 서버에 아직 없는 새 객체는 `ImpactSeedNotFound`로 알리고 ②만 합니다. 운영에는 카탈로그 읽기 조회만 보냅니다. 한계: 동적 SQL(`EXEC(@sql)`, `sp_executesql`)로 부르는 곳과 다른 DB에서 참조하는 곳은 찾지 못합니다. 이름을 상수 클래스에 두고 쓰는 C# 코드는 상수 정의 위치만 찾고 그 상수를 쓰는 곳은 찾지 않습니다. ③은 서버에 있는 **고치기 전** 정의를 기준으로 하므로 이번 수정이 새로 쓰기 시작할 컬럼은 반영되지 않습니다.
* **`pelly apply`:** 작업마다 성공하면 줄을 `- [x] … <!-- run: <run-id> -->`로, 실패하면 `<!-- failed: <run-id> <종류> -->`로 표시하고 멈춥니다. `--continue`는 실패한 작업을 건너뛰고 나머지를 이어 가며(종료 코드는 1 — 작업별 검토에서 거부한 작업도 실패로 셉니다), `--resume`은 완료 표시가 된 작업을 건너뛰고 첫 미완료 작업부터 이어 갑니다. `[x]`가 있는 명세를 `--resume` 없이 실행하면 종료 코드 4입니다.
* **검토:** 기본은 작업마다 diff 승인을 묻습니다. `--review-at-end`는 전부 끝난 뒤 한 번만 묻고, 거부하면 이번 apply가 바꾼 파일을 모두 되돌리고 이번에 남긴 체크 표시와 상태도 되돌립니다(종료 코드 2). 실행 중 Ctrl+C로 취소해도 같은 방식으로 되돌립니다. 단, apply 도중 pelly 밖에서 바뀐 파일은 덮어쓰지 않고 그대로 두며, 그 파일을 알리고 종료 코드 4로 끝납니다. 그 파일의 작업은 `[x]`를 유지합니다. `--review-at-end`는 대화형 실행에서만 쓸 수 있습니다(비대화형이면 `--yes`가 있어도 시작 전에 종료 코드 4). 명세 파일을 쓸 수 없어 중간에 멈추면 파일을 되돌릴 수 없으므로, 검토하지 않은 변경이 남은 파일을 경고로 알려 줍니다.
* **작업 그룹(`[G1]`)** 은 시그니처·이름을 바꾸는 `cs` 작업을 묶어, 마지막 작업 뒤에 한 번만 빌드 검증합니다. 같은 그룹의 작업은 명세에서 연속해야 하고(`GroupNotContiguous`), `sql` 작업은 넣을 수 없습니다(`SqlInGroup`). 그룹 작업에서는 대상 멤버의 반환 형식·매개변수·한정자·특성·이름을 바꿀 수 있지만, 다른 멤버를 바꾸거나 지울 수 없고 using을 지울 수 없으며 public 멤버를 추가할 수 없습니다(private 도우미는 가능). 이름이 바뀐 대상은 같은 형식에 새로 생긴 같은 종류·같은 접근 수준의 멤버 중 같은 이름, 없으면 같은 자리(앞뒤에 그대로 있는 멤버로 셈)의 멤버로 짝짓습니다. private 대상 바로 옆(위나 아래)에 private 도우미도 새로 생겨 자리로 가를 수 없으면, 짝을 잘못 짓지 않도록 수정을 거절하고 도우미를 대상에서 떨어진 자리에 두라고 피드백합니다(시그니처가 그대로인 쪽을 고르지 않습니다 — 이름과 시그니처를 함께 바꾼 대상 옆에 옛 시그니처의 도우미가 생긴 경우와 구별되지 않기 때문입니다). 그룹의 첫 작업 전에 영향 범위(그룹 파일의 프로젝트와, 그 프로젝트를 직접·간접으로 `ProjectReference`하는 프로젝트 — 후보는 작업 공간 폴더에서 찾은 `.csproj`와 작업 공간의 `.sln`·`.slnx`에 적힌 프로젝트이며, `packages` 폴더는 모든 하위 폴더에 `.nupkg`가 있는 packages.config 복원 폴더일 때만 건너뜁니다)를 기준 빌드하고, 마지막 작업 뒤에 다시 빌드해 새로 생긴 오류만 셉니다(그룹 빌드는 `GenerateFullPaths=true`로 오류 경로를 전체 경로로 받고, 상대 경로로 나온 오류는 그 프로젝트 폴더 기준으로 풉니다). 새 오류가 그룹 작업의 대상 멤버 안이거나, 그 작업이 더한 private 도우미 안이거나, 그 작업 대상이 속한 형식의 선언 줄·기본 형식 목록(예: 인터페이스를 더 이상 구현하지 않는 `CS0535`·`CS0738`)이면 그 작업이 빌드 피드백을 받아 다시 실행됩니다(그룹 전체 `retry.max` 또는 `--max-retries`; 재실행에서는 그 작업이 더한 도우미도 고칠 수 있습니다). 재실행을 다 써도 새 오류가 남으면 그룹 전체를 되돌리고 종료 코드 1(`GroupBuildFailed`)입니다. 그 밖의 오류(다른 멤버, 멤버 밖, 그룹 밖 파일)는 명세에 빠진 영향 대상이므로 그룹 전체를 되돌리고 위치를 알린 뒤 종료 코드 1(`GroupMissingTarget`)입니다. 위치는 `<파일>#<멤버>`, 멤버 밖이거나 파일을 읽을 수 없으면 `<파일>:<줄>`, 파일이 없는 오류는 오류 코드입니다(예: `그룹 G1: 명세에 빠진 영향 대상: src/Web/OrdersController.cs#Cancel, src/App/Mapper.cs:12`). 검증할 수 없으면 파일을 고치기 전에 종료 코드 4(`GroupUnverifiable`)입니다: 그룹 파일이 속한 `.csproj`가 없음, 영향 범위를 확정할 수 없음(작업 공간 폴더가 2000개를 넘음, 목록이나 속성을 읽을 수 없는 폴더, 읽을 수 없는 솔루션·프로젝트 파일, 작업 공간 밖을 가리키거나 대상을 알 수 없는 링크·정션 폴더 — 작업 공간 안을 가리키는 링크는 건너뜁니다, `$(…)` 속성으로 경로를 만든 `ProjectReference`, 범위 안 프로젝트의 `ProjectReference`가 없는 `.csproj`를 가리킴), 레거시 프로젝트를 Windows 밖에서 빌드, 빌드 도구 없음, 기준 빌드의 비컴파일러 오류·시간 초과·시작 실패, 범위 안 다른 프로젝트가 참조하는 프로젝트에 기준 빌드부터 컴파일 오류가 있음(그 프로젝트를 참조하는 프로젝트는 컴파일되지 않아 깨져도 보이지 않습니다). 메시지는 `그룹 빌드 검증 불가 (<프로젝트>): <원인>` 꼴이며, 시간 초과는 `build.timeoutSeconds`의 초를, 비컴파일러 오류는 오류 코드·메시지와 조치를 적습니다. 편집 뒤의 검증 빌드가 검증할 수 없는 상태가 되면(시간 초과 등) 그룹을 되돌리고 종료 코드 4입니다. 그룹 승인은 빌드가 통과한 뒤 한 번 묻고(`--yes`·`--review-at-end`이면 묻지 않음) 거부하면 그룹을 되돌립니다(종료 코드 2). 성공하면 그룹의 모든 작업이 함께 `[x]`가 되고, 실패하면 그룹의 모든 작업에 `<!-- failed: <run-id> <종류> -->`가 붙습니다(종류: `GroupUnverifiable`, `GroupBuildFailed`, `GroupMissingTarget`, `GroupRejected`, `GroupTestGreenFailed`, `GroupTestRegression`, `GroupTestUnverifiable`, `GroupTestFileChanged` 또는 작업의 실패 종류). 되돌리는 중 pelly 밖에서 바뀐 파일은 덮어쓰지 않고 알리며(종료 코드 4), 그 파일의 작업은 `[x]`를 유지합니다(표시가 파일 상태와 맞도록). `--resume`은 `[x]`인 작업을 건너뛰고 첫 미완료 작업부터 이어 가므로, 실패한 그룹은 `[x]`가 아닌 작업만 한 그룹으로 다시 실행합니다. 그룹 일부가 `[x]`로 남았다면 그 작업들은 다시 실행되지 않으니 그 파일을 먼저 직접 확인하십시오. `spec check`와 `--resume`은 완료된 작업의 심볼·요청문 이름을 검사하지 않습니다(그룹이 이름을 바꿨을 수 있으므로). `--continue`는 실패한 그룹을 되돌린 뒤 다음 작업으로 가되, 그 그룹의 파일을 대상으로 하는 뒤 작업이 있으면 거기서 멈춥니다(검증할 수 없는 그룹이면 바로 멈춥니다). 첫 미완료 작업이 그룹 작업이면 `--dry-run`은 그 작업 하나만 그룹 모드(시그니처·이름 변경 허용)로 실행해 diff를 보여 줍니다. 영향 범위 탐색·기준 빌드·검증 빌드는 하지 않으므로 위의 종료 코드 4 사유도 검사하지 않고(dry-run이 통과해도 실제 apply가 검증 불가로 멈출 수 있습니다), 그룹의 나머지 작업은 실행하지 않으며, 파일과 명세 파일을 바꾸지 않습니다. 그룹 백업은 `.pelly/backup/<apply-id>.<그룹 ID>/`에 둡니다. 명세에 빠진 영향 대상은 위의 영향 탐색으로 미리 찾을 수 있습니다.
* **`--dry-run`:** 첫 미완료 작업 하나만 실행해 diff를 보여 주고, 파일과 명세 파일 어느 것도 쓰지 않습니다. 비대화형 실행에서도 `--yes` 없이 쓸 수 있습니다.
* **비대화형 실행에는 `--yes`가 필요합니다**(`--dry-run` 제외). 없으면 파일을 건드리기 전에 종료 코드 4입니다.
* **전체 작업이 성공하면** 명세를 `status: done`으로 바꿔 `changes/archive/<yyyy-MM-dd>-<id>.md`로 옮깁니다(같은 이름이 있으면 `-2`, `-3`). 하나라도 실패하면 보관하지 않고 `status: in-progress`로 둡니다.
* **기록:** 백업은 `.pelly/backup/<apply-id>/`, 요약은 `.pelly/runs/<apply-id>/apply.json`입니다. 작업별 실행 기록은 기존 `.pelly/runs/<run-id>/`입니다. apply 한 번이 작업 공간 잠금을 끝까지 잡습니다.
* **종료 코드:** 0 전부 성공, 1 검증 실패, 2 사용자 거부, 3 대상 찾기 실패, 4 환경·입력 오류(명세 오류, 실행 중 명세 파일을 바깥에서 고친 경우 포함), 5 안전 거부. `--continue`에서 전역적 오류(4)가 나면 거기서 멈춥니다.
* 명세 파일이 실행 중에 사람의 손으로 바뀌면 덮어쓰지 않고 종료 코드 4로 멈춥니다.

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

### `pelly doctor` — 환경 점검
```bash
pelly doctor [--project <csproj>] [--sanitize [--out <폴더>]] [--config <경로>] [--model <태그>] [--endpoint <URL>]
```
Ollama 서버 접속, 모델 존재·로드 여부, chat template, 토크나이저, 응답 시간, pelly 자체와 설정, .NET SDK(`dotnet --version`), 레거시 프로젝트용 MSBuild(Windows에서만), 타겟팅 팩, 의미 분석(영향 탐색용 보조 프로세스 폴더 `BuildHost-*` 유무, `--project`를 주면 불러오기까지), SQL 연결(허용 목록·`VIEW DEFINITION`, 연결 문자열이 설정된 경우), 서버 관리자 확인 체크리스트를 표로 보입니다. 하나라도 Fail이면 종료 코드 4입니다.
* `--project <csproj>`: 그 프로젝트의 .NET Framework 타겟팅 팩이 설치됐는지 확인합니다(Windows에서만, 실기 확인 전). 의미 분석 행도 이 프로젝트를 불러와 봅니다. 불러오지 못하면 Warn이고(종료 코드는 그대로) 영향 탐색은 이름 기준으로 대신합니다. 보조 프로세스 폴더가 없으면 Fail입니다.
* `--sanitize`: 화면 대신 비식별 결과 `json`·`md`를 현재 폴더(또는 `--out`)에 씁니다. 행 이름·상태·버전 같은 짧은 값만 남고 경로·연결 문자열·설명 문장은 나가지 않습니다.

### `pelly report` — 실행 기록 요약
```bash
pelly report [--since 2026-10-01] [--sanitize [--out <폴더>]]
```
현재 작업 공간의 `.pelly/runs/*/summary.json`을 모아 TTFT·대기·패치 포맷 파싱 성공률·최종 성공률·완료 시간 표와 종료 코드·실패 유형·시도 수 분포를 보입니다.
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
* `deploy/version-record.json`은 개발 PC의 Ollama에서 `dotnet run --project eval/PellyCoder.Eval -- record --out deploy/version-record.json`으로 다시 만듭니다(현장 모델의 GGUF SHA-256은 `--gguf-sha256 <hex>`, 현장에서는 `pelly-eval record`). 반입 전에 사람이 다시 실행합니다. Apple 칩 Mac에서는 `ollama pull qwen3.6:35b`가 GGUF가 아닌 `nvfp4` 판을 받으므로, GGUF를 받는 방법은 [docs/m4-gguf-baseline.md](docs/m4-gguf-baseline.md) 1절을 따르십시오.
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
dotnet run --project eval/PellyCoder.Eval -- compare docs/m4-gguf-baseline.summary.json <후보 summary.json>
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
