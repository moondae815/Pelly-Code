# 실행 중 외부 통신 없음 (보안 심사용)

pelly는 실행 중에 설정된 서버 둘에만 접속합니다. 그 밖의 곳에는 접속하지 않습니다.
이 문서는 그 근거와 확인 방법을 적습니다. (PRD §5)

## 1. 접속 대상

| 대상 | 프로토콜 | 어디서 정하는가 | 언제 접속하는가 |
| :--- | :--- | :--- | :--- |
| Ollama 서버 하나 | HTTP | 설정 `llm.endpoint` | `pelly cs`, `pelly sql`, `pelly doctor` |
| SQL Server 하나 | TDS (SQL Server 기본 프로토콜) | 설정 `sql.connectionString` 또는 환경 변수 `PELLY_SQL_CONNECTION` | `pelly sql`, `pelly doctor`(연결 문자열이 있을 때만) |

* 두 주소 모두 사용자가 정합니다. 코드에 고정된 주소는 없습니다.
* 업데이트 확인, 텔레메트리, 오류 보고, 라이선스 확인은 없습니다.
* `pelly doctor`의 `SqlClient` 자체 점검은 존재하지 않는 로컬 이름 있는 파이프에 연결을 시도합니다. 네트워크로 나가지 않습니다.
* 반입 zip 안의 실행 파일은 위 두 곳 말고 다른 곳에 접속하지 않습니다.
* 빌드·패키징(CI)에서 NuGet과 도구를 내려받는 것은 예외입니다. 그것은 반입 전 단계입니다.

## 2. 프록시 동작

pelly는 프록시를 직접 설정하지 않습니다. `HttpClient`는 .NET 기본값(`SocketsHttpHandler`, `UseProxy = true`)을 그대로 씁니다.
따라서 .NET 기본 프록시 규칙을 따릅니다.

* 환경 변수 `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, `NO_PROXY`(소문자도 인식)가 있으면 그 값을 씁니다.
* Windows에서 위 환경 변수가 없으면 시스템(인터넷 옵션) 프록시 설정을 씁니다. Windows 미확인.
* `llm.endpoint`가 `http://`이면 `HTTP_PROXY`가 적용됩니다. `https://`이면 `HTTPS_PROXY`가 적용됩니다.
* 프록시가 걸린 PC에서 사내 Ollama 서버(예: `ollama.internal`)에 직접 가야 한다면 `NO_PROXY`에 그 호스트를 넣으십시오.
  * 예: `NO_PROXY=ollama.internal,10.0.0.0/8`
  * 안 넣으면 요청이 사내 프록시로 갑니다. 프록시가 사내 주소를 모르면 연결에 실패합니다.
* `localhost`, `127.0.0.1`처럼 loopback 주소는 .NET 환경 변수 프록시가 기본으로 우회한다고 알려져 있습니다. 이 문서 작성 중 loopback 우회를 따로 측정하지는 않았습니다.
* SQL 연결은 `HttpClient`를 쓰지 않으므로 HTTP 프록시와 무관합니다. SQL 서버로 직접 TCP 연결합니다.
* 서버 앞에 인증 프록시가 있으면 `llm.headers`로 헤더를 지정합니다. 프록시 자체의 인증 정보는 pelly가 다루지 않습니다.

## 3. 이 정책 테스트가 막는 것

`tests/PellyCoder.Core.Tests/Policy/NetworkPolicyTests.cs`가 `src/` 아래 모든 `*.cs`를 훑습니다. 대상은 `src/`뿐입니다.
`eval/`은 개발자용 벤치마크 도구이고 반입본에 들어가지 않으므로 훑지 않습니다. `bin/`, `obj/`도 제외합니다.
주석과 문자열 안의 언급은 지우고 검사합니다. 위반하면 파일과 줄 번호를 출력하며 실패합니다.

| 생성 지점 | 허용 파일 |
| :--- | :--- |
| `new HttpClient(`, `SocketsHttpHandler` | `src/PellyCoder.Cli/Composition.cs` |
| `new SqlConnection(` | `src/PellyCoder.Core/Sql/SqlServerExecutor.cs` |
| `TcpClient`, `UdpClient`, `new Socket(`, `WebRequest`, `WebClient`, `Dns.` | 어디에도 없어야 함 |
| `new Process`, `Process.Start(`, `new ProcessStartInfo` | `src/PellyCoder.Core/Processes/SystemProcessRunner.cs` |

* 이미 만들어진 `HttpClient`를 받아 쓰는 것은 생성이 아니므로 검사하지 않습니다.
* 같은 테스트 파일이 `Directory.Packages.props`에 `ApplicationInsights`, `OpenTelemetry`, `Sentry`가 없음을 확인합니다.
* 한계: 이 테스트는 소스의 생성 지점만 봅니다. 이미 있는 라이브러리 내부의 통신(예: SqlClient 자체 동작)은 보지 않습니다. 그래서 4절의 실행 중 확인이 필요합니다.
* 외부 프로세스는 `dotnet`, MSBuild, 사용자가 설정한 테스트 명령만 띄웁니다. 그 프로세스가 무엇에 접속하는지는 pelly가 통제하지 않습니다. 예를 들어 `dotnet build`는 복원 단계에서 NuGet에 접속할 수 있습니다. 폐쇄망에서는 NuGet 원본을 사내 미러나 로컬 캐시로 두십시오.

## 4. 디스크에서 읽고 쓰는 것

| 위치 | 동작 | 내용 |
| :--- | :--- | :--- |
| `<작업 공간>/pelly.json` | 읽기 | 작업 공간 설정 |
| 사용자 설정 (`%APPDATA%\pelly\config.json`, macOS는 `~/Library/Application Support/pelly/config.json`) | 읽기 | 사용자 설정 |
| `<작업 공간>/.pelly/runs/<runId>/` | 쓰기 | 이벤트 로그, 시도별 텍스트, `summary.json`, 시험 결과 `tests/*.trx` |
| `<작업 공간>/.pelly/backup/<runId>/` | 쓰기 | 수정 전 원본 파일 사본 |
| `<작업 공간>/.pelly/lock`, `.pelly/.gitignore` | 쓰기 | 동시 실행 방지, 추적 제외 |
| 수정 대상 파일 옆 `*.pelly-tmp` | 쓰기 | 원자적 저장용 임시 파일. 저장 뒤 이름이 바뀝니다 |
| 환경 변수 | 읽기 | `PELLY__*`, `PELLY_SQL_CONNECTION`, 프록시 변수 |

* 시험 결과 임시 폴더는 시스템 임시 폴더가 아니라 위 `.pelly/runs/<runId>/tests/`입니다. `src/`는 시스템 임시 폴더를 쓰지 않습니다.
* 비밀번호가 있는 SQL 연결 문자열은 설정 파일에 두지 못하게 막고 `PELLY_SQL_CONNECTION`으로만 받습니다.

## 5. 확인 방법

pelly 실행 중에 연결 목록을 봅니다. 기대 결과는 설정한 Ollama와 SQL 서버 주소뿐입니다.

### Windows (Windows 미확인)

1. 터미널 하나에서 `pelly doctor` 또는 `pelly cs ...`를 실행합니다.
2. 다른 터미널에서 실행합니다.
   * `netstat -ano | findstr ESTABLISHED`
3. `tasklist | findstr pelly`로 pelly의 PID를 찾아 `netstat -ano` 결과의 PID 열과 맞춥니다.
4. 그 PID의 원격 주소가 `llm.endpoint`와 SQL 서버 주소뿐인지 봅니다.
5. 대안: 리소스 모니터(`resmon`) → 네트워크 탭 → 「TCP 연결」에서 `pelly.exe`를 선택해 원격 주소를 봅니다.
6. 실행이 짧으면 연결이 금방 사라집니다. `pelly cs`로 긴 요청을 걸어 놓고 보십시오.

### macOS (실측함)

```
lsof -nP -i -a -c pelly
```

`pelly doctor`를 반복 실행하는 동안 반복 조회한 결과의 모양입니다 (로컬 Ollama로 측정, 주소는 localhost뿐).

```
COMMAND   PID      USER   FD   TYPE DEVICE SIZE/OFF NODE NAME
pelly   64495 <user>  126u  IPv6 0x...      0t0  TCP 127.0.0.1:52506->127.0.0.1:11434 (ESTABLISHED)
```

연결은 Ollama 포트(11434) 하나였고 다른 원격 주소는 없었습니다. SQL 연결 문자열을 설정하지 않아 SQL 점검은 건너뛰었습니다.

### 더 엄격하게 보려면

* 방화벽에서 pelly 실행 파일의 외부 송신을 Ollama와 SQL 서버 주소만 허용하도록 제한합니다.
* 그래도 pelly가 정상 동작하면 다른 곳에 의존하지 않는 것입니다.
