# 실행 중 외부 통신 없음 (보안 심사용)

pelly는 실행 중에 설정된 서버 둘에만 접속합니다. 그 밖의 곳에는 접속하지 않습니다.
이 문서는 그 근거와 확인 방법을 적습니다. (PRD §5)

## 1. 접속 대상

| 대상 | 프로토콜 | 어디서 정하는가 | 언제 접속하는가 |
| :--- | :--- | :--- | :--- |
| Ollama 서버 하나 | HTTP | 설정 `llm.endpoint` | `pelly cs`, `pelly sql`, `pelly doctor` |
| SQL Server 하나 | TDS (SQL Server 기본 프로토콜) | 설정 `sql.connectionString` 또는 환경 변수 `PELLY_SQL_CONNECTION` | `pelly sql`, `pelly doctor`(연결 문자열이 있을 때만) |

* 두 주소 모두 사용자가 정합니다. 설정이 없을 때 `llm.endpoint` 기본값은 `http://localhost:11434`(같은 PC)입니다. SQL 기본 주소는 없습니다.
* 업데이트 확인, 텔레메트리, 오류 보고, 라이선스 확인은 없습니다.
* 숨김 옵션 `pelly doctor --offline-selftest`(패키지 점검용)에만 SqlClient 자체 점검 행이 있습니다. 일반 `pelly doctor`에는 없습니다.
  * Windows에서는 존재하지 않는 로컬 이름 있는 파이프(`np:\\.\pipe\pelly-selftest-none`)를 열어 봅니다. 네트워크로 나가지 않습니다. Windows 미확인(CI windows-latest에서만 실행).
  * 그 밖의 OS에서는 연결 객체를 만들기만 하고 열지 않습니다.
* 반입 zip 안의 실행 파일은 위 두 곳 말고 다른 곳에 접속하지 않습니다.
* 빌드·패키징(CI)에서 NuGet과 도구를 내려받는 것은 예외입니다. 그것은 반입 전 단계입니다.
* pelly가 직접 열지는 않지만 OS가 대신 만드는 통신이 있을 수 있습니다. 모두 Windows 미확인입니다.
  * DNS: 설정한 Ollama·SQL 호스트 이름을 OS 리졸버가 조회합니다.
  * `Integrated Security=true`: Windows 인증을 위해 OS가 도메인 컨트롤러(Kerberos/NTLM)와 통신합니다.
  * TLS(`https://` 엔드포인트, `Encrypt=True`이고 `TrustServerCertificate`가 아닌 SQL): OS가 인증서 체인 확인을 위해 CRL·OCSP·중간 인증서를 내려받으려 할 수 있습니다.
  * 이 통신은 pelly 프로세스가 아니라 OS 서비스(예: `lsass`, DNS 클라이언트)에서 나갈 수 있어 5절의 PID 확인에 안 보일 수 있습니다.

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

작성 방법: `src/`에서 `File.`, `Directory.`, `FileStream`, `IFileSystem`(`fs.File.`/`fs.Directory.`)의 읽기·쓰기·삭제·열거 호출을 grep으로 모두 뽑아 파일별로 분류했습니다. 임베디드 리소스(토크나이저)는 디스크 읽기가 아니라서 뺐습니다.
`src/`의 쓰기는 아래 표가 전부입니다. 그 밖에 .NET 단일 파일 호스트가 압축을 푸는 자리(표 마지막 행)가 있습니다. 쓰는 곳은 모두 로컬 디스크입니다.

### 쓰기

| 위치 | 언제 | 내용 |
| :--- | :--- | :--- |
| 수정 대상 소스 파일 | `pelly cs`, `pelly sql`(`--dry-run` 제외) | 모델이 만든 수정. 임시 파일 `<파일>.pelly-tmp` 또는 같은 폴더의 `.<이름>.pelly-tmp`에 쓴 뒤 이름을 바꿉니다 |
| `<작업 공간>/.pelly/runs/<runId>/` | 실행마다 | `events.jsonl`, `attempt-<n>/`의 `prompt.txt`·`think.txt`·`output.txt`·`feedback.txt`, `summary.json`, `messages.txt`, 시험 결과 `tests/*.trx` |
| `<작업 공간>/.pelly/backup/<runId>/` | 파일 수정 전 | 원본 파일 사본. 되돌릴 때 이 사본을 읽습니다(읽기 표 참고) |
| `<작업 공간>/.pelly/lock`, `.pelly/.gitignore` | 실행마다 | 동시 실행 방지, 추적 제외(`*`) |
| `.pelly/runs/` 아래 오래된 실행 폴더 | 실행 종료 시 | `log.retentionDays`(기본 30일)가 지난 폴더를 삭제합니다 |
| 시험 결과 파일 | `pelly cs`의 시험 단계 | 실행 전 이전 `.trx`를 지우고 시험 도구가 새로 씁니다(`.pelly/runs/<runId>/tests/`) |
| `--out` 폴더, 없으면 현재 폴더 | `pelly doctor --sanitize` | `pelly-doctor-<날짜시각>.json`, `.md` (같은 초에 겹치면 `-2` 식 접미사) |
| `--out` 폴더, 없으면 현재 폴더 | `pelly report --sanitize` | `pelly-report-<날짜시각>.json`, `.md` (같은 방식) |
| `%TEMP%\.net\pelly\<해시>\` (환경 변수 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`가 있으면 `<그 값>\pelly\<해시>\`) | `pelly.exe` 첫 실행(그 버전의 첫 실행) | .NET 단일 파일 호스트가 네이티브 `Microsoft.Data.SqlClient.SNI.dll`(약 0.5 MB) 하나를 풉니다. pelly 코드가 아니라 .NET 호스트가 씁니다. 다음 실행부터는 풀린 파일을 다시 씁니다. CI `windows-latest`에서 측정(docs/spikes/S6-single-file.md), 현장 PC는 Windows 미확인 |

* `src/`의 코드는 시스템 임시 폴더(`GetTempPath`)를 쓰지 않습니다. 다만 위 마지막 행처럼 .NET 호스트가 `%TEMP%` 아래에 씁니다. AppLocker·WDAC가 `%TEMP%`의 DLL 실행을 막는 PC라면 [install.md](install.md)의 「압축 해제 폴더」를 보십시오.
* 두 `--sanitize` 출력은 허용 목록 방식의 비식별 결과입니다(`--help` 설명 기준).

### 읽기

| 위치 | 언제 | 내용 |
| :--- | :--- | :--- |
| `<작업 공간>/pelly.json` 또는 `--config`로 준 파일 | 모든 명령 | 작업 공간 설정 |
| 사용자 설정 (Windows `%APPDATA%\pelly\config.json`, macOS `~/Library/Application Support/pelly/config.json`) | 모든 명령 | 사용자 설정 |
| `llm.template`에 지정한 파일 | 지정했을 때 | 사용자가 정한 채팅 템플릿 |
| 요청한 소스 파일(`pelly cs`/`sql`의 대상), 같은 작업 공간의 `*.cs` | `pelly cs`, `pelly sql` | 대상 파일과 DTO 수집 등 문맥 |
| `*.csproj`, `Directory.Build.props`, `*.sln`, `global.json` | 빌드·시험 단계, `pelly doctor --project` | 프로젝트 종류·언어 버전·시험 프로젝트 찾기 |
| `.pelly/runs/*/summary.json`, `.pelly/runs/<runId>/tests/*.trx` | `pelly report`, 시험 단계 | 실행 기록, 시험 결과 |
| `<작업 공간>/.pelly/backup/<runId>/` | 검증 실패·거부·중단으로 되돌릴 때 | 원본 파일 사본을 읽어 대상 파일에 다시 씁니다 |
| 작업 공간 루트 판정 | 모든 명령 | `.git`, `.svn`, `*.sln` 존재 여부와 폴더 열거 |
| `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe` | 레거시 빌드 검증(Windows) | MSBuild 위치 찾기. 있으면 실행합니다. Windows 미확인 |
| `build.msbuildPath`로 지정한 MSBuild | 지정했을 때 | 존재 확인 |
| `%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\<버전>` | `pelly doctor --project`(Windows) | 존재 확인만 합니다. Windows 미확인 |
| 환경 변수 | 시작 시 | `PELLY__*`, `PELLY_SQL_CONNECTION`, `ProgramFiles(x86)`, 프록시 변수. 전체 환경 변수를 한 번 읽어 설정에 반영합니다 |

* 비밀번호가 있는 SQL 연결 문자열은 설정 파일에 두지 못하게 막고 `PELLY_SQL_CONNECTION`으로만 받습니다.

### 띄우는 프로세스

디스크 읽기·쓰기 말고 pelly가 띄우는 외부 프로세스도 있습니다. 목록과 접속 범위는 3절 끝을 보십시오.

* `pelly doctor`는 .NET SDK 확인을 위해 `dotnet --version`을 실행합니다(현재 폴더에서, 결과의 버전 문자열만 읽음). 이 프로세스의 접속은 따로 측정하지 않았습니다.
* pelly는 띄우는 `dotnet`에 텔레메트리 끄기 변수를 넣지 않습니다. .NET CLI는 기본으로 사용 통계를 보낼 수 있으니 폐쇄망 PC에는 사용자 환경 변수 `DOTNET_CLI_TELEMETRY_OPTOUT=1`을 두십시오. Windows 미확인.
* `pelly cs`의 빌드·시험 단계는 `dotnet build`/`dotnet run`/`dotnet test`, MSBuild, `test.command`를 실행합니다. 이 프로세스가 쓰는 `bin/`·`obj/`와 그 접속은 pelly가 통제하지 않습니다.

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
