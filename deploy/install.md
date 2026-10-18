# 설치 가이드

폐쇄망 PC에 pelly를 설치하는 방법입니다. 관리자 권한과 .NET 런타임 설치는 필요 없습니다.
(실행 파일에 .NET 런타임이 들어 있습니다. 단, 아래 「사전 조건」의 빌드 도구는 별도입니다.)

## 반입 zip 구성

`pelly-0.9.0-win-x64.zip`을 풀면 이렇게 나옵니다.

| 파일 | 용도 |
| :--- | :--- |
| `pelly.exe` | 실행 파일 (단일 파일) |
| `pelly.json.template` | 설정 템플릿 |
| `SHA256SUMS.txt` | 파일별 SHA-256 체크섬 |
| `sbom.cdx.json` | SBOM (CycloneDX), 보안 심사용 |
| `THIRD-PARTY-NOTICES.md` | 서드파티 라이선스 목록 |
| `licenses/Microsoft.Data.SqlClient.SNI.runtime.LICENSE.txt` | 예외 승인한 SQL Server 네이티브 클라이언트(SNI)의 라이선스 전문 |
| `version-record.json` | pelly 버전, Ollama 버전, 개발 PC 모델의 태그·digest·형식·양자화. 현장 모델(GGUF)의 SHA-256은 M4에서 기록합니다 |
| `benchmark-summary.md` | 벤치마크 결과 요약 |
| `docs/` | 이 문서들 (`install.md`, `usage.md`, `ide-external-tools.md`, `server-checklist.md`, `no-network.md`) |

## 사전 조건

* 접속할 수 있는 Ollama 서버. 서버 관리자에게 [server-checklist.md](server-checklist.md)를 전달하십시오.
* `dotnet` SDK(.NET 10, 개발 때 쓴 버전은 10.0.302)가 PATH에 있어야 합니다. C# 빌드 검증에 필요합니다. **SQL만 쓰는 PC도 `dotnet`을 실행할 수 없으면 `pelly doctor`가 Fail(종료 코드 4)로 끝납니다.** 이 행의 안내는 「.NET 10 SDK를 설치하고 PATH를 확인하세요」입니다.
* 레거시 .NET Framework 프로젝트는 Visual Studio(또는 Build Tools)의 MSBuild가 필요합니다.
* SQL 수정을 쓰려면 SQL Server 접속 정보와 `VIEW DEFINITION` 권한이 필요합니다.

## 1. zip 풀기

원하는 폴더에 풉니다. 예: `C:\Tools\pelly`.

## 2. 체크섬 확인

PowerShell에서 실행합니다.

```powershell
cd C:\Tools\pelly
Get-FileHash .\pelly.exe -Algorithm SHA256
Select-String -Path .\SHA256SUMS.txt -Pattern 'pelly.exe'
```

두 해시가 같아야 합니다. `Get-FileHash`는 대문자, `SHA256SUMS.txt`는 소문자로 보여 줍니다. 대소문자는 무시하고 비교하십시오.

`SHA256SUMS.txt`는 한 줄에 파일 하나입니다. 형식은 `<소문자 SHA-256>  <zip 안 경로>`입니다(공백 두 칸, 경로 구분은 `/`). `SHA256SUMS.txt` 자신은 들어 있지 않습니다.
전체 파일을 한 번에 확인하려면 이렇게 실행합니다. 모든 줄이 `OK`여야 합니다.

```powershell
Get-Content .\SHA256SUMS.txt | ForEach-Object {
  $hash, $file = $_ -split '  ', 2
  $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
  if ($actual -eq $hash) { "OK  $file" } else { "다름  $file" }
}
```

(이 명령은 CI `windows-latest`의 PowerShell 7에서 확인했습니다. Windows PowerShell 5.1은 미확인입니다.)

## 3. PATH에 추가

PowerShell에서 사용자 PATH에 추가합니다. 관리자 권한은 필요 없습니다.

```powershell
[Environment]::SetEnvironmentVariable('Path', [Environment]::GetEnvironmentVariable('Path','User') + ';C:\Tools\pelly', 'User')
```

새 터미널을 열어 확인합니다.

```powershell
pelly --version
```

`0.9.0`으로 시작하면 됩니다. (Windows 미확인: PATH 반영은 새 터미널부터입니다. IDE는 재시작해야 할 수 있습니다.)
PATH를 건드리지 않고 `pelly.exe` 전체 경로로 실행해도 됩니다. IDE External Tools는 전체 경로를 씁니다.

## 4. 설정 파일

`pelly.json.template`을 복사해 고칩니다. 둘 중 한 곳에 둡니다.

| 위치 | 용도 |
| :--- | :--- |
| `%APPDATA%\pelly\config.json` | 내 PC 전체에 적용 (사용자 설정) |
| `<작업 공간 루트>\pelly.json` | 그 작업 공간에만 적용. 사용자 설정을 덮어씁니다 |

작업 공간 루트는 대상 파일에서 위로 올라가며 처음 만나는 `.git`, `.svn` 또는 `*.sln`이 있는 폴더입니다.
`--config <경로>`로 파일을 직접 지정할 수도 있습니다.

```powershell
New-Item -ItemType Directory -Force "$env:APPDATA\pelly"
Copy-Item .\pelly.json.template "$env:APPDATA\pelly\config.json"
notepad "$env:APPDATA\pelly\config.json"
```

(Windows 미확인: `%APPDATA%` 경로 동작.)

고칠 값은 이렇습니다.

| 키 | 설명 |
| :--- | :--- |
| `llm.endpoint` | Ollama 서버 주소. 예: `http://ollama.internal:11434` |
| `llm.model` | 서버에 올라 있는 모델 태그 |
| `sql.allowedHosts` | pelly sql이 접속할 서버 이름. **비어 있으면 SQL은 접속하지 않고 종료 코드 5** |
| `sql.allowedDatabases` | 접속할 데이터베이스. 비어 있으면 위와 같음 |
| `sql.connectionString` | 비밀번호 없는 연결 문자열. `Server=<서버>;Database=<DB>;Integrated Security=true;TrustServerCertificate=True` 형태 |

* 연결 문자열에 `Password`가 있으면 설정 오류(종료 코드 4)입니다. 비밀번호가 필요한 SQL 인증은 환경 변수 `PELLY_SQL_CONNECTION`으로만 지정합니다.
* SQL을 쓰지 않으면 `sql` 항목은 비워 두어도 됩니다.
* `sql.allowedHosts` 항목은 연결 문자열의 `Server=` 뒤에 쓴 값 그대로(또는 그 호스트 부분)여야 합니다. 대소문자는 구분하지 않습니다. 예: `Server=db01\SQL2022`이면 `db01\SQL2022` 또는 `db01`, `Server=tcp:db01,1433`이면 `db01`. IP로 접속하면 IP를 적습니다. `.`나 `(local)`은 바꿔 주지 않으니 호스트 이름을 쓰십시오.
* 설정은 `내장 기본값 → 사용자 설정 → 작업 공간 pelly.json → 환경 변수 PELLY__<섹션>__<키> → 명령줄 옵션` 순으로 합쳐지고 뒤의 것이 이깁니다.
* 프록시가 있는 PC에서 사내 Ollama에 직접 가야 하면 [no-network.md](no-network.md)의 프록시 절을 보십시오.

## 5. 확인: `pelly doctor`

```powershell
pelly doctor
```

Ollama 접속, 모델, 토크나이저, 응답 시간, .NET SDK, MSBuild, SQL 연결, 서버 확인 사항을 표로 보여 줍니다.
하나라도 Fail이면 종료 코드 4입니다. Fail 행의 안내대로 고친 뒤 다시 실행하십시오.

* 레거시 프로젝트를 쓰면 `pelly doctor --project <csproj>`로 .NET Framework 타겟팅 팩이 설치됐는지도 봅니다. (Windows 미확인)
* 문제를 외부에 문의할 때는 `pelly doctor --sanitize`가 만든 비식별 결과만 보내십시오. 반출 가능 여부는 사내 보안 규정을 따릅니다.

## 대안: `dotnet tool install`

로컬 피드로 설치하는 방법은 M4에서 결정합니다. 이 패키지에는 .nupkg가 들어 있지 않습니다.

## 업데이트

새 zip을 같은 방법으로 반입하고 `pelly.exe`를 바꿉니다. 설정 파일은 그대로 둡니다.
`version-record.json`으로 버전을 확인하십시오. 샘플링·예산 같은 값은 설정 파일로 바꿀 수 있어서 재반입이 필요 없습니다.

## 다음 문서

* 사용법: [usage.md](usage.md)
* IDE 연동: [ide-external-tools.md](ide-external-tools.md)
* 실행 중 외부 통신: [no-network.md](no-network.md)
