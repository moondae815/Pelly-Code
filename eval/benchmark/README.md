# 벤치마크 작업 정의

`tasks/*.yaml` 한 파일이 작업 한 건입니다. 실행기 `eval/PellyCoder.Eval`이 이 정의를 읽어 실행하고 판정합니다(`dotnet run --project eval/PellyCoder.Eval -- --help`).
정의 형식은 `tests/PellyCoder.Integration.Tests/Benchmark/BenchmarkDefinitionTests.cs`가 검증합니다.

## 스위트 (`suite.json`)
이 폴더(`eval/benchmark`)가 내장 스위트입니다. 현장 코드베이스로 벤치마크할 때는 같은 모양의 폴더(`suite.json` + `tasks/*.yaml`, 선택 `judges/<루트 이름>/`)를 만들고 `--suite <폴더>`로 지정합니다. `--suite`가 없으면 현재 폴더, 그다음 실행 파일 폴더에서 위로 올라가며 `eval/benchmark/suite.json`을 찾습니다.

```json
{
  "formatVersion": 1,
  "name": "builtin",
  "roots": [
    { "name": "Modern", "path": "../corpus/Modern" },
    { "name": "Legacy", "path": "../corpus/Legacy" },
    { "name": "Sql", "path": "../corpus/Sql" }
  ],
  "supportFiles": ["../corpus/Directory.Build.props", "../corpus/Directory.Packages.props", "../../global.json"],
  "resultsDir": "../results"
}
```

| 키 | 형식 | 설명 |
| :--- | :--- | :--- |
| `formatVersion` | 정수 | `1`만 읽습니다 |
| `name` | 문자열 | 필수, 빈 문자열 불가 |
| `roots` | 목록 (하나 이상) | 작업 폴더 `work/<id>/<name>/`으로 복사되는 원본 폴더 |
| `roots[].name` | 문자열 | 형식 `\A[A-Za-z0-9_-]+\z`, 서로 달라야 함(대소문자 무시). `judges`는 쓸 수 없음 |
| `roots[].path` | 문자열 | 존재하는 폴더. 두 루트가 같은 폴더이거나 한쪽이 다른 쪽 안에 있으면 오류 |
| `roots[].exclude` | 문자열 목록 (선택) | 복사에서 뺄 폴더 **이름**(경로 아님, 어느 깊이에서든, 대소문자 무시). `bin`·`obj`·`.pelly`·`TestResults`·`.git`·`.vs`·`.svn`·`.hg`·`$tf`는 항상 뺍니다 |
| `roots[].overlay` | 문자열 (선택) | 루트 복사본 위에 덧씌울 폴더. 같은 이름 파일은 overlay가 이깁니다. 코퍼스를 바꾸지 않고 이 스위트에서만 `AGENTS.md` 같은 파일을 넣을 때 씁니다. 존재하는 폴더여야 함 |
| `supportFiles` | 문자열 목록 (선택) | 작업 폴더 맨 위(`work/<id>/`)에 파일 이름 그대로 복사할 파일. 존재해야 하고 파일 이름이 서로 달라야 함 |
| `resultsDir` | 문자열 (선택) | 기본 출력 폴더의 부모. 기본 `results` |

* 경로는 `suite.json`이 있는 폴더 기준 상대 경로 또는 절대 경로입니다. 공백·한글이 있는 경로도 됩니다.
  * Windows 드라이브 문자 경로(예: `"D:\\소스\\ERP 본사"`, JSON에서는 `\`를 두 번 씀)는 「Windows 미확인」입니다. 해석 규칙(`Path.GetFullPath`)은 같지만 Windows 실기에서 돌려 보지 못했습니다.
* 모르는 키는 오류입니다(오타가 조용히 무시되지 않게). 스위트 오류가 있으면 실행기는 오류를 모두 출력하고 종료 4로 끝납니다.
* 작업의 `file`을 해석하지 못하면(알 수 없는 루트, 루트 밖을 가리키는 `..`, 없는 파일, `\` 구분자) 그 작업은 실행하지 않고 실패(`Definition`)로 기록합니다.
* Legacy 여부(복원 생략, 보고서의 Legacy 칸)는 루트 이름이 아니라 대상 파일에서 가장 가까운 csproj가 비 SDK 형식인지로 정합니다(`cs` 작업만).

## 스키마

| 필드 | 형식 | 설명 |
| :--- | :--- | :--- |
| `id` | 문자열 | 파일 이름(확장자 제외)과 같음. 형식 `^(cs-(modern\|legacy)\|sql)-\d{3}$` |
| `kind` | `cs` \| `sql` | 작업 종류 |
| `category` | `mvc` \| `adonet` \| `dapper` (선택) | Legacy 작업의 앱 종류(MVC 컨트롤러·서비스 / ADO.NET 배치 / Dapper 리포지토리). Legacy 작업에만 두고 구성 테스트가 셉니다. 합격 판정의 범주(cs-legacy·cs-modern·sql)는 id 접두사입니다 |
| `file` | 문자열 | `<루트 이름>/<루트 안 경로>`, 구분자는 `/`만 (예: `Modern/Shop.Api/Services/OrderService.cs`). 루트는 `suite.json`의 `roots` |
| `method` | 문자열 (선택) | `cs`: 대상 메서드 이름. `method`와 `line` 중 하나만 |
| `line` | 정수 (선택) | `cs`: 대상 멤버 안의 줄 번호(1부터). IDE External Tools 사용을 흉내 냄 |
| `object` | 문자열 (선택) | `sql`: 대상 객체 이름. 파일에 객체가 여러 개면 필수 |
| `request` | 문자열 | 한국어 요청문 (사용자가 입력하는 그대로) |
| `judge.build` | bool | `cs` 전용. 빌드 통과 필요 |
| `judge.sqlCompile` | bool | `sql` 전용. NOEXEC 컴파일 통과 필요 |
| `judge.tests` | 문자열 목록 (선택) | 수정 뒤 통과해야 하는 테스트 전체 이름. 판정 프로젝트(`judges/`)와 코퍼스 테스트에서 찾음. **원본에서는 실패해야 함** |
| `judge.failingTests` | 문자열 목록 (선택) | 수정 뒤 **실패해야** 하는 코퍼스 테스트 전체 이름(수정 전 동작을 고정한 `*_CurrentlyCancels` 등). 코퍼스 테스트 프로젝트에서만 찾음. **원본에서는 통과해야 함** |
| `judge.mustContain` | 문자열 목록 (선택) | 수정된 대상에 있어야 하는 문자열. **원본 대상에는 없어야 함** |
| `judge.mustAdd` | 문자열 목록 (선택) | 수정된 대상에서 원본보다 **더 많이** 나와야 하는 문자열 (원본에 이미 있는 문자열을 새로 추가했는지 확인) |
| `judge.mustNotContain` | 문자열 목록 (선택) | 수정된 대상에 없어야 하는 문자열. **원본 대상에는 있어야 함** |
| `judge.mustNotAdd` | 문자열 목록 (선택) | 수정된 대상에서 원본보다 **더 많이** 나오면 실패하는 문자열(프로젝트 규칙이 금지한 함수를 새로 쓰지 않았는지 확인). 혼자서는 내용 판정으로 치지 않습니다 |

## 공통 판정 규칙
* **대상(멤버 또는 SQL 객체 배치)이 원본과 같으면 실패**입니다. 아무것도 고치지 않은 패치가 성공으로 집계되지 않게 하기 위함입니다.
* 대상 범위: `method` 지정이면 그 멤버, `line` 지정이면 그 줄을 감싸는 멤버, `sql`이면 대상 객체 배치.
* **공백 차이뿐인 수정(줄바꿈·들여쓰기만 바뀐 재포맷)도 "원본과 같음"으로 셉니다**(공백 연속을 한 칸으로 접어 비교).
* 정의의 `mustContain`·`mustNotContain`이 원본 대상(실행기와 같은 범위) 기준으로 의미가 있는지는 `BenchmarkDefinitionTests.Judges_RejectUnmodifiedTarget`이, sql 대상이 객체 배치 하나인지는 `SqlTasks_TargetIsAnObjectBatch`가 검사합니다.

## 동작 판정 (`tests`·`failingTests`)
* Modern C# 작업에만 둡니다(R1). Legacy·SQL은 문자열 판정을 유지합니다.
* 판정 테스트는 코퍼스 밖 `judges/Modern/`에 있습니다(R2). 원본에서 실패하는 테스트라 코퍼스 CI와 pelly `--test`가 보지 않게 하기 위함입니다.
  * `Shop.Api.JudgeTests`: `Shop.Api`를 참조해 서비스·컨트롤러를 가짜 저장소·서비스로 실행합니다.
  * `Shop.Data.JudgeTests`: 데이터 접근 코드(`OrderRepository`, `Shop.Jobs`의 `DailySettlementJob`)를 소스로 컴파일해 가짜 Dapper·`SqlConnection`에 묶습니다. DB 없이 보낸 SQL·매개변수와 결과를 봅니다(가짜 DB가 `ORDER BY`를 행에 적용).
  * 규칙·현재 테스트 표는 `judges/Modern/Shop.Api.JudgeTests/README.md`에 있습니다.
* 종료 0 작업에 `tests`나 `failingTests`가 있으면 독립 검증이 다음을 합니다.
  1. 판정 프로젝트(스위트의 `judges/<루트 이름>/`)를 `work/<id>/judges/<루트 이름>/`으로 복사합니다.
  2. `-p:JudgeTargetRoot=<work/<id>/Modern>`으로 복원·빌드합니다.
  3. 테스트 프로젝트마다 한 번 실행하고 TRX로 테스트별 결과를 읽습니다.
* `tests`는 모두 Passed, `failingTests`는 모두 Failed여야 합니다. **이름이 결과(TRX)에 없으면 실패**로 봅니다.
* 판정 프로젝트 빌드 오류, 시간 초과, TRX 없이 끝난 실행은 실패입니다(수정한 코드가 원인일 수 있음).
* 판정 도구 오류(판정 프로젝트 복원 실패, 테스트 프로젝트 없음, 테스트 호스트를 띄우지 못함)는 독립 검증 **생략**(`판정 도구 오류: …`)입니다. 일시적인 NuGet 문제가 검증 불변 조건 위반으로 세지지 않게 하기 위함입니다. 이때 작업은 성공으로 세지 않습니다(사유 `동작 판정 생략`, 실패 유형 `JudgeTooling`). KPI 표의 최종 작업 성공률 칸에 「판정 도구 오류로 미판정 N건」으로 나옵니다.
* 동작 판정 실패(판정 테스트·`failingTests`)는 **모델 실패**(실패 유형 `Judge`)이고, 검증 불변 조건 위반이 아닙니다. 불변 조건은 독립 빌드(C#)·NOEXEC 컴파일(SQL) 결과로만 판정합니다(아래 「실행」).
* 문자열 판정·빌드 판정과 함께 적용합니다. **동작 판정이 있으면, 다른 올바른 수정을 떨어뜨릴 수 있는 문자열 판정은 두지 않습니다.** 테스트가 못 보는 것을 확인할 때만 남깁니다.
  * 예: cs-modern-001의 `mustAdd: OrderStatus.Shipped`는 올바른 수정(Shipped 예외를 넣고, 죽은 환불 조건의 `|| Shipped`를 지움)을 실패시켜 뺐습니다(2026-10-18 실측).
  * cs-modern-002의 `mustAdd: Math.Round`·`mustNotContain: Math.Floor`도 뺐습니다. `Math.Floor(refund / 10m + 0.5m) * 10m`도 올바른 반올림입니다.
  * 그래서 지금 Modern 12건은 모두 빌드 + 동작 판정만 씁니다.
* 통과하면 보고서의 독립 검증 칸에 실행한 판정 테스트 수가 나옵니다.
* `JudgeTestDefinitionTests`가 원본 코퍼스 복사본에서 `tests`는 실패하고 `failingTests`는 통과하는지 검사합니다.

## 주의
* 코퍼스의 `*_CurrentlyCancels` 테스트는 **수정 전** 동작을 고정한 것입니다. 해당 벤치마크 작업이 성공하면 이 테스트는 실패하는 것이 정상이므로 `judge.tests`가 아니라 `judge.failingTests`에 넣습니다.
* 레거시 작업(`cs-legacy-*`)의 빌드 판정은 Windows에서만 가능합니다. Mac에서는 "빌드 판정 생략" 모드로 실행합니다(설계 §10.6).

## 실행
```bash
export PELLY_SQL_CONNECTION="Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=<sa 비밀번호>;TrustServerCertificate=True"
dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json>            # 50건 전체
dotnet run --project eval/PellyCoder.Eval -- --tasks sql --config <pelly.json> # 일부 (id 접두사·글롭, --kind cs|sql)
dotnet run --project eval/PellyCoder.Eval -- --suite <스위트 폴더> --config <pelly.json> --keep-work failed   # 현장 스위트
```
* `--no-rules`: 작업 복사본에서 `AGENTS.md`(대소문자 무시, 모든 깊이)를 지우고 실행합니다. 같은 스위트를 프로젝트 규칙 없이 재는 대조 실행이며, 요약의 실행 조건에 `rulesRemoved: true`로 남습니다.
* `--keep-work <all|failed|none>`: 작업 복사본(`work/<id>/`) 보존 범위. 기본 `all`, 현장 실제 코드는 `failed`를 권장합니다. `results.json` 행은 그대로입니다.
* 결과 폴더: `<스위트 resultsDir>/<yyyyMMdd-HHmmss>/`, 내장 스위트는 `eval/results/…`(`--out`으로 변경, git 추적 제외). 비어 있지 않은 `--out`은 거절합니다(종료 4). 이전 결과에 이어 쓰면 남은 작업 폴더의 작업이 「생략」으로 집계되기 때문입니다
  * `report.md`(KPI 표·실패 유형·작업별 결과), `results.json`, `work/<id>/`(작업별 스위트 루트 복사본과 `.pelly/runs` 로그)
* 검증 불변 조건(종료 0 = 검증 통과, PRD §2.2)은 실행기가 결과를 pelly와 별개로 다시 **빌드(C#)·NOEXEC 컴파일(SQL)** 해 판정합니다. 동작 판정·문자열 판정의 실패는 위반이 아니라 모델 실패(`Judge`)입니다. **분모는 독립 빌드·컴파일이 `Passed`/`Failed`인 종료 0 실행만**이고, 생략된 실행(Windows가 아닌 환경의 Legacy, 검증기 예외)은 "독립 검증 생략 N건"으로 따로 표시되며 판정된 것이 없으면 "판정 불가"입니다.
* 작업 정의 오류: id 형식·필드 조합이 `validate`의 정의 규칙을 어기거나 대상을 해석하지 못한(프로젝트 파일을 읽을 수 없음 포함) 작업은 실행하지 않고 실패(`Definition`)로 기록합니다. 작업 YAML을 읽을 수 없으면 실행하지 않고 종료 4입니다.
* 복원: SDK 형식 작업은 루트 맨 위의 `*.sln` 중 이름순(서수) 첫 파일을 `dotnet restore`합니다(`validate --deep`도 같은 파일). 실패하면 `환경 오류(복원 실패)`로 생략하고, 상세에 복원 출력의 마지막 5줄을 붙입니다.
* 종료 코드: 0 실행 완료 · 1 검증 불변 조건 위반 · 2 중단 · 4 설정 오류(작업 파일·`--out` 포함) 또는 LLM 서버 연결 불가
* **반복 실행·비교 (M3b):** `--repeat N`은 `run-1/`…`run-N/`과 상위 `summary.md`·`summary.json`(`formatVersion` 2, 1도 읽음. 실행 조건에 Ollama 버전·모델 digest 포함)을 만듭니다. `compare <기준선 summary.json> <후보 summary.json>`은 R4 채택 규칙(평균 +1.0건 이상, 범주별 −0.5건 넘게 감소 없음, 위반 0건, 시간 중위값 1.5배 이하)으로 판정합니다(종료 0 채택 · 3 거절 · 4 판정 불가. 모델 digest나 Ollama 버전이 다른 요약은 판정 불가). 기준선은 `docs/m3b-baseline.summary.json`입니다.
* **하위 명령 (M4 준비):** `validate [--suite] [--config] [--deep]`는 실행 전에 정의를 점검합니다(종료 0 오류 없음 · 4 오류 있음, `--deep`은 판정 시험을 원본에서 실제로 실행). `record --out <파일> [--gguf-sha256 <hex>]`는 버전 기록, `export <결과 폴더> --out <폴더>`는 비식별 반출본을 씁니다(id가 정해진 형식이 아니면 반출하지 않고 종료 4). 숨은 `--offline-selftest`는 `pelly doctor --offline-selftest`와 같은 내장 구성 요소 점검입니다(배포본 확인용). 전체 도움말은 `-- --help`입니다.
* **`sql` 작업에는 SQL Server가 필요합니다.**
  * 연결은 환경 변수 `PELLY_SQL_CONNECTION`(비밀번호가 있는 연결은 환경 변수로만).
  * `--config`의 pelly.json에 허용 목록을 둡니다: `{"sql":{"allowedHosts":["localhost"],"allowedDatabases":["PellyCorpus"]}}`
  * 연결이 없거나 허용 목록에 없으면 `sql` 작업은 건너뜁니다.
  * DB는 실행 전에 `eval/corpus/Sql/deploy.sh`로 코퍼스와 맞춰 둡니다. 독립 검증은 원본 객체가 오류 없이 컴파일된다고 가정합니다(절대 기준).
  * 독립 검증은 pelly의 참조 수집기·실행기를 그대로 재사용하므로 둘의 사각지대(3·4부 이름, 동적 SQL, `#temp`, 별칭과 같은 이름의 테이블)를 공유합니다. 또 절대 기준이라 코퍼스 원본이 오류 없이 컴파일된다고 가정합니다.
* 첫 측정 기록: `docs/m2b-benchmark.md`

## apply 벤치마크 (`pelly-eval apply`, V2-1c)
여러 작업 명세를 `pelly apply`로 끝까지 돌려 **명세 완료율**(PRD §12.6)을 잽니다. 사례는 `apply/<id>.md` 12건(승인된 S6 정답, 출처·변환 규칙은 `apply/README.md`)과, 수용 기준 테스트(`test:`·`breaks:`)를 단 테스트 우선 사례 `apply-test-first/t1`~`t8`(V2-2c, 형식·구성·승인은 `apply-test-first/README.md`)입니다.
```bash
dotnet run --project eval/PellyCoder.Eval -- apply --config <pelly.json>                       # 12건 전체
dotnet run --project eval/PellyCoder.Eval -- apply --cases a1,d* --repeat 3 --out <폴더> --config <pelly.json>
dotnet run --project eval/PellyCoder.Eval -- apply --set test-first --config <pelly.json>      # 테스트 우선 8건
```
* 옵션: `--set <default|test-first|all>`(기본 `default` = 12건만, `test-first` = 테스트 우선 8건, `all` = 20건), `--cases <패턴>`(사례 id 접두사·글롭, 쉼표로 여러 개, `--set`이 고른 폴더 안에서), `--repeat <N>`(사례마다 N회), `--out <폴더>`(기본 `eval/results/apply-<yyyyMMdd-HHmmss>/`, 비어 있지 않으면 거절), `--config <pelly.json>`.
* 테스트 우선 사례는 폴더(`spec.md`, `files/`, `expected.json`, 선택 `pelly.json`)입니다. 작업 복사본을 만든 뒤 `files/`를 `corpus/` 위에 덮어 넣고, 사례의 `pelly.json` 조각(`test.command`만)은 그 사례 실행의 설정에만 합칩니다(t8의 레거시 `{trx}` 명령). 승인 파일 `apply-test-first/APPROVED.json`(`{ "approvedBy", "approvedAt", "cases": [id…] }`)은 사람이 만들고, 실행 집합에 승인되지 않은 사례가 있으면 요약 제목에 「예비(사람 승인 전)」가 붙습니다.
* 사례·회차마다 다음 순서로 돕니다.
  1. 건너뛰기: 범위에 `Legacy`가 있는데 Windows가 아니면 `Windows 전용`, `Sql`이 있는데 SQL 연결이 없거나 허용 목록 밖이면 `SQL 연결 없음`.
  2. 작업 복사본 `work/<id>-<n>/`: 스위트 `supportFiles`(`global.json` 등)를 맨 위에, `eval/corpus`를 `corpus/`에(기본 제외 폴더 빼고) 복사합니다. `corpus/`에 빈 `.git` 폴더를 만들어 루트마다 있는 `.sln`과 상관없이 코퍼스 전체가 작업 공간 하나가 되게 하고, 명세를 `corpus/changes/<id>.md`로 씁니다.
  3. 범위에 `Modern`이 있으면 `Modern/Modern.sln`을 복원합니다(실패하면 「복원 실패」로 건너뜀). Legacy는 `run`과 같은 전제(Windows, 패키지 폴더 준비)입니다.
  4. `pelly apply`와 같은 조립(`SpecRunnerFactory`, 시작 영향 탐색 포함)으로 `--yes`, `--continue` 없이 실행합니다.
  5. 종료 0이면 **독립 검증**: 원본 코퍼스와 바이트가 다른 `.cs` 파일의 프로젝트와 그 의존 프로젝트를 다시 빌드합니다(pelly 기록이 아니라 파일 비교로 찾음). 컴파일러 오류가 있으면 검증 불변 조건 위반(기준 빌드와 비교하지 않으므로 **원본 코퍼스가 오류 없이 컴파일된다고 가정**합니다. CI가 Modern을 `-warnaserror`로, Legacy를 Windows에서 빌드해 지킵니다), 빌드가 판정할 수 없으면 「독립 검증 불가」, 바뀐 `.cs`가 없으면 「C# 변경 없음」입니다.
* 산출물: `apply-results.json`(모든 사례 실행), `apply-summary.md`(명세 완료율·작업 3~8개 명세 완료율·그룹 빌드 통과율·독립 빌드 집계·건너뛴 사례·사례별 표), `work/<id>-<n>/corpus/.pelly/runs/`(apply·작업 실행 기록).
  * 수용 기준이 있는 실행이 있으면 「테스트 우선」 표(수용 기준 충족률·RED 확인 경고율·테스트로 검증되지 않은 수용 기준·breaks로 뺀 테스트, `pelly report`와 같은 계산, 그리고 테스트 판정 불일치·독립 테스트 검증 불가·건너뜀 수)와 사례별 수용 기준 표(기대 vs 실제 RED/결과, 독립 테스트)가 더해집니다. **독립 테스트**: 종료 0인 apply에서 pelly가 `Met`이라 한 수용 기준의 `test:`를 pelly 밖에서 다시 빌드·실행합니다. 통과하지 않으면 「테스트 판정 불일치」로 검증 불변 조건 위반(종료 1)이고, 판정할 수 없으면(레거시 off Windows는 「건너뜀」, 그 밖은 「검증 불가」) 따로 셉니다.
  * 완료율·그룹 빌드 통과율은 각 작업 복사본의 `apply.json`을 `pelly report`와 같은 계산(`ApplyReport.Build`)으로 셉니다. 완료 = 종료 0이고 명세가 보관됨. 건너뛰지도 실행기 오류도 아닌데 `apply.json`을 남기지 않은 실행(명세 검사 오류·거절로 시작 전에 끝남 등)은 미완료로 분모에 더하고(작업 3~8개 줄은 그 사례의 작업 수로), 요약 줄에 「apply.json 없는 실행 N건을 미완료로 포함」으로 적습니다.
* 종료 코드는 `run`과 같습니다: 0 실행 완료 · 1 검증 불변 조건 위반(종료 0인데 독립 빌드에 컴파일러 오류, 또는 테스트 판정 불일치) · 2 중단 · 4 설정 오류(사례 명세 오류·`--out` 포함), LLM 서버 연결 불가, 또는 실행기 오류(`error`: 작업 복사본을 못 만듦·실행기 예외) 사례가 있음. 여럿이면 2 → 1 → 4 순으로 앞의 것을 냅니다.
* 현장 모델 실측(`--repeat N`)과 레거시 사례(a3 등)의 Windows 실측은 사람이 하고, 결과는 `deploy/field-results.md`에 옮깁니다.

## 현재 구성 (M3b, 50건)
C# 30건(Legacy 18, Modern 12) + SQL 20건. 시그니처를 바꾸는 요청(async 전환 등)은 넣지 않습니다(MemberShapeGuard가 막는 의도된 동작). SQL 매개변수 추가는 허용합니다.
모든 작업에 내용 판정(`mustContain`·`mustAdd`·`mustNotContain`) 또는 동작 판정(`tests`·`failingTests`)이 하나 이상 있습니다(`AllTasks_HaveContentCriterion`) — 빌드·컴파일만으로는 의미 없는 수정도 통과하기 때문입니다. `tests`는 원본에서 실패해야 하므로 같은 역할을 합니다.
Modern 12건은 모두 동작 판정을 가집니다(R1). 구성은 `Composition_Is30Cs20Sql_With18Legacy`가 검사합니다.

### Legacy 범주 (`category`)
| 범주 | 건수 | 대상 |
| :--- | :--- | :--- |
| `mvc` (MVC 컨트롤러·서비스) | 8 | Shop.Web `OrderController`, `OrderService` |
| `adonet` (ADO.NET 배치) | 5 | Shop.Batch `SettlementBatch` |
| `dapper` (Dapper 리포지토리) | 5 | Shop.Data `OrderDao` (Dapper 1.50) |

### 작업 목록
| id | 대상 | 지정 방식 | 요청 종류 | 판정 |
| :--- | :--- | :--- | :--- | :--- |
| cs-legacy-001 | Shop.Web `OrderService.Cancel` (CP949) | method | 로직 수정 | 문자열 |
| cs-legacy-002 | Shop.Batch `SettlementBatch.Execute` (CP949, ADO.NET) | line | null 처리 | 문자열 |
| cs-legacy-003 | Shop.Data `OrderDao.SearchOrders` (Dapper 1.50) | method | Dapper 쿼리 변경 | 문자열 |
| cs-legacy-004 | Shop.Web `OrderController.Cancel` (MVC) | method | 검증 추가 | 문자열 |
| cs-legacy-005 | Shop.Web `OrderController.Detail` (MVC) | line | 검증 추가 | 문자열 |
| cs-legacy-006 | Shop.Web `OrderService.Cancel` (CP949) | line | 로직 수정 | 문자열 |
| cs-legacy-007 | Shop.Batch `SettlementBatch.Execute` (CP949, ADO.NET) | method | 쿼리 조건 | 문자열 |
| cs-legacy-008 | Shop.Batch `SettlementBatch.BuildReport` (CP949, 긴 멤버) | method | 로직 수정 | 문자열 |
| cs-legacy-009 | Shop.Data `OrderDao.GetOrder` (Dapper 1.50) | method | Dapper 쿼리 변경 | 문자열 |
| cs-legacy-010 | Shop.Data `OrderDao.UpdateStatus` (Dapper 1.50) | method | Dapper 쿼리 변경 | 문자열 |
| cs-legacy-011 | Shop.Web `OrderService.GetOrder` (CP949) | method | 검증 추가 | 문자열 |
| cs-legacy-012 | Shop.Web `OrderService.Cancel` (CP949) | line | 로그 추가 | 문자열 |
| cs-legacy-013 | Shop.Web `OrderController.Cancel` (MVC) | method | 예외 처리 | 문자열 |
| cs-legacy-014 | Shop.Web `OrderController.Detail` (MVC) | method | 로직 수정 | 문자열 |
| cs-legacy-015 | Shop.Batch `SettlementBatch.Execute` (CP949, ADO.NET) | line | 명령 설정 | 문자열 |
| cs-legacy-016 | Shop.Batch `SettlementBatch.BuildReport` (CP949, 긴 멤버) | method | 정렬 변경 | 문자열 |
| cs-legacy-017 | Shop.Data `OrderDao.AddHistory` (Dapper 1.50) | method | null 처리 | 문자열 |
| cs-legacy-018 | Shop.Data `OrderDao.SearchOrders` (Dapper 1.50) | line | Dapper 쿼리 변경 | 문자열 |
| cs-modern-001 | Shop.Api `OrderService.CancelAsync` | method | 로직 수정 | 동작(+ failingTests) |
| cs-modern-002 | Shop.Api `OrderService.CalculateRefundAsync` | method | 로직 수정(반올림) | 동작 |
| cs-modern-003 | Shop.Api `OrderRepository.GetItemsAsync` | method | Dapper 쿼리 변경(정렬) | 동작 |
| cs-modern-004 | Shop.Api `OrderRepository.SearchAsync` | line | null·공백 처리 | 동작 |
| cs-modern-005 | Shop.Api `OrdersController.Get` | method | 검증 추가(경계값) | 동작 |
| cs-modern-006 | Shop.Api `OrdersController.Search` | method | 검증 추가(경계값) | 동작 |
| cs-modern-007 | Shop.Api `OrderService.SearchAsync` | method | null 처리 | 동작 |
| cs-modern-008 | Shop.Jobs `DailySettlementJob.Run` | method | 검증 추가(경계값) | 동작 |
| cs-modern-009 | Shop.Api `OrdersController.Cancel` | line | 예외 처리 | 동작 |
| cs-modern-010 | Shop.Api `OrderService.CancelAsync` | method | 검증 추가(경계값) | 동작 |
| cs-modern-011 | Shop.Api `OrderRepository.SearchAsync` | method | Dapper 쿼리 매개변수 | 동작 |
| cs-modern-012 | Shop.Api `OrderService.SearchAsync` | method | 조건 결합 검증 | 동작 |
| sql-001 | `usp_CancelOrder` (SSMS 스크립트 형태) | 파일 | 조건 추가 | 문자열 |
| sql-002 | `usp_SearchOrders` | 파일 | 매개변수·조건 추가 | 문자열 |
| sql-003 | `fn_OrderTotal` (함수) | 파일 | 조건 추가 | 문자열 |
| sql-004 | `Settlement_All.sql`의 `usp_SettleMonthly` (한 파일 여러 객체) | object | 컬럼 추가 | 문자열 |
| sql-005 | `usp_SearchOrdersDynamic` (동적 SQL) | 파일 | 로직 수정 | 문자열 |
| sql-006 | `usp_SearchOrders` | 파일 | 조건 추가 | 문자열 |
| sql-007 | `usp_BuildSettlement` (#temp 테이블) | 파일 | 조건 추가 | 문자열 |
| sql-008 | `usp_GetOrderDetail` (결과 집합 3개) | 파일 | 컬럼 추가 | 문자열 |
| sql-009 | `usp_SearchOrders` | 파일 | 컬럼 추가 | 문자열 |
| sql-010 | `Settlement_All.sql`의 `usp_SettleMonthly` | object | JOIN 변경 | 문자열 |
| sql-011 | `vw_ShippedOrders` (뷰) | 파일 | 컬럼 추가 | 문자열 |
| sql-012 | `Settlement_All.sql`의 `usp_SettleDaily` | object | 조건 추가 | 문자열 |
| sql-013 | `usp_GetOrderDetail` (결과 집합 3개) | 파일 | 컬럼 추가 | 문자열 |
| sql-014 | `Settlement_All.sql`의 `usp_SettleMonthly` | object | 매개변수·조건 추가 | 문자열 |
| sql-015 | `usp_SearchOrdersDynamic` (동적 SQL) | 파일 | 매개변수 추가(sp_executesql) | 문자열 |
| sql-016 | `vw_ShippedOrders` (뷰) | 파일 | JOIN 변경 | 문자열 |
| sql-017 | `Order_Batch.sql`의 `usp_ShipOrder` (TRY/CATCH, 트랜잭션) | object | 검증 추가 | 문자열 |
| sql-018 | `Order_Batch.sql`의 `usp_ExpirePendingOrders` (커서) | object | 이력 추가 | 문자열 |
| sql-019 | `fn_CustomerOrders` (인라인 테이블 함수) | 파일 | 조건 추가 | 문자열 |
| sql-020 | `usp_SetSellerCommission` | 파일 | 검증 추가 | 문자열 |

M3b에서 코퍼스에 더한 것: SQL 객체 넷(`Order_Batch.sql`의 `usp_ShipOrder`·`usp_ExpirePendingOrders`, `fn_CustomerOrders`, `usp_SetSellerCommission`, `deploy.sh` 목록에 추가). C# 코퍼스는 바꾸지 않았습니다.
