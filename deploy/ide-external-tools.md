# IDE External Tools 연동

IDE에서 파일을 연 채로 커서 위치의 멤버를 pelly로 수정하는 설정입니다.
`pelly.exe` 전체 경로를 알아야 합니다(예: `C:\Tools\pelly\pelly.exe`). 설치는 [install.md](install.md)를 보십시오.

## 설정 값

| 항목 | Visual Studio (도구 → 외부 도구) | Rider (Settings → Tools → External Tools) |
| :--- | :--- | :--- |
| 명령 | `pelly.exe` 전체 경로 | 같음 |
| 인수 | `cs "$(ItemPath)" --line $(CurLine) --pause-on-exit` | `cs "$FilePath$" --line $LineNumber$ --pause-on-exit` |
| 초기 디렉터리 | `$(SolutionDir)` | `$ProjectFileDir$` |
| 기타 | 「출력 창 사용」 끔 (별도 콘솔 창에서 대화형 입력) | 「Synchronize files after execution」 켬 |

* 인수의 파일 경로는 큰따옴표로 감싸십시오. 경로에 공백이 있어도 하나로 전달됩니다.
* 요청문은 인수에 넣지 않습니다. 콘솔 창에서 `수정 요청을 입력하세요:`가 뜨면 입력합니다.

## SQL용 항목

같은 방식으로 항목을 하나 더 등록합니다. 제목은 예를 들어 `Pelly SQL`입니다.

| 항목 | Visual Studio | Rider |
| :--- | :--- | :--- |
| 인수 | `sql "$(ItemPath)" --line $(CurLine) --pause-on-exit` | `sql "$FilePath$" --line $LineNumber$ --pause-on-exit` |

나머지 값은 C# 항목과 같습니다. `.sql` 파일에서 실행하십시오.
SQL 항목은 [install.md](install.md)의 설정(`sql.allowedHosts` 등)이 끝나 있어야 동작합니다.

## 실행 전에 저장

IDE에 저장하지 않은 편집이 있으면 pelly는 **디스크의 파일만** 보고 수정합니다.
저장하지 않은 편집이 사라지거나 충돌할 수 있으니 실행 전에 저장하십시오.

* Visual Studio: 모두 저장 단축키(`Ctrl+Shift+S`) 후 외부 도구를 실행합니다. 자동 저장 설정이 없으면 습관으로 두십시오.
* Rider: 외부 도구 실행 전에 자동 저장되도록 설정하거나 `Ctrl+S`로 저장합니다. 자동 저장 항목의 위치와 이름은 버전마다 다를 수 있습니다. 아래 「Rider 실기 확인 결과」에서 확정합니다.

저장하지 않은 IDE 편집과 겹쳐 파일이 바뀐 경우에도 pelly는 그 사이 외부에서 바뀐 파일을 덮어쓰지 않고 종료 코드 4로 멈춥니다.

## 파일 다시 불러오기

pelly가 파일을 고치고 나면 IDE가 그 변경을 가져와야 합니다.

* Rider: 「Synchronize files after execution」를 켜면 됩니다.
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

(S7 실기 확인 뒤에 채웁니다.)
