# S6. win-x64 단일 파일 배포본

## 질문
폐쇄망에 들고 갈 `pelly.exe` 한 파일이 SDK·네트워크 없이 동작하는가? 포함 리소스·네이티브 SqlClient(SNI)·Roslyn·ScriptDom이 단일 파일 안에서 로드되는가? 크기와 첫 실행 시간은? (PRD §11.1)

## 환경
| 항목 | 값 |
| :--- | :--- |
| SDK | `10.0.302` (`net10.0`) |
| 판정 | GitHub Actions `windows-latest`, 잡 `publish-check` |
| 참고 | 로컬 macOS arm64 (`osx-arm64` 단일 파일) |
| 클라이언트 | `Microsoft.Data.SqlClient` 7.1.1 |
| 날짜 | 2026-10-18 |

## 방법
* publish 명령(PRD §11.1 그대로):
  ```
  dotnet publish src/PellyCoder.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=none -o artifacts/publish/win-x64
  ```
* 같은 속성을 `PellyCoder.Cli.csproj`의 `Condition="'$(RuntimeIdentifier)'=='win-x64'"` 그룹에도 두었다. `dotnet publish -r win-x64`만 써도 같은 exe가 나온다.
  * 단, 그 그룹의 `DebugType=none`은 Cli 프로젝트에만 걸린다. 명령줄 없이 publish하면 `PellyCoder.Core.pdb`가 옆에 남는다. 반입본은 위 명령을 쓴다.
* `pelly doctor --offline-selftest`(숨김)에 `SqlClient` 행을 더했다. 여섯 행이다.
  * Windows: 존재하지 않는 로컬 파이프 `np:\\.\pipe\pelly-selftest-none\sql\query`에 `Connect Timeout=1`로 연결한다. `SqlException`이면 네이티브 SNI가 로드된 것이다. 네트워크는 쓰지 않는다.
  * Windows가 아니면 SqlClient가 관리 코드 SNI를 쓰므로 `SqlConnection` 생성만 한다.
  * SqlClient 호출은 `SqlServerExecutor.SelfTestNativeLayer()` 안에만 있다.
* CI `publish-check` 잡이 publish한 exe로 다음을 확인한다.
  1. exe 크기를 로그에 남긴다.
  2. `doctor --offline-selftest` 종료 0. 새 압축 해제 폴더(`DOTNET_BUNDLE_EXTRACT_BASE_DIR`)로 첫 실행과 두 번째 실행 시간을 잰다.
  3. `--version`이 `0.9.0`으로 시작한다.
  4. `cs --help` 종료 0.
  5. Cli 포함 리소스(서버 확인 사항) 확인: 닫힌 루프백 포트(`127.0.0.1:1`)로 `doctor`를 실행해 종료 4와 체크리스트 출력을 본다.

## 측정 결과
**크기**

| 변형 | win-x64 | osx-arm64 |
| :--- | ---: | ---: |
| 기본(위 명령), Mac 교차 publish | 118,783,302 B (113.3 MB) | 124,086,294 B |
| 기본(위 명령), CI `windows-latest` | **119,305,264 B (113.8 MB)** | - |
| + `SatelliteResourceLanguages=en;ko` | 110,142,461 B | - |
| + `EnableCompressionInSingleFile=true` | 52,477,302 B | 51,054,886 B |
| 참고: 단일 파일 아님(폴더) | 120 MB, 파일 255개 | - |

* 변형 행(위성 제한·압축·폴더)은 Mac 교차 publish 값이다.
* CI에서 첫 실행 때 풀린 파일은 `Microsoft.Data.SqlClient.SNI.dll`(555,416 B) 하나다. 위치: `DOTNET_BUNDLE_EXTRACT_BASE_DIR\pelly\<해시>\`(기본값은 `%TEMP%\.net`). 나머지 네이티브 라이브러리(런타임 등)는 exe 안에서 바로 로드된다.

**실행 시간** (`doctor --offline-selftest`)

| 환경 | 첫 실행 | 두 번째 실행 |
| :--- | ---: | ---: |
| osx-arm64 기본 | 1.60 s | 0.46 s |
| osx-arm64 압축 | 1.31 s | 0.56 s |
| win-x64 (CI, run 39131686427) | 2.46 s | 2.36 s |

* osx-arm64에서는 풀 네이티브 라이브러리가 없어 압축 해제 폴더가 생기지 않았다. 첫 실행의 1초 남짓은 OS의 첫 실행 비용이다.
* win-x64는 압축 해제(SNI 0.5 MB 하나)가 시간에 거의 영향을 주지 않는다. 두 번째 실행도 2.4초 남짓이다. 러너의 백신 검사 등 환경 비용으로 보이며, 실제 PC 값은 **Windows 미확인**이다.
* CI 러너의 실측은 Mac 교차 publish보다 약 0.5 MB 크다(Windows에서 만든 apphost 차이로 보인다). 반입본은 Windows에서 만든 값을 기준으로 한다.

**자체 점검 결과**

| 행 | osx-arm64 | win-x64 (CI) |
| :--- | :--- | :--- |
| 토크나이저 | Ok 토큰 8개 | Ok 토큰 8개 |
| Roslyn | Ok | Ok |
| ScriptDom | Ok | Ok |
| 인코딩 | Ok CP949 왕복 성공 | Ok |
| 리소스 | Ok 프롬프트·채팅 템플릿 | Ok |
| SqlClient | Ok 관리 코드 SNI | Ok 네이티브 SNI 로드 (예상된 연결 실패 `Number` 2) |

* 포함 리소스 두 곳(Core의 프롬프트·토크나이저, Cli의 `server-checklist.md`)은 매니페스트 리소스라 단일 파일에서도 그대로 읽힌다. osx-arm64 exe의 `doctor` 출력에 체크리스트가 나오는 것을 확인했다.
* `--version` → `0.9.0`, `cs --help` 종료 0 (osx-arm64).

## 문제와 해결
| # | 문제 | 해결 |
| :--- | :--- | :--- |
| 1 | `PackAsTool=true`와 RID 단일 파일 publish가 충돌하는지 | 충돌 없음. publish·`dotnet pack`(PellyCoder.Cli.0.9.0.nupkg) 모두 그대로 동작한다. 속성은 RID 조건 그룹에만 두어 pack에는 걸리지 않는다 |
| 2 | CI 첫 실행(run 39131686427): 점검 다섯 개가 모두 통과했는데 스텝이 종료 1로 실패. 오류 메시지 없음 | 마지막 명령이 의도대로 종료 4로 끝나는 `doctor`(체크리스트 확인)였다. GitHub의 `pwsh` 셸은 `-command`로 스크립트를 부르므로, 마지막 네이티브 명령이 0이 아니면 스텝 종료 코드가 1이 된다. 스크립트 끝에 `exit 0`을 넣었다 |

## 결론
* **반입본은 위 명령 하나로 만든다.** 트리밍·압축은 쓰지 않는다.
  * 압축은 크기를 절반 이하로 줄이지만 매 실행 약 0.1초가 늘어난다. 반입 zip이 어차피 압축하므로 이득이 작다.
  * 위성 어셈블리 제한(`en;ko`)은 7% 줄이지만 필수가 아니어서 쓰지 않는다.
* SDK·설정·네트워크 없이 자체 점검 여섯 행이 통과한다.
* 실제 폐쇄망 PC(사용자 권한·백신·`%TEMP%` 정책)에서의 첫 실행은 **Windows 미확인**이다. CI 러너는 관리자 권한이다. M4 현장 확인에서 잰다.
