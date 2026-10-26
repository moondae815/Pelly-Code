# IDE External Tools 연동

IDE에서 파일을 연 채로 커서 위치의 멤버를 pelly로 수정하는 설정입니다.
`pelly.exe` 전체 경로를 알아야 합니다(예: `C:\Tools\pelly\pelly.exe`). 설치는 [install.md](install.md)를 보십시오.

> **주의: Rider 설정은 실기로 확인하지 않았습니다.** Rider 실기 확인은 하지 않기로 했습니다(2026-10-19). 현장에서 Rider를 쓰면 아래 「콘솔 입력이 안 될 때」의 명령줄 대안을 먼저 보십시오.
> Rider는 외부 도구 출력을 Run 도구 창에 보여 줄 수 있습니다. 이 창이 키 입력(stdin)을 받지 않으면 요청문을 입력할 수 없고, 입력이 막힌 것으로 보여 `--pause-on-exit` 대기도 일어나지 않습니다.
> 콘솔 입력이 안 되면 아래 「콘솔 입력이 안 될 때」를 따르고, 「Rider 실기 확인 결과」 절을 보십시오.
> Visual Studio 설정도 폐쇄망 현장 확인(M4) 전이며 Windows 미확인입니다.

## 설정 값

| 항목 | Visual Studio (도구 → 외부 도구) | Rider (Settings → Tools → External Tools) (실기 미확인) |
| :--- | :--- | :--- |
| 명령 | `pelly.exe` 전체 경로 | 같음 (실기 미확인) |
| 인수 | `cs "$(ItemPath)" --line $(CurLine) --pause-on-exit` | `cs "$FilePath$" --line $LineNumber$ --pause-on-exit` (실기 미확인) |
| 초기 디렉터리 | `$(SolutionDir)` | `$ProjectFileDir$` (실기 미확인) |
| 기타 | 「출력 창 사용」 끔 (별도 콘솔 창에서 대화형 입력) | 「Synchronize files after execution」 켬. 「Open console for tool output」(정확한 이름은 실기 미확인) 항목이 있으면 켜서 입력 가능한 콘솔로 열리는지 확인 (실기 미확인) |

* 인수의 파일 경로는 큰따옴표로 감싸십시오. 경로에 공백이 있어도 하나로 전달됩니다.
* 요청문은 인수에 넣지 않습니다. 콘솔 창에서 `수정 요청을 입력하세요:`가 뜨면 입력합니다.

## 콘솔 입력이 안 될 때

External Tools 창이 키 입력을 받지 못하면 요청문을 인수로 넘깁니다. 이때 대화형 질문은 쓸 수 없습니다.

* 먼저 미리 보기: `cs "$FilePath$" "요청문" --line $LineNumber$ --dry-run` (파일을 쓰지 않고 diff만 보여 줌)
* 적용: `--dry-run`을 `--yes`로 바꿉니다. 승인 질문 없이 바로 씁니다. 결과는 반드시 IDE의 diff나 버전 관리 도구로 확인하십시오.
* 요청문이 고정되므로 자주 쓰는 요청마다 항목을 따로 등록하거나, 터미널 창에서 `pelly`를 직접 실행하는 편이 낫습니다.
* 이 경우 `--pause-on-exit`는 기다리지 않을 수 있습니다. 결과는 도구 창 출력과 종료 코드로 확인합니다.
* 대상이 여러 개라 모호하면 비대화형에서는 종료 코드 3입니다. `--line`이나 `--method`/`--object`로 정확히 지정하십시오.

## SQL용 항목

같은 방식으로 항목을 하나 더 등록합니다. 제목은 예를 들어 `Pelly SQL`입니다.

| 항목 | Visual Studio | Rider |
| :--- | :--- | :--- |
| 인수 | `sql "$(ItemPath)" --line $(CurLine) --pause-on-exit` | `sql "$FilePath$" --line $LineNumber$ --pause-on-exit` (실기 미확인) |

나머지 값은 C# 항목과 같습니다. `.sql` 파일에서 실행하십시오.
SQL 항목은 [install.md](install.md)의 설정(`sql.allowedHosts` 등)이 끝나 있어야 동작합니다.

## GUI 항목 (`pelly ui`)

콘솔 입력이 안 되는 IDE에서는 콘솔 대신 승인 창을 씁니다. 요청 입력·diff 확인·승인이 창 안에서 되므로 표준 입력과 `--pause-on-exit`가 필요 없습니다. 기존 콘솔 항목은 그대로 두고 **항목을 더 등록**합니다. 사용법은 [usage.md](usage.md)의 「`pelly ui`」를 보십시오.
이 절의 모든 설정은 **Windows 미확인**입니다. Rider 열은 실기 미확인입니다.

| 항목 | Visual Studio | Rider (실기 미확인) |
| :--- | :--- | :--- |
| 제목 | `Pelly C# (창)` | 같음 |
| 명령 | `pelly.exe` 전체 경로 | 같음 |
| 인수 (C#) | `ui cs "$(ItemPath)" --line $(CurLine)` | `ui cs "$FilePath$" --line $LineNumber$` |
| 인수 (SQL) | `ui sql "$(ItemPath)" --line $(CurLine)` | `ui sql "$FilePath$" --line $LineNumber$` |
| 초기 디렉터리 | `$(SolutionDir)` | `$ProjectFileDir$` |
| 기타 | **「출력 창 사용」 켬** | 「Synchronize files after execution」 켬. Run 도구 창으로 충분합니다(표준 입력이 필요 없음) |

* Visual Studio는 반드시 「출력 창 사용」을 켜십시오. `pelly.exe`는 콘솔 실행 파일이라 끄면 빈 콘솔 창이 창과 함께 뜹니다.
* `--pause-on-exit`와 `--yes`는 붙이지 않습니다. 주면 종료 코드 4입니다.
* 요청문은 인수에 넣지 않고 창의 요청 칸에 입력한 뒤 「실행」(Ctrl+Enter)을 누릅니다.
* 모르는 옵션이나 값 빠진 옵션 같은 명령줄 오류는 창 없이 출력 창에 한 줄로 나오고 종료 코드 4입니다. 항목을 처음 등록한 뒤 한 번 실행해 출력 창을 확인하십시오.
* 파일 경로 검사 같은 그 밖의 오류는 창 안에 빨간 글씨로 보입니다.
* `pelly.exe` 옆에 `av_libglesv2.dll`, `libHarfBuzzSharp.dll`, `libSkiaSharp.dll`이 있어야 창이 뜹니다. 없으면 출력 창에 「GUI 구성 요소를 불러오지 못했습니다」가 나오고 종료 코드 4입니다.
* 승인 뒤 IDE가 파일을 다시 불러오는 것과 「실행 전에 저장」은 콘솔 항목과 같습니다(아래 절).
* 한글 IME 입력, 고해상도 표시, 창이 뜨는지는 현장 확인 전입니다. 결과는 `field-results.md`의 「pelly ui 현장 확인」에 적습니다.

## 실행 전에 저장

IDE에 저장하지 않은 편집이 있으면 pelly는 **디스크의 파일만** 보고 수정합니다.
저장하지 않은 편집이 사라지거나 충돌할 수 있으니 실행 전에 저장하십시오.

* Visual Studio: 모두 저장 단축키(`Ctrl+Shift+S`) 후 외부 도구를 실행합니다. 자동 저장 설정이 없으면 습관으로 두십시오.
* Rider (실기 미확인): 외부 도구 실행 전에 자동 저장되도록 설정하거나 `Ctrl+S`로 저장합니다. 자동 저장 항목의 위치와 이름은 버전마다 다를 수 있습니다. 아래 「Rider 실기 확인 결과」에서 확정합니다.

저장하지 않은 IDE 편집과 겹쳐 파일이 바뀐 경우에도 pelly는 그 사이 외부에서 바뀐 파일을 덮어쓰지 않고 종료 코드 4로 멈춥니다.

## 파일 다시 불러오기

pelly가 파일을 고치고 나면 IDE가 그 변경을 가져와야 합니다.

* Rider (실기 미확인): 「Synchronize files after execution」를 켜면 됩니다.
* Visual Studio: 파일이 외부에서 바뀌었다는 대화 상자가 뜨면 「예」(다시 불러오기)를 선택합니다. (Windows 미확인)

승인 질문이 떠 있는 동안 파일은 이미 바뀐 상태입니다. 그 동안 IDE가 파일을 다시 불러와도, 거부하면 pelly가 원래 파일로 되돌립니다.

## `--pause-on-exit`

External Tools로 띄운 콘솔은 pelly가 끝나면 바로 사라집니다. 결과를 읽을 수 있게 인수 끝에 `--pause-on-exit`를 붙입니다.
끝난 뒤 「종료 코드 n. 아무 키나 누르면 창을 닫습니다…」를 보여 주고 키 입력을 기다립니다.

* 인자를 잘못 줘서 실패한 경우에도 기다립니다.
* Ctrl+C로 중단한 실행도 원본을 복원하고 종료 코드를 보여 준 뒤 키를 기다립니다.
* 창 닫기나 프로세스 종료 신호(SIGTERM·SIGHUP)로 중단한 경우는 기다리지 않습니다. 대기 중에 Ctrl+C를 한 번 더 누르면 바로 끝납니다.
* 입력이 리디렉션되어 있으면 기다리지 않습니다.
* Windows 미확인: 키 대기와, 창 닫기와 Ctrl+C의 구분은 macOS에서만 확인했습니다.

## 한글 입력 확인

처음 설정한 뒤 한 번 확인하십시오.

1. External Tools로 pelly를 실행하고 요청문 입력 줄에 한글(`배송 중인 주문은 취소할 수 없게 해줘`)을 입력합니다.
2. 입력한 글자가 깨지지 않고 보이는지, diff와 안내 문구의 한글이 깨지지 않는지 봅니다.
3. 깨지면 콘솔 창의 글꼴과 코드 페이지를 확인하고 결과를 알려 주십시오.

pelly는 입출력을 UTF-8로 맞춥니다. Windows 콘솔에서의 동작은 Windows 미확인입니다.

## Rider 실기 확인 결과

실기 확인을 하지 않기로 했습니다(2026-10-19). Rider 열의 값은 모두 「실기 미확인」입니다. 현장에서 확인한 사람이 있으면 `docs/m3b-s7-rider.md`의 항목 번호로 이 절에 적어 주십시오.

## Visual Studio 실기 확인 결과

폐쇄망 Windows PC의 Visual Studio에서 확인하고 채웁니다. 항목 번호는 Rider 확인표(`docs/m3b-s7-rider.md`)의 0~8과 같습니다.
준비: 「설정 값」 절대로 `Pelly C#` 항목을 등록하고, 작업 공간이 git 저장소인 복사본에서 시험합니다. 항목마다 시험 전에 `git restore .`로 되돌립니다.
결과 칸에는 합격 / 불합격 / 해당 없음 중 하나를 적고, 메모에는 화면에 보인 실제 문구를 적습니다. 불합격은 고치지 말고 그대로 적습니다.

| 번호 | 확인할 것 | 결과 | 메모 |
| :--- | :--- | :--- | :--- |
| 0 | 외부 도구의 콘솔이 키 입력을 받는가 (「출력 창 사용」을 끄면 별도 콘솔 창이 뜨는가) | | |
| 1 | `--line` 실행 시 커서가 있는 메서드가 대상이 되는가 | | |
| 2 | 요청문을 생략하고 콘솔에서 한글을 입력할 수 있는가 | | |
| 3 | 출력의 한글(진행 줄, diff, 안내 문구)이 깨지지 않는가 | | |
| 4 | `--pause-on-exit`로 창이 남고 키를 누르면 닫히는가 | | |
| 5 | Ctrl+C 후에도 파일이 원래대로이고 창이 남는가 (창 닫기는 기다리지 않는 것이 설계) | | |
| 6 | 승인 후 Visual Studio가 파일을 다시 읽는가 (외부 변경 대화 상자 문구) | | |
| 7 | SQL 항목 `--line` (선택: `PELLY_SQL_CONNECTION`이 IDE에 전달되는가) | | |
| 8 | 실행 전 자동 저장 설정 (저장하지 않은 편집을 pelly가 보는가; 설정 이름과 위치) | | |

* 확인한 Visual Studio 버전: ____
* 확인한 날짜·확인자: ____
