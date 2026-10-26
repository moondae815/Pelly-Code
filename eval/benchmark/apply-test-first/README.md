# 테스트 우선 apply 사례 (`pelly-eval apply --set test-first`)

이 폴더의 `t1`~`t8`은 수용 기준에 `test:`(·`breaks:`)를 단 명세를 `pelly apply`로 끝까지 돌리는 사례입니다(설계 V2-2c §5). 실행 방법·산출물은 상위 `eval/benchmark/README.md`의 「apply 벤치마크」 절을 보십시오.

## 폴더 형식
폴더 하나가 사례 하나이고, 폴더 이름이 사례 id입니다. 하위 폴더는 모두 사례여야 합니다(`spec.md`가 없으면 실행기가 거절).

| 파일 | 내용 |
| :--- | :--- |
| `spec.md` | 명세. 머리말은 `id`(= 폴더 이름), `status: draft`, `scope`(`Modern`·`Legacy`·`Sql`, 기존 apply 사례와 같은 규칙) |
| `files/` | 작업 복사본의 `corpus/` 위에 같은 상대 경로로 덮어 넣는 파일. RED 테스트 파일, 새 테스트 프로젝트와 그것을 넣은 `Modern/Modern.sln` 사본, 레거시 테스트 프로젝트 사본 |
| `expected.json` | `[{ "acId", "red", "outcome" }]`. `test:`가 있는 수용 기준마다 한 줄(명세 순서). `red`는 `RedState`(`Red`·`AlreadyPassed`·`RedCompile`·`NotChecked`), `outcome`은 `AcOutcome`(`Met`·`Unverified` 등) 이름. **판정에 쓰지 않고** 사람 승인과 요약 표의 「기대」 칸에만 씁니다 |
| `pelly.json`(선택) | 그 사례의 실행에만 합치는 설정 조각. `{ "test": { "command": "…" } }`만 받습니다(t8의 레거시 `test.command`) |

* 코퍼스(`eval/corpus`)와 판정 프로젝트(`judges/`)는 쓰지 않고 고치지 않습니다. RED 테스트는 `files/` 안에만 둡니다.
* 새 테스트 프로젝트를 `files/`로 넣으면 `files/Modern/Modern.sln`(원본 사본 + 그 프로젝트)도 함께 넣습니다. 실행기의 `Modern.sln` 복원이 그 프로젝트를 덮어야 pelly와 독립 재실행이 돌 수 있습니다(아니면 「검증 불가」).
* 독립 빌드의 「바뀐 `.cs`」 비교 기준은 「원본 코퍼스 + 그 사례의 `files/`」입니다.

## 사례
| id | 범위 | 다루는 것 | 수용 기준 테스트(`files/`) | 기대 |
| :--- | :--- | :--- | :--- | :--- |
| t1 | Modern | AC 하나를 서비스·컨트롤러 두 작업이 채움(첫 작업 RED, 마지막 작업 GREEN) | `Shop.Api.Tests/CancelReasonTests.cs` | AC1 Red/Met |
| t2 | Modern | `breaks:`로 옛 동작 테스트 `OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels`를 뺌 | `Shop.Api.Tests/ShippedCancelTests.cs` | AC1 Red/Met |
| t3 | Modern | 그룹 `[G1]` 시그니처 변경(`CancelAsync` → `Task<decimal>`) + 새 시그니처로 쓴 테스트(컴파일 RED, 엄격 회귀) | `Shop.Api.Tests/CancelRefundTests.cs` | AC1 RedCompile/Met |
| t4 | Modern | 이미 통과하는 테스트(RED 확인 경고, `--yes`로 계속) | `Shop.Api.Tests/DeliveredCancelTests.cs` | AC1 AlreadyPassed/Met |
| t5 | Modern | 컨트롤러 취소 작업 T2가 AC1의 마지막이자 AC2의 첫 연결 작업 | `Shop.Api.Tests/OrderRequestValidationTests.cs` | AC1·AC2 Red/Met |
| t6 | Modern+Sql | AC1은 SQL·C# 작업이 함께 참조(SQL 작업은 판정 안 함), AC2는 SQL 작업만 참조(`TestUnverified` 경고, 테스트는 SQL 서버가 있어야 도는 `Skip` 테스트) | `Shop.Api.Tests/RecancelTests.cs` | AC1 Red/Met, AC2 NotChecked/Unverified |
| t7 | Modern | 수용 기준 테스트가 대상 프로젝트를 참조하지 않는 새 테스트 프로젝트 `Shop.Services.Tests`(서비스 소스를 `Compile Include`)에 있음: 「선언 프로젝트 합집합」으로만 찾음 | `Shop.Services.Tests/OrderSearchRangeTests.cs` | AC1 Red/Met |
| t8 | Legacy | 레거시 MSTest 프로젝트 + 사례 `pelly.json`의 `test.command`(`{trx}`), Windows 전용 | `Legacy/Shop.Tests/PendingCancelTests.cs`(+ `Shop.Tests.csproj` 사본) | AC1 Red/Met |

* t6은 범위에 `Sql`이 있어 SQL 연결이 없으면 건너뜁니다. t8은 Windows가 아니면 건너뜁니다. t8의 명령은 `vstest.console.exe`를 이름만 씁니다. `PATH`에 없으면(개발자 명령 창이 아닐 때, CI 포함) 실행기가 vswhere로 최신 Visual Studio의 `TestPlatform\vstest.console.exe`를 찾아 바꿔 넣습니다. 또 `Legacy/packages`(`eval/corpus/Legacy/build.ps1`)가 준비되어 있어야 합니다.

## 정의 검사
* `tests/PellyCoder.Eval.Tests/ApplyTestFirstCasesTests`: 사례 8건과 `expected.json`, `spec check`(코퍼스 사본 + `files/`) 오류 0·경고는 `Unverified` 기대 수만큼의 `TestUnverified`뿐, `files/`의 Modern 테스트 프로젝트가 `Modern.sln`에 있음.
* `tests/PellyCoder.Integration.Tests/Benchmark/ApplyTestFirstCaseDefinitionTests`(실제 dotnet): t1·t2·t5·t6·t7의 `test:`는 원본 + `files/`에서 실패, t4는 통과, t3은 테스트 프로젝트가 그 테스트 파일 안의 CS 오류로만 빌드되지 않음, `breaks:`는 원본에서 통과. t8은 Windows에서만 돕니다.

## 승인
* 사람이 사례를 검토한 뒤 `APPROVED.json`을 이 폴더에 만듭니다. 구현자는 만들지 않습니다.
  ```json
  { "approvedBy": "<이름>", "approvedAt": "<yyyy-MM-dd>", "commit": "<검토한 커밋>", "cases": ["t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8"] }
  ```
* `commit`은 검토한 사례가 들어 있던 커밋(기록용)입니다. 실행기는 `cases`만 읽고 `approvedBy`·`approvedAt`·`commit`은 읽지 않습니다.
* `cases`에 없는 사례가 실행 집합에 하나라도 있으면 `apply-summary.md` 제목에 「예비(사람 승인 전)」가 붙습니다.
* 승인 뒤에 사례를 고치면(명세·`files/`·`expected.json`·`pelly.json`) 그 사례를 다시 승인받아야 합니다: 사람이 다시 검토한 뒤 `APPROVED.json`의 `approvedAt`·`commit`을 새로 적습니다. 실행기는 내용이 바뀐 것을 알아채지 못하므로, 다시 승인받기 전까지는 `cases`에서 그 사례를 빼 두어 요약이 「예비(사람 승인 전)」로 나오게 합니다.
