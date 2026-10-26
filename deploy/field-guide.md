# 현장 절차 (폐쇄망 기준선 측정)

폐쇄망 Windows PC에서 pelly의 기준선을 재고, 설정을 튜닝하고, 결과를 반출하는 순서입니다.
명령은 `cmd`나 PowerShell에서 실행합니다. `<…>`는 자기 값으로 바꿉니다. `<후보.json>`은 튜닝 후보의 설정 파일입니다.
이 문서의 명령은 Windows에서 실행해 보지 못했습니다(Windows 미확인). 막히면 막힌 줄과 출력을 `field-results.md`의 메모에 적으십시오.

반입물은 zip 둘입니다.

| zip | 내용 |
| :--- | :--- |
| `pelly-<버전>-win-x64.zip` | `pelly.exe`와 그 옆 GUI DLL 셋(`av_libglesv2.dll`, `libHarfBuzzSharp.dll`, `libSkiaSharp.dll`)·`BuildHost-net472\`·`BuildHost-netcore\` 폴더(함께 두십시오), 설치 문서. 이 문서에서 「pelly zip을 푼 폴더의 `docs\install.md`」처럼 부릅니다 |
| 현장 키트 zip | `pelly-eval.exe`와 그 옆 `BuildHost-net472\`·`BuildHost-netcore\` 폴더(함께 두십시오), 이 문서들(`docs\field-*.md`), `templates\` |

이 문서에서 실행기를 `pelly-eval`이라고 부릅니다. 파일 이름은 `pelly-eval.exe`입니다.
`pelly-eval --help`가 옵션과 종료 코드를 보여 줍니다.

`pelly-eval`을 부르는 방법은 셋 중 하나입니다(Windows 미확인).

* `cmd`: 현장 키트 zip을 푼 폴더로 이동(`cd /d <키트 폴더>`)한 뒤 이 문서의 명령을 그대로 씁니다.
* PowerShell: 현재 폴더의 실행 파일은 이름만으로 부를 수 없습니다. 키트 폴더에서 `.\pelly-eval`처럼 앞에 `.\`를 붙입니다(예: `.\pelly-eval --help`).
* 어느 폴더에서나 쓰려면 키트 폴더를 `PATH`에 넣습니다. `cmd`에서는 `set PATH=%PATH%;<키트 폴더>`(그 창에서만 유효)입니다.

## 1. zip의 SHA-256 확인

zip 두 개를 풀기 전에, zip과 다른 경로로 받은 값과 비교합니다. 방법은 pelly zip을 푼 폴더의 `docs\install.md` 2절과 같습니다.

```powershell
Get-FileHash .\<zip 이름>.zip -Algorithm SHA256
```

기대: 받아 둔 값과 같습니다(대소문자 무시). 다르면 풀지 말고 다시 반입하십시오.
풀고 난 뒤 `SHA256SUMS.txt`로 파일별 확인도 합니다(같은 문서 2절).

## 2. 설치와 `pelly doctor`

pelly zip을 푼 폴더의 `docs\install.md` 1~4절대로 설치하고 설정 파일을 만듭니다. 이 문서에서 설정 파일 경로를 `<pelly.json>`이라고 부릅니다.

**예전 설정 파일을 다시 쓰는 경우:** `budget.system`이 600으로 적혀 있으면 450으로 낮춥니다. 규칙 예산 `budget.rules`(150 토큰)가 시스템 예산에서 나뉘어, 600이면 예산 합계가 `budget.inputTotal`(4096)을 넘어 모든 명령이 종료 코드 4로 멈춥니다. `budget` 블록이 없으면 그대로 둡니다.

SQL 작업을 측정하려면 `doctor` 전에 환경 변수 `PELLY_SQL_CONNECTION`을 설정합니다. 설정하지 않으면 `SQL 연결` 행이 Warn이고 계정이 보이지 않습니다.
레거시 프로젝트가 있으면 `--project`로 그 csproj를 줍니다(타겟팅 팩 행은 이 옵션이 있어야 나옵니다).

```powershell
pelly doctor --config <pelly.json> --project <레거시.csproj>
```

기대: 종료 코드 0. 아래 행을 봅니다.

| 행 | 볼 것 |
| :--- | :--- |
| Ollama 서버 | `Ok`와 서버 버전 |
| 모델 | `Ok`와 `digest <12자>`. 이 12자를 결과 양식에 적습니다 |
| MSBuild (레거시) | 레거시 프로젝트가 있으면 `Ok`. 없으면 레거시 작업은 측정에서 빠집니다 |
| 타겟팅 팩 | 레거시 프로젝트의 .NET Framework 팩이 있어야 합니다 (`--project`를 준 경우에만 나옵니다) |
| SQL 연결 | `Windows 인증 (<계정>)` 또는 `SQL 로그인 (<계정>)`. 접속 계정이 읽기 전용 계정인지 확인합니다 |

Fail이 하나라도 있으면 안내대로 고친 뒤 다시 실행합니다.

현장 키트의 실행 파일도 확인합니다. 서버 없이 내장 구성 요소(토크나이저, 파서, 인코딩, 포함 리소스, SqlClient)만 봅니다.

```cmd
pelly-eval --offline-selftest
```

기대: 종료 코드 0, 여섯 행 모두 `Ok`. 아니면 키트 zip을 다시 반입합니다.

## 3. 버전 기록

서버 관리자에게 모델 GGUF 파일의 SHA-256을 받습니다. 확인 방법은 pelly zip을 푼 폴더의 `docs\server-checklist.md`에 있습니다.
Ollama 모델 digest와 GGUF 해시는 다른 값입니다. 둘 다 적습니다.

`<hex64>`는 64자리 16진수입니다.

```cmd
pelly-eval record --out D:\pelly-field\version-record.json --gguf-sha256 <hex64> --config <pelly.json>
```

기대: 종료 코드 0. `version-record.json`에 Ollama 버전, 모델 digest, 형식, 양자화, GGUF SHA-256이 들어갑니다. 접속 주소는 쓰지 않습니다.
종료 3이면 모델이 서버에 없거나 서버가 버전을 알리지 않은 것입니다. 종료 4는 접속 불가·인자 오류·파일 쓰기 실패입니다.

## 4. 스위트 작성

`field-benchmark.md`를 따라 현장 스위트 폴더를 만듭니다. 이 문서에서 그 폴더를 `<폴더>`라고 부릅니다(예: `D:\pelly-field\suite`).
템플릿은 현장 키트 zip을 푼 폴더의 `templates\suite.json`과 `templates\tasks\*.yaml`입니다. 복사해서 고치십시오.

## 5. 스위트 검사

먼저 정적 검사입니다. LLM과 SQL에 접속하지 않습니다.

```cmd
pelly-eval validate --suite <폴더> --config <pelly.json>
```

기대: 종료 0. 경고는 있어도 됩니다. 종료 4면 오류 목록을 고치고 다시 실행합니다.

다음은 `--deep`입니다. 동작 판정 테스트를 원본에서 실제로 돌립니다. 복원과 빌드가 필요해 느립니다.

```cmd
pelly-eval validate --suite <폴더> --config <pelly.json> --deep
```

기대: 종료 0. 정적 검사에 오류가 있으면 `--deep`은 돌지 않습니다.

## 6. 기준선 측정

같은 작업 집합을 세 번 돌립니다. 현장 실제 코드에서는 실패한 작업의 복사본만 남기는 것을 권장합니다.

```cmd
pelly-eval --suite <폴더> --config <pelly.json> --repeat 3 --keep-work failed --out D:\pelly-field\baseline
```

* 일부만 돌려 보려면 `--tasks cs-modern,sql-00?` 또는 `--kind sql`을 붙입니다. 기준선은 전체를 돌립니다.
* `sql` 작업에는 환경 변수 `PELLY_SQL_CONNECTION`이 필요합니다. 비밀번호가 있는 연결 문자열은 이 환경 변수로만 넘깁니다. 연결이 없거나 연결 대상이 `pelly.json`의 허용 목록(`sql.allowedHosts`, `sql.allowedDatabases`)에 없으면 `sql` 작업은 건너뜁니다.
* 종료 코드: 0 실행 완료 · 1 검증 불변 조건 위반 · 2 중단 · 4 설정 오류 또는 LLM 서버 연결 불가.
* 종료 1이면 중단하고 `report.md`를 먼저 읽으십시오. 이것은 모델 실패가 아니라 시스템 보장 위반입니다.
* `--out` 폴더가 이미 있고 비어 있지 않으면 실행하지 않고 종료 4입니다. 다시 돌릴 때는 새 폴더(예: `baseline-2`)를 주십시오. 이전 결과에 이어 쓰면 작업이 「생략」으로 집계되기 때문입니다.
* `--out`이 파일 경로이거나 읽을 수 없는 폴더여도, 같은 작업 id를 쓰는 작업 파일이 둘 이상이어도 실행하지 않고 종료 4입니다. 메시지에 원인이 나옵니다.
* id 형식이 틀리거나 대상을 해석하지 못한 작업은 실행하지 않고 실패(`Definition`)로 기록됩니다. 5절 `validate`를 먼저 깨끗하게 하십시오.

기대: `D:\pelly-field\baseline\`에 `summary.md`, `summary.json`, `run-1\`~`run-3\`가 생깁니다. 각 `run-<k>\`에 `report.md`와 `results.json`이 있습니다.

## 7. KPI 판정

`summary.md`와 `run-<k>\report.md`의 값을 `field-results.md`에 옮겨 적습니다.
목표는 PRD §2.2의 여섯 지표입니다(양식에 목표가 적혀 있습니다).
최종 작업 성공률의 목표(잠정 85%)를 현장 값으로 확정하는 것(Q6)은 사람이 합니다. 도구가 정하지 않습니다. 확정 값과 결정한 사람·날짜를 양식에 적습니다.
외부 기준(개발 환경 측정)과의 차이도 양식의 표에 적습니다. 차이가 크면 먼저 환경 차이(서버 사양, 모델 파일)를 의심하십시오.

## 8. 설정 튜닝

바꿔 볼 것은 `pelly.json`의 값뿐입니다(샘플링 값, 예산 등). 코드는 바꾸지 않습니다.
예산을 바꿀 때는 `budget.system + budget.rules + budget.request + budget.context + budget.feedback`이 `budget.inputTotal` 이하여야 합니다(기본 450 + 150 + 200 + 3000 + 296 = 4096).
후보 설정 파일을 따로 만들고, 같은 스위트에 같은 명령으로 측정합니다.

```cmd
pelly-eval --suite <폴더> --config <후보.json> --repeat 3 --keep-work failed --out D:\pelly-field\candidate-1
```

그다음 기준선과 비교합니다.

```cmd
pelly-eval compare D:\pelly-field\baseline\summary.json D:\pelly-field\candidate-1\summary.json
```

종료 코드: 0 채택 · 3 거절 · 4 판정 불가. **종료 0만 채택합니다.** 거절된 후보는 기본값을 유지하고 결과만 양식에 적습니다.
채택 규칙(R4): 평균 +1.0건 이상, 범주별 −0.5건 넘는 감소 없음, 위반 0건, 시간 중위값 1.5배 이하.
후보마다 `candidate-2`, `candidate-3`…으로 폴더를 나눕니다.

### 8.1 프로젝트 규칙(AGENTS.md) 효과 측정 (PRD §12.6)

규칙 효과는 **대상 코드에 보이지 않는 팀 관례**가 걸리는 작업에서만 나타납니다(외부 사전 측정: 내장 50건 +1.3%p, 관례 작업 6건 0/6 → 6/6, `docs/v2-3-rules-premeasure.md`).

1. 실제 코드 루트에서 `pelly rules init --dry-run`으로 초안을 보고, 팀 관례 중 코드에 잘 드러나지 않는 것을 한 줄씩 더해 `AGENTS.md`를 만듭니다(예: 현재 시각 함수, 공통 예외 타입, 금지 API). 코드에 보이는 관례(주석 언어 등)는 모델이 이미 따라 하므로 효과가 작습니다.
2. 그 관례가 걸리는 작업을 스위트에 넣고 `mustContain`(써야 하는 것)·`mustNotAdd`(새로 쓰면 안 되는 것)로 판정합니다. 관례가 걸리는 작업 비율은 실제 요청 이력에 가깝게 잡습니다.
3. `AGENTS.md`는 작업 공간 루트에 있어야 합니다. 코드 쪽에 두지 않으려면 루트의 `overlay` 폴더나 `supportFiles`로 넣습니다(`eval/benchmark-rules/README.md`).
4. 같은 스위트를 규칙 없이, 그리고 규칙과 함께 한 번씩 돌려 비교합니다.

```cmd
pelly-eval --suite <폴더> --config <pelly.json> --repeat 3 --no-rules --keep-work failed --out D:\pelly-field\rules-off
pelly-eval --suite <폴더> --config <pelly.json> --repeat 3 --keep-work failed --out D:\pelly-field\rules-on
pelly-eval compare D:\pelly-field\rules-off\summary.json D:\pelly-field\rules-on\summary.json
```

`--no-rules`는 작업 복사본의 `AGENTS.md`를 모두 지우고 돌립니다(요약의 조건에 `rulesRemoved: true`). 결과는 `field-results.md` 8.1에 적습니다.

## 9. 반출

반출 가능 여부는 사내 보안 규정을 따릅니다. 아래 명령은 비식별본을 만들 뿐, 반출을 허가하지 않습니다.

```cmd
pelly-eval export D:\pelly-field\baseline --out D:\pelly-field\export\baseline
pelly-eval export D:\pelly-field\candidate-1 --out D:\pelly-field\export\candidate-1
```

* 작업 복사본(`work\`)과 pelly 실행 기록은 복사하지 않습니다. 사유·상세 본문과 접속 주소·경로는 지웁니다.
* 작업 id는 형식이 정해져 있어 업무 이름이 들어가지 않습니다. 형식이 틀린 id가 결과에 하나라도 있으면 `export`는 반출하지 않고 종료 4로 끝나며, 그 id를 출력합니다.

pelly 자체의 실행 기록과 환경은 따로 비식별해서 냅니다.

`pelly report`는 현재 폴더에서 위로 올라가며 처음 만나는 `.git`, `.svn`, `*.sln`이 있는 폴더를 작업 공간으로 봅니다. 직원이 pelly를 쓴 작업 공간의 루트로 **먼저 이동**한 뒤 실행하십시오. 다른 폴더(예: `D:\pelly-field`)에서 실행하면 「실행 기록이 없습니다」를 출력하고 아무것도 쓰지 않은 채 종료 0으로 끝납니다.

```cmd
cd /d <작업 공간 루트>
pelly report --sanitize --out D:\pelly-field\export\report
pelly doctor --sanitize --out D:\pelly-field\export\doctor --config <pelly.json> --project <레거시.csproj>
```

* `doctor`에는 2절과 같은 `--config`와 `--project`를 줍니다. 그래야 반출 기록이 측정한 환경을 설명합니다.
* `pelly report`는 `pelly-eval`이 만든 실행 로그(`work\<id>\.pelly\runs`)를 포함하지 않습니다. `--keep-work failed`로 돌렸다면 대부분 지워져 있기도 합니다.

반출 전에 `export` 폴더의 파일을 직접 열어 사내 이름이 없는지 훑어보십시오. 반출한 파일 목록을 `field-results.md`에 적습니다.

## 10. Visual Studio External Tools 확인

**pelly zip**을 푼 폴더의 `docs\ide-external-tools.md`(현장 키트 zip에는 없습니다)의 「Visual Studio 실기 확인 결과」 표를 0번부터 8번까지 채웁니다.
설정은 같은 문서의 「설정 값」 절을 따릅니다. 항목마다 결과(합격·불합격·해당 없음)와 메모를 적습니다.
불합격 항목은 고치지 말고 그대로 기록해서 내보내십시오.

`pelly ui`(승인 창)를 쓸 PC라면 같은 문서의 「GUI 항목」대로 항목을 더 등록하고, `field-results.md` 9절 「`pelly ui` 현장 확인」의 U1~U4를 채웁니다. (Windows 미확인)
