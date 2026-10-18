# S7. Rider External Tools 실기 확인표

사람이 JetBrains Rider에서 직접 확인합니다. 구현자가 준비한 것은 이 표와 준비물뿐입니다.
**결과 칸은 비어 있습니다.** 확인한 뒤 채우고 알려 주십시오. 결과는 `deploy/ide-external-tools.md`의 「Rider 실기 확인 결과」 절에 옮깁니다.

* 확인 대상: `deploy/ide-external-tools.md`의 Rider 설정 (현재 모두 「실기 미확인」).
* 실패 항목은 결함으로 기록합니다. 고칠지는 사용자가 정합니다.
* 사전 점검 (2026-10-19, 터미널, Rider 아님): `pelly --version` → `0.9.0`. `pelly doctor` 종료 0 (Ollama·모델·토크나이저 Ok). `pelly cs … --dry-run`, `pelly sql … --dry-run` 모두 패치 미리 보기까지 정상.

## 1. 준비물

준비물은 저장소 밖 `/Users/payletter/pelly-s7/`에 있습니다. 커밋하지 않습니다.

| 항목 | 경로 |
| :--- | :--- |
| Mac용 `pelly` (osx-arm64 단일 파일, 0.9.0) | `/Users/payletter/pelly-s7/bin/pelly` |
| 코퍼스 복사본 (git 저장소로 초기화됨) | `/Users/payletter/pelly-s7/Modern/` |
| Rider에서 열 솔루션 | `/Users/payletter/pelly-s7/Modern/Modern.sln` |
| 작업 공간 설정 | `/Users/payletter/pelly-s7/Modern/pelly.json` |
| C# 대상 파일 | `/Users/payletter/pelly-s7/Modern/Shop.Api/Services/OrderService.cs` |
| SQL 대상 파일 | `/Users/payletter/pelly-s7/Modern/sql/usp_CancelOrder.sql` |

* `pelly.json`은 로컬 Ollama(`http://localhost:11434`)와 모델 `qwen3.6:35b-a3b-coding-nvfp4`를 가리킵니다.
* SQL 허용 목록은 호스트 `localhost`, 데이터베이스 `PellyCorpus`입니다. 비밀번호는 어디에도 없습니다.
* 시작 전에 Ollama가 떠 있어야 합니다. 첫 요청은 모델 로딩 때문에 느릴 수 있습니다.
* 복사본은 git 저장소입니다. 시험 사이에 `git -C /Users/payletter/pelly-s7/Modern restore .`으로 원래대로 되돌립니다.
* 작업 공간 루트는 `Modern.sln`이 있는 `/Users/payletter/pelly-s7/Modern` 입니다.

시작: Rider에서 `Modern.sln`을 엽니다. `OrderService.cs`를 엽니다.

## 2. External Tools 등록

`Settings → Tools → External Tools`에서 `+`로 항목을 추가합니다. 항목 이름은 Rider 버전마다 조금 다를 수 있습니다. 다르면 화면에 보이는 이름을 메모 칸에 적으십시오.

### 항목 A: `Pelly C#` (본 확인용)

| 필드 | 값 |
| :--- | :--- |
| Name | `Pelly C#` |
| Program | `/Users/payletter/pelly-s7/bin/pelly` |
| Arguments | `cs "$FilePath$" --line $LineNumber$ --pause-on-exit` |
| Working directory | `$ProjectFileDir$` |
| Synchronize files after execution | 켬 |
| Open console for tool output | 켬 |
| Make console active on message in stdout / stderr | 켬 (있으면) |
| Show console when a message is printed to stdout / stderr | 켬 (있으면) |

### 항목 B: `Pelly C# dry` (콘솔 입력이 안 될 때만)

콘솔이 키 입력을 받지 못할 때만 등록합니다. 요청문을 인수로 넘깁니다.

| 필드 | 값 |
| :--- | :--- |
| Name | `Pelly C# dry` |
| Program | `/Users/payletter/pelly-s7/bin/pelly` |
| Arguments | `cs "$FilePath$" "취소 사유를 최대 길이로 자르지 말고 그대로 사용해 줘" --line $LineNumber$ --dry-run` |
| Working directory | `$ProjectFileDir$` |
| Synchronize files after execution | 켬 |
| Open console for tool output | 켬 |

적용 변형은 `--dry-run`을 `--yes`로 바꾼 같은 항목(`Pelly C# yes`)입니다. 승인 질문 없이 바로 씁니다. 필요할 때만 만드십시오.

### 항목 C: `Pelly SQL` (선택, 8번)

| 필드 | 값 |
| :--- | :--- |
| Name | `Pelly SQL` |
| Program | `/Users/payletter/pelly-s7/bin/pelly` |
| Arguments | `sql "$FilePath$" --line $LineNumber$ --pause-on-exit` |
| Working directory | `$ProjectFileDir$` |
| 나머지 | 항목 A와 같음 |

**SQL은 선택 항목입니다. 이유:** 이 SQL 서버는 SQL 인증이라 비밀번호가 필요합니다. 비밀번호는 파일에 둘 수 없고(`pelly.json`에 `Password`가 있으면 종료 코드 4), `PELLY_SQL_CONNECTION` 환경 변수로만 줄 수 있습니다.
Rider의 External Tools 대화상자에 환경 변수 칸이 있는지는 이 Mac에 Rider가 없어 **확인하지 못했습니다**. 대화상자에 환경 변수 칸이 있으면 그 칸에 넣으십시오. 없으면 다음 방법을 쓰십시오.

1. Rider를 완전히 종료합니다.
2. 터미널에서 환경 변수를 준 채 Rider를 실행합니다. 값은 `common.md`나 본인이 아는 컨테이너 접속 정보로 채웁니다(여기에 쓰지 않습니다).
   ```
   PELLY_SQL_CONNECTION='Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=<비밀번호>;TrustServerCertificate=True' /Applications/Rider.app/Contents/MacOS/rider
   ```
   (Rider 설치 경로는 다를 수 있습니다. `open -a Rider`는 터미널 환경을 넘기지 않으므로 쓰지 마십시오. 이 방법도 확인 전입니다.)
3. 이 방법이 안 되면 8번은 「건너뜀」으로 적고 이유를 메모합니다.

## 3. 확인 항목

각 항목 앞에서 복사본을 원래대로 되돌립니다: `git -C /Users/payletter/pelly-s7/Modern restore .`
`Tools → External Tools → Pelly C#`로 실행합니다. 실행 전에 `Ctrl+S`(또는 `Cmd+S`)로 저장합니다.

### 0. 「Open console for tool output」 콘솔이 키 입력을 받는가

* 동작: `OrderService.cs`를 엽니다. 커서를 **45번 줄**(`var trimmedReason = reason.Trim();`)에 둡니다. `Pelly C#`을 실행합니다. 도구 창에 `수정 요청을 입력하세요:`가 뜨면 그 창을 클릭하고 `abc`를 입력해 봅니다.
* 기대: 창이 열리고 글자가 입력 줄에 보입니다. 안 보이면 입력이 막힌 것입니다. 이 경우 항목 B(`Pelly C# dry`)로 바꾸고 2번과 5번을 「해당 없음」으로 적습니다. 4번은 대기가 일어나지 않을 수 있습니다.
* 결과: ___ / 메모: ___

### 1. `--line` 실행 시 커서 메서드가 대상이 된다

* 동작: 45번 줄에 커서를 두고 `Pelly C#`을 실행합니다. 요청문은 `취소 사유를 최대 길이로 자르지 말고 그대로 사용해 줘`.
* 기대: 진행 줄에 `Locate 대상 찾기: 45번 줄`이 보입니다. 패치 미리 보기의 대상이 `CancelAsync`입니다. `MaxReasonLength` 검사 블록(`if (trimmedReason.Length > MaxReasonLength) { … }`)이 지워지는 diff가 나옵니다. 다른 메서드는 바뀌지 않습니다.
* 결과: ___ / 메모: ___

### 2. 요청문을 생략하고 콘솔에서 한글을 입력할 수 있다

* 동작: 항목 A는 인수에 요청문이 없습니다. `수정 요청을 입력하세요:`에서 한글 `취소 사유를 최대 길이로 자르지 말고 그대로 사용해 줘`를 입력하고 Enter. 입력 도중 글자 조합(IME)이 깨지는지도 봅니다.
* 기대: 입력한 한글이 깨지지 않고 줄에 보입니다. 그 요청대로 진행됩니다.
* 결과: ___ / 메모: ___

### 3. 출력의 한글이 깨지지 않는다

* 동작: 1번 실행의 출력 전체를 봅니다(진행 줄, diff, 안내 문구, 표 테두리).
* 기대: `사전 점검`, `패치 미리 보기`, diff 안의 한글 주석·문자열이 모두 정상입니다. `?`나 `□`, 깨진 글자가 없습니다. 선 문자(`─`, `│`)가 있으면 어떻게 보이는지 메모합니다.
* 결과: ___ / 메모: ___

### 4. `--pause-on-exit`로 창이 남는다

* 동작: 1번 실행을 끝까지 진행합니다(승인 질문에는 `n`). 끝난 뒤 창을 봅니다.
* 기대: `종료 코드 n. 아무 키나 누르면 창을 닫습니다…`가 보이고 창이 남아 있습니다. 키를 누르면 닫힙니다. 창이 바로 사라지거나 키를 기다리지 않으면, 기다린 시간을 메모합니다. (입력이 리디렉션된 환경이면 기다리지 않는 것이 설계입니다. 그런 경우 「기다리지 않음」으로 적습니다.)
* 결과: ___ / 메모: ___

### 5. Ctrl+C 후에도 창이 남는다 (Task 1)

* 동작: `Pelly C#`을 실행하고 모델이 응답하는 동안(`수정 생성` 단계) 콘솔에서 `Ctrl+C`를 누릅니다. 다음에 같은 실행을 한 번 더 하고 이번에는 도구 창의 정지(빨간 네모) 버튼을 눌러 봅니다.
* 기대:
  * `Ctrl+C`: 파일은 원래대로이고(`git -C /Users/payletter/pelly-s7/Modern status --short`가 비어 있음), 종료 코드를 보여 주고 키를 기다립니다(창이 남음). 대기 중 `Ctrl+C`를 한 번 더 누르면 바로 끝납니다.
  * 정지 버튼: 종료 신호로 취급되어 기다리지 않을 수 있습니다. 이것은 설계입니다. 실제 동작과 파일 복원 여부만 메모합니다.
  * Rider 콘솔이 `Ctrl+C`를 신호로 전달하지 않으면 그 사실을 적습니다.
* 결과: ___ / 메모: ___

### 6. 승인 후 Rider가 파일을 다시 읽는다

* 동작: 복사본을 되돌리고 `OrderService.cs`를 연 채로 1번 실행을 합니다. 승인 질문(y/n)에서 `y`를 입력합니다. 에디터 창을 눈여겨봅니다.
* 기대: 「Synchronize files after execution」 덕분에 에디터가 바뀐 파일을 자동으로 다시 읽습니다. 에디터에서 `MaxReasonLength` 블록이 사라진 것이 보입니다. 외부 변경 대화 상자는 뜨지 않거나, 뜨면 그 문구를 메모합니다. 에디터가 안 바뀌면 `File → Reload All from Disk` 후에 바뀌는지 메모합니다. 확인은 `git -C /Users/payletter/pelly-s7/Modern diff --stat`로도 합니다(파일은 실제로 바뀌어 있어야 함).
* 보너스: 승인 질문이 떠 있는 동안에도 파일은 이미 바뀐 상태입니다. 이때 `n`을 입력하면 파일이 원래대로 돌아오고 에디터도 따라 돌아오는지 봅니다.
* 결과: ___ / 메모: ___

### 7. 실행 전 자동 저장 (참고)

* 동작: `OrderService.cs`의 주석에 글자 하나를 입력하되 저장하지 않고 `Pelly C#`을 실행합니다.
* 기대: `Settings → Appearance & Behavior → System Settings`의 저장 관련 항목(예: 「Save files if the IDE is idle」, 「on frame deactivation」)이 켜져 있으면 저장되어 pelly가 새 내용을 봅니다. 꺼져 있으면 pelly는 디스크 내용만 봅니다. 어느 경우였는지, 항목의 실제 이름과 위치를 적습니다.
* 결과: ___ / 메모: ___

### 8. SQL 항목 `--line` (선택)

* 준비: 위 「항목 C」 절차로 `PELLY_SQL_CONNECTION`이 Rider에 전달되도록 합니다. 복사본의 `sql/usp_CancelOrder.sql`을 엽니다.
* 동작: **42번 줄**(`UPDATE dbo.Orders`)에 커서를 두고 `Pelly SQL`을 실행합니다. 요청문은 `이미 배송 중(Status=2)인 주문도 취소할 수 없게 해줘`.
* 기대: `Locate 대상 찾기: 42번 줄`. diff에 `IF @Status = 2 … RAISERROR(N'배송 중인 주문은 취소할 수 없습니다.' …)` 블록이 `@Status = 3` 검사 앞에 더해집니다(문구는 다를 수 있음). 환경 변수가 안 넘어가면 종료 코드 4 또는 5와 안내가 나옵니다. 그 경우 「환경 변수 전달 안 됨」으로 적습니다.
* 결과: ___ / 메모: ___

## 4. 끝나면

* 위 결과 칸을 채워 컨트롤러에게 알려 주십시오.
* 실패하거나 이름이 다른 항목은 메모 칸에 화면에 보이는 그대로 적으십시오.
* `Rider 버전`: ___ / `macOS`: ___ / 날짜: ___
