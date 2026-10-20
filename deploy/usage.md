# 사용 가이드

pelly는 C# 메서드 하나나 SQL 저장 프로시저·함수·뷰 하나를 요청대로 수정하고, 컴파일러와 DB 엔진으로 검증합니다.
설치는 [install.md](install.md)를 먼저 보십시오.

## 기본 흐름

1. 대상과 요청문을 줍니다.
2. 모델이 수정안을 만듭니다. pelly가 파일에 먼저 쓰고 검증합니다.
3. 검증을 통과하면 diff를 보여 주고 승인을 묻습니다. 거부하면 원래 파일로 되돌립니다.

승인 질문이 떠 있는 동안 파일은 이미 바뀐 상태입니다. IDE나 파일 감시 도구가 그 변경을 볼 수 있습니다.

## `pelly cs` — C# 멤버 하나 수정

```
pelly cs <파일> ["<요청문>"] [--class <클래스>] [--method <메서드>] [--line N]
         [--yes] [--dry-run] [--max-retries N] [--strict-baseline] [--warn-as-error]
         [--test] [--verbose] [--pause-on-exit]
         [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
```

**대상 지정 (3가지)**

1. `--method <이름>` (+ 선택 `--class <클래스>`). 같은 이름이 여러 개면 `--class`로 좁힙니다.
2. `--line N`. 그 줄을 포함하는 멤버를 고칩니다. IDE 커서 줄을 넘길 때 씁니다. `--class`/`--method`와 함께 쓸 수 없고 1 이상이어야 합니다.
3. 둘 다 생략. 요청문에 나온 식별자로 찾고, 없으면 모델이 멤버 목록(최대 60개)에서 고릅니다.

요청문을 생략하면 대화형 실행에서는 입력을 묻고, 입력이 리디렉션된 실행에서는 종료 코드 4입니다.

```
pelly cs Shop.Api\Services\OrderService.cs "배송 중인 주문은 취소할 수 없게 해줘" --method CancelAsync --test --verbose
pelly cs Shop.Api\Services\OrderService.cs "null 검사 추가" --line 42 --pause-on-exit
pelly cs Shop.Api\Services\OrderService.cs
```

* 모델은 대상 멤버 안을 고치면서 private 도우미 메서드를 추가할 수 있습니다. 시그니처 변경과 다른 멤버 수정은 거부되어 재시도됩니다.
* 요청문이나 대상 파일에 모델 제어 토큰 문자열(`<|im_end|>` 등)이 있으면 서버를 부르기 전에 종료 코드 4로 멈춥니다.
* 파일에 CRLF와 LF가 섞여 있으면 알림을 보여 주고, 수정하면 많은 쪽으로 통일됩니다.

## `pelly sql` — 저장 프로시저·함수·뷰 하나 수정

```
pelly sql <파일.sql> ["<요청문>"] [--object <이름>] [--line N]
          [--yes] [--dry-run] [--max-retries N] [--verbose] [--pause-on-exit]
          [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
```

* 수정할 수 있는 것은 **대상 객체 배치 하나**입니다. 같은 파일의 다른 배치가 바뀌면 거부됩니다.
* 대상 지정: 파일에 `CREATE`/`ALTER PROCEDURE|FUNCTION|VIEW`가 하나면 자동입니다. 여럿이면 `--object dbo.이름` 또는 `--line N`으로 지정합니다. 지정이 없으면 대화형은 선택 메뉴, 비대화형은 종료 코드 3입니다. `--line`과 `--object`는 함께 쓸 수 없습니다.
* 검증은 DB 서버가 합니다. 참조 객체가 있는지 보고, `SET NOEXEC ON`으로 컴파일해 새 오류가 없는지 봅니다. 트랜잭션 안에서 하고 롤백하므로 객체는 실제로 바뀌지 않습니다. 원본에 이미 있던 오류는 세지 않습니다.
* 서버 연결이 필수입니다. 연결할 수 없으면 종료 코드 4입니다. 설정은 [install.md](install.md)의 4절을 보십시오.
* 검증 계정 권한은 DB 수준 `VIEW DEFINITION` 하나면 됩니다. 없으면 종료 코드 4와 안내 메시지로 멈춥니다.
* 쓰기 전에 안전 가드가 금지 구문을 검사합니다(`GRANT`/`REVOKE`/`DENY`, `xp_cmdshell`, `OPENROWSET`, `SET NOEXEC` 등). 걸리면 재시도하고, 끝내 못 고치면 종료 코드 5입니다. 원본 대상에 이미 금지 구문이 있으면 모델을 부르기 전에 종료 코드 5로 멈춥니다.

```
pelly sql Procedures\usp_CancelOrder.sql "이미 배송된 주문은 취소하지 않고 오류를 내게 해줘"
pelly sql Settlement_All.sql "월 정산에 수수료 컬럼 추가" --object dbo.usp_SettleMonthly
pelly sql Settlement_All.sql "조건 추가" --line 120
```

## 자주 쓰는 옵션

| 옵션 | 동작 |
| :--- | :--- |
| `--yes` | 승인 질문 없이 승인. 검증은 그대로 합니다 |
| `--dry-run` | 파일을 쓰지 않고 diff만 보여 줌 |
| `--test` | (`cs`만) 검증 때 테스트도 실행. 아래 절 참고 |
| `--verbose` | 끝난 뒤 단계별 시간·토큰·시도 요약과 실행 폴더 경로를 보여 줌. 프롬프트 원문은 출력하지 않고 `attempt-N/prompt.txt` 경로를 알려 줌 |
| `--pause-on-exit` | 결과를 보여 준 뒤 키 입력을 기다림. IDE 창이 바로 닫히지 않게 할 때 씀 |
| `--max-retries N` | 교정 재시도 최대 횟수 |
| `--strict-baseline` | (`cs`만) 원래 있던 CS 오류도 종료 코드 4로 처리 |
| `--warn-as-error` | (`cs`만) 새로 생긴 경고도 오류로 취급 |
| `--config <경로>` | 작업 공간 설정 파일 지정 |
| `--model`, `--endpoint`, `--think` | 설정을 이번 실행만 덮어쓰기 |

`--pause-on-exit`는 `pelly doctor`와 `pelly report`에서도 똑같이 동작합니다.
모르는 옵션(예: `--dry-rum` 같은 오타)은 무시하지 않고 `Unknown option` 메시지와 함께 종료 코드 4로 멈춥니다. 아무것도 쓰지 않습니다. 값이 필요한 옵션을 값 없이 쓴 경우(예: `--out`을 줄 끝에 쓰거나 바로 뒤에 다른 옵션을 쓴 경우)도 `--out 옵션에 값이 없습니다`와 함께 종료 코드 4로 멈춥니다.
입력이 리디렉션되어 있으면 기다리지 않습니다. 키 대기는 Windows 미확인입니다.
SQL 안의 `#임시테이블` 컬럼, 동적 SQL 안의 문장, 다른 DB·연결된 서버 객체는 검증하지 못하고 알림만 보여 줍니다.

## `--test` — 테스트까지 검증 (C# 전용)

* 대상 프로젝트를 참조하고 이름에 `Test`가 든 테스트 프로젝트(또는 `test.projects`로 지정한 것)를 수정 전후로 실행합니다. **새로 실패한 테스트만** 실패로 봅니다.
* 원본 상태의 테스트를 돌리지 못하면 알림과 함께 빌드로만 검증합니다.
* 제한 시간은 `test.timeoutSeconds`(기본 300초)입니다.
* 레거시 프로젝트는 `test.command` 설정이 필요하며 Windows 미확인입니다.
* 테스트별 결과를 읽는 테스트 프로젝트: VSTest 방식(`Microsoft.NET.Test.Sdk`, 측정은 xunit v2), `xunit.v3`, `xunit.v3.mtp-*`. `MSTest.Sdk` 같은 다른 Testing Platform 프로젝트는 종료 코드만 봅니다.
* 여러 대상 프레임워크(`TargetFrameworks`)를 가진 Testing Platform 테스트 프로젝트(xunit v3, MSTest.Sdk 등)는 **첫 번째 TFM만** 실행합니다. 나머지 TFM에서만 깨지는 테스트는 잡지 못합니다.

## 종료 코드

| 코드 | 의미 |
| :-: | :--- |
| 0 | 성공 |
| 1 | 검증 실패 (재시도를 소진해도 통과하지 못함) |
| 2 | 사용자 거부 또는 중단 (Ctrl+C·종료 신호·콘솔 창 닫기, 원본 복원) |
| 3 | 대상 찾기 실패 |
| 4 | 환경 오류 (서버·모델·설정·잘못된 인자(모르는 옵션 포함)·외부 편집 충돌) |
| 5 | 안전 거부 |

중단 신호를 받으면 원본 복원이 끝날 때까지 최대 4.5초 기다린 뒤 끝납니다.
콘솔 창 닫기가 같은 경로를 타는지는 Windows 미확인입니다.

## 실행 기록

작업 공간의 `.pelly/runs/<runId>/`에 `summary.json`, `events.jsonl`, `attempt-*/`가 남습니다.
수정 전 원본은 `.pelly/backup/<runId>/`에 있습니다. `.pelly/`는 자동으로 버전 관리에서 제외됩니다.
오래된 기록은 `log.retentionDays`(기본 30일) 뒤에 지워집니다.

## 반출 자료 만들기: `pelly report --sanitize`

폐쇄망 밖으로 결과를 보내야 할 때 씁니다.

```
pelly report --sanitize --out C:\export
pelly report --since 2026-10-01 --sanitize
pelly doctor --sanitize --out C:\export
```

* `pelly-report-<시각>.json`과 `.md`가 만들어집니다. `--out`을 생략하면 현재 폴더에 씁니다.
* 허용 목록 방식입니다. 소스, 경로, 식별자, 모델 이름, RunId는 나가지 않고 숫자 지표, 오류 코드(`CS0103` 등), 실패 유형, 버전만 담깁니다.
* 반출 가능 여부는 사내 보안 규정을 따르십시오. 파일 내용을 직접 열어 확인한 뒤 반출하십시오.
* `--sanitize` 없이 `pelly report`만 실행하면 화면에 TTFT·성공률·완료 시간 표를 보여 줍니다.

## 알려진 한계

* **인라인 SQL은 DB로 검증하지 않습니다.** C# 문자열 안의 SQL(Dapper, `SqlCommand.CommandText`)은 C# 빌드만 통과하면 승인됩니다. 잘못된 컬럼명은 실행할 때 드러납니다. 바뀐 문자열 리터럴에 `SELECT`·`INSERT`·`UPDATE`·`DELETE`·`EXEC`가 있으면 「인라인 SQL이 바뀌었습니다」 경고를 `--yes`일 때도 보여 줍니다.
* **요청이 기존 테스트와 모순되면 `--test`가 속을 수 있습니다.** 모델이 요청을 로그 출력 같은 것으로 바꿔 회피하고 테스트를 통과해 종료 코드 0이 될 수 있습니다. 실측에서 20회 중 5회(25%, M3a), 같은 요청 단독 재측정에서 20회 중 7회(35%, 2026-10-19)였고, 현장과 같은 모델 파일(`qwen3.6:35b` GGUF)로는 20회 중 9회(45%, 2026-10-20)였습니다. `--test`를 써도 **diff를 직접 읽고** 승인하십시오.
* 레거시 .NET Framework 프로젝트의 빌드 검증과 `--test`는 Windows에서 실기 확인하지 않았습니다(Windows 미확인). 반입 후 현장에서 확인합니다(M4).
* SQL 경로(`pelly sql`)도 Windows에서 실기 확인하지 않았습니다(Windows 미확인).
* Razor 뷰(`.cshtml`)와 설정 파일(`web.config`, `appsettings.json`)은 수정 대상이 아닙니다.
* 컨트롤러 액션의 라우팅·필터 특성(`[HttpPost]`, `[Authorize]` 등)은 바꿀 수 없습니다. 의도된 동작입니다.
* 벤치마크는 현장과 같은 모델 파일(`qwen3.6:35b` GGUF `Q4_K_M`)과 0.9.1 기본값으로 50건 × 3회에서 평균 46.00건(92%) 성공이었습니다. 실패 4건 중 하나(`Math.Round(x, -1)`)는 빌드는 통과하고 실행할 때 예외가 나는 수정이라 빌드 검증으로는 잡히지 않습니다. 상세는 반입 패키지의 `benchmark-summary.md`에 있습니다. 이 수치는 개발 PC(Mac) 기준이고, Windows·GPU 서버 값은 현장에서 확인합니다.

## 관련 문서

* 설치: [install.md](install.md)
* IDE 연동: [ide-external-tools.md](ide-external-tools.md)
* 서버 관리자용 확인 사항: [server-checklist.md](server-checklist.md)
* 실행 중 외부 통신 없음: [no-network.md](no-network.md)
