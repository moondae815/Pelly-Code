# Pelly-Coder CLI

폐쇄망 로컬 LLM(`Qwen3.6-35B-A3B`, Ollama)으로 C# 메서드와 SQL 저장 프로시저를 수정하고, 컴파일러·DB 엔진으로 검증하는 코딩 CLI입니다.

## 문서
- [PRD](docs/PRD.md)
- [v1 설계](docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md)
- [구현 계획](docs/superpowers/plans/)

## 빌드·테스트
```bash
dotnet build -warnaserror
dotnet test
dotnet run --project src/PellyCoder.Cli -- --version
```

## 사용법

### `pelly cs` — C# 멤버 하나 수정
```bash
pelly cs <파일> ["<요청문>"] [--class <클래스>] [--method <메서드>] [--line N] [--yes] [--dry-run] \
  [--max-retries N] [--strict-baseline] [--warn-as-error] \
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

예:
```bash
pelly cs Shop.Api/Services/OrderService.cs "배송 중인 주문은 취소할 수 없게 해줘" --method CancelAsync
pelly cs Shop.Api/Services/OrderService.cs "null 검사 추가" --line 42
pelly cs Shop.Api/Services/OrderService.cs        # 요청문을 대화형으로 입력
```

### `pelly sql` — 저장 프로시저·함수·뷰 하나 수정
```bash
pelly sql <파일.sql> ["<요청문>"] [--object <이름>] [--line N] [--yes] [--dry-run] \
  [--max-retries N] [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
```

* 파일은 SSMS가 스크립트한 형태(`SET ANSI_NULLS ON` / `GO` / `CREATE PROCEDURE …` / `GO`)나 여러 객체가 든 파일이어도 됩니다. 수정할 수 있는 것은 **대상 객체 배치 하나**이고, 같은 파일의 다른 배치가 바뀌면 거부됩니다.
* **대상 지정:** 파일에 `CREATE`/`ALTER PROCEDURE|FUNCTION|VIEW`가 하나면 자동, 여럿이면 `--object dbo.이름` 또는 `--line N`(그 줄을 포함하는 정의)으로 지정합니다. 지정이 없으면 대화형은 선택 메뉴, 비대화형은 종료 코드 3입니다. `--line`과 `--object`는 함께 쓸 수 없습니다.
* 검증은 **DB 서버가 합니다**: 참조 객체가 존재하는지(`OBJECT_ID`)와 `SET NOEXEC ON`으로 대상 배치를 컴파일해 새 오류가 없는지 봅니다(트랜잭션 안에서 실행하고 롤백하며, 객체는 실제로 바뀌지 않습니다). 원본에 이미 있던 오류는 세지 않습니다. 서버 연결이 필수이고, 연결할 수 없으면 종료 코드 4입니다.
* 동적 SQL 안의 문장, `#임시테이블`의 컬럼, 다른 DB·연결된 서버(3·4부 이름) 객체는 검증하지 못하며 알림만 보여줍니다.
* 파일에 쓰기 전에 **안전 가드**가 검사합니다: 대상 밖 배치 변경, 최상위 문장 수 변경, 금지 구문(`GRANT`/`REVOKE`/`DENY`, `xp_cmdshell`, `OPENROWSET`/`OPENDATASOURCE`, `SET NOEXEC`·`PARSEONLY`·`FMTONLY`, `ALTER DATABASE`류, 로그인 문장, `SHUTDOWN`, `RECONFIGURE`)은 거부되어 재시도되고, 끝내 못 고치면 종료 코드 5입니다. **원본 대상에 이미 금지 구문이 있으면** 모델을 부르기 전에 종료 코드 5로 멈춥니다(고칠 수 없는 프로시저).
* 구문 오류가 있는 SQL을 만들면 파일에 쓰기 전에 걸러 재시도합니다(소진하면 종료 코드 1).

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
* **Windows 미확인:** SQL 경로는 Windows에서 확인하지 않았습니다(CI의 SQL 테스트는 Linux에서만 돕니다). 코퍼스 배포 스크립트 `eval/corpus/Sql/deploy.sh`는 bash가 필요하므로 Windows에서는 WSL이나 Git Bash로 실행하십시오.
* 허용 목록은 `Data Source`의 `.`, `(local)`, `np:` 형태를 정규화하지 않습니다. 허용 목록과 일치하지 않으면 접속을 거부하므로(fail closed) 호스트 이름을 쓰십시오.
* 연결 문자열에 서버나 데이터베이스 키가 둘 이상 있거나 `Failover Partner`가 있으면 거부합니다(종료 코드 5).
* **검증 계정 권한:** DB 수준 **`VIEW DEFINITION`** 하나면 됩니다(`ALTER`·`SHOWPLAN` 불필요). 없으면 객체 존재 검사와 스키마 요약이 동작하지 않으므로 종료 코드 4와 안내 메시지로 멈춥니다. `pelly doctor`가 접속·허용 목록·권한을 점검합니다.

### `pelly doctor` — 환경 점검
Ollama 서버 접속, 모델 존재·로드 여부, chat template, 토크나이저, .NET SDK(`dotnet --version`), 레거시 프로젝트용 MSBuild(Windows에서만), SQL 연결(허용 목록·`VIEW DEFINITION`, 연결 문자열이 설정된 경우)을 점검하고 확인할 항목을 알려 줍니다.

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

실제 모델 실행 기록은 [docs/m1-e2e.md](docs/m1-e2e.md)(C# 최소 경로), [docs/m2a-e2e.md](docs/m2a-e2e.md)(빌드 검증·교정 루프), [docs/m2b-e2e.md](docs/m2b-e2e.md)(SQL 경로)에 있습니다.

## 벤치마크 실행
작업 30건(C# 18 + SQL 12)을 실제 모델로 돌려 KPI 표를 만드는 실행기 `eval/PellyCoder.Eval`이 있습니다. 작업 정의와 판정 규칙은 [eval/benchmark/README.md](eval/benchmark/README.md), 첫 측정은 [docs/m2b-benchmark.md](docs/m2b-benchmark.md)입니다.

```bash
# SQL 작업에는 SQL Server와 코퍼스 배포가 필요합니다 (eval/corpus/Sql/deploy.sh). 비밀번호가 있는 연결은 환경 변수로만.
export PELLY_SQL_CONNECTION="Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=<비밀번호>;TrustServerCertificate=True"
# 허용 목록·모델은 --config 파일에 둡니다: {"llm":{"model":"<태그>"},"sql":{"allowedHosts":["localhost"],"allowedDatabases":["PellyCorpus"]}}
dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json>             # 30건 전체
dotnet run --project eval/PellyCoder.Eval -- --tasks sql --config <pelly.json>  # 일부 (id 접두사·글롭, --kind cs|sql)
```
* 결과는 `eval/results/<yyyyMMdd-HHmmss>/`(`--out`으로 변경, git 추적 제외)에 `report.md`(KPI 표·실패 유형·작업별 결과), `results.json`, `work/<id>/`로 남습니다. 코퍼스(`eval/corpus`)는 읽기 전용이며 작업마다 임시 복사본에서 실행합니다.
* **SQL 작업은 허용 목록이 비어 있거나 연결이 없으면 건너뜁니다**(실행기가 허용 목록을 스스로 넓히지 않습니다).
* Legacy 작업은 Windows가 아니면 빌드 판정을 생략합니다. 검증 불변 조건(KPI)의 분모는 **독립 검증이 실제로 판정한 종료 0 실행**만이고, 생략된 실행은 따로 표시됩니다.
* 종료 코드: 0 실행 완료 · 1 검증 불변 조건 위반 · 2 중단 · 4 설정 오류 또는 LLM 서버 연결 불가.

## 통합 테스트 환경
환경이 필요한 통합 테스트는 환경 변수가 있을 때만 실행됩니다.

| 분류 | 환경 변수 |
| :--- | :--- |
| SQL Server | `PELLY_TEST_SQL` (연결 문자열) |
| Ollama | `PELLY_TEST_OLLAMA` (엔드포인트), `PELLY_TEST_MODEL` (모델 태그) |
| Windows | Windows에서 자동 실행 |
