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

## 사용법 (M1)

### `pelly cs` — C# 메서드 하나 수정
```bash
pelly cs <파일> "<요청문>" --method <메서드> [--class <클래스>] [--yes] [--dry-run] [--max-retries N] \
  [--config <경로>] [--think off|retry|always] [--model <태그>] [--endpoint <URL>]
```

* M1에서는 `--method`가 필수입니다. 같은 이름이 여러 개면 `--class`로 좁힙니다.
* 순서: 패치를 파일에 **먼저 쓰고**(APPLY) → 구문 검사(VERIFY) → 통과하면 diff를 보여주고 승인을 묻습니다(REVIEW). 거부하면 원래 파일로 되돌립니다.
  승인 질문이 떠 있는 동안 파일은 이미 바뀐 상태이므로, IDE나 파일 감시 도구(빌드 watch 등)가 승인 전 변경을 볼 수 있습니다.
* `--yes`는 질문 없이 승인, `--dry-run`은 파일을 쓰지 않고 diff만 보여줍니다.
* 실행 기록은 작업 공간의 `.pelly/runs/<runId>/`(`summary.json`, `events.jsonl`, `attempt-*/`)에 남습니다.
* 예: `pelly cs Shop.Api/Services/OrderService.cs "배송 중인 주문은 취소할 수 없게 해줘" --method CancelAsync`

### `pelly doctor` — 환경 점검
Ollama 서버 접속, 모델 존재·로드 여부, chat template, 토크나이저를 점검하고 확인할 항목을 알려 줍니다.

### 설정 파일
설정은 아래 순서로 합쳐지며 뒤의 것이 앞의 것을 덮어씁니다.

1. 내장 기본값
2. 사용자 설정: `pelly/config.json` (macOS·Linux `~/.config/pelly/config.json` 또는 `~/Library/Application Support/pelly/config.json`, Windows `%APPDATA%\pelly\config.json`)
3. 작업 공간 설정: `<작업 공간 루트>/pelly.json` (`--config`로 경로 지정 가능). 작업 공간 루트는 대상 파일에서 위로 올라가며 처음 만나는 `.git`, `.svn` 또는 `*.sln`이 있는 폴더입니다.
4. 환경 변수 `PELLY__<섹션>__<키>` (예: `PELLY__LLM__MODEL`)
5. 명령줄 옵션 (`--model`, `--endpoint`, `--think`)

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

실제 모델 실행 기록은 [docs/m1-e2e.md](docs/m1-e2e.md)에 있습니다.

## 통합 테스트 환경
환경이 필요한 통합 테스트는 환경 변수가 있을 때만 실행됩니다.

| 분류 | 환경 변수 |
| :--- | :--- |
| SQL Server | `PELLY_TEST_SQL` (연결 문자열) |
| Ollama | `PELLY_TEST_OLLAMA` (엔드포인트), `PELLY_TEST_MODEL` (모델 태그) |
| Windows | Windows에서 자동 실행 |
