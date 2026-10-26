# Ollama 서버 확인 사항 (서버 관리자용)

pelly는 서버 설정을 바꾸거나 읽을 수 없습니다. 아래 항목을 서버 관리자가 확인해 주세요. (PRD §7.1)

| 설정 | 권장 값 | 이유 | 확인 방법 |
| :--- | :--- | :--- | :--- |
| Ollama 버전 | 외부 개발 환경과 같은 버전 | raw 모드·캐시 동작이 버전마다 다를 수 있습니다 | `pelly doctor`가 `/api/version`으로 자동 확인 |
| 모델 | `Qwen3.6-35B-A3B` GGUF `Q4_K_M`, 외부와 같은 파일 | 결과 재현성 | GGUF 파일의 SHA-256을 외부 값과 비교. 현장 모델의 외부 값은 M4에서 기록합니다. Linux 서버: `sha256sum <ollama 모델 저장소의 blob 파일>`. Windows 서버: `Get-FileHash -Algorithm SHA256 <파일>`. blob 파일은 `ollama show <모델> --modelfile`의 `FROM` 줄이 가리키는 경로(모델 저장소의 `blobs` 폴더에서 가장 큰 파일)입니다. 그 파일 이름 `sha256-<hex>`의 `<hex>`가 이미 해시이므로 계산 결과와 같은지 보면 됩니다. 단 모델 digest(매니페스트)와 GGUF 해시는 다른 값입니다 |
| `OLLAMA_FLASH_ATTENTION` | `1` | KV 캐시 양자화를 쓰려면 필요합니다 | 서버 환경 변수 확인 |
| `OLLAMA_KV_CACHE_TYPE` | `q8_0` (4비트 금지) | 변수명 철자와 괄호 짝 기억력을 지키면서 VRAM을 줄입니다 | 서버 환경 변수 확인 |
| `OLLAMA_NUM_PARALLEL` | 동시 사용자 수에 맞춰 협의 | 슬롯 수만큼 KV 메모리(8K × 슬롯)가 늘어납니다. 1이면 요청이 대기합니다 | 서버 환경 변수 확인 |
| `OLLAMA_HOST` | 폐쇄망 내부 주소로 바인딩 | 개발 PC에서 원격으로 접속할 수 있어야 합니다 | `pelly doctor`의 연결 확인 |
| `OLLAMA_KEEP_ALIVE` | 업무 시간 동안 언로드되지 않는 값 | 모델을 다시 올리면 첫 응답이 느려집니다. pelly도 요청마다 `keep_alive: "30m"`을 지정합니다 | `pelly doctor`가 `/api/ps`로 자동 확인 |

pelly가 요청마다 지정하는 값: `options.num_ctx = 8192`, `keep_alive = "30m"`, 단계별 샘플링 값.

## 운영 SQL Server 읽기 전용 계정 (DB 관리자용, V2-1d)

pelly는 운영 DB의 저장 프로시저·함수·뷰 정의와 참조 관계를 **읽기만** 합니다(`pelly sql pull`·`pelly sql check`·영향 탐색). 운영에 쓰는 문장은 보내지 않으므로 계정에는 아래 두 권한만 주십시오. 개발 DB용 계정과 분리하는 것을 권장합니다.

```sql
-- 서버: 로그인 (Windows 인증 계정이면 CREATE LOGIN [DOMAIN\account] FROM WINDOWS;)
CREATE LOGIN pelly_prod_ro WITH PASSWORD = '<강한 비밀번호>', CHECK_POLICY = ON;

-- 운영 DB: 사용자, 접속, 정의 보기
USE [<운영 DB>];
CREATE USER pelly_prod_ro FOR LOGIN pelly_prod_ro;
GRANT CONNECT TO pelly_prod_ro;
GRANT VIEW DEFINITION TO pelly_prod_ro;   -- 데이터베이스 범위 권한
```

* `db_datareader` 등 역할은 넣지 않습니다. 테이블 데이터는 읽을 수 없어야 합니다.
* 비밀번호가 있는 로그인이면 연결 문자열은 환경 변수 `PELLY_SQL_PROD_CONNECTION`으로만 지정합니다(설정 파일에 비밀번호를 쓰면 설정 오류).
* pelly 설정에서 `sql.production.allowedHosts`·`sql.production.allowedDatabases`로 운영 서버·DB를 허용합니다.
* 확인: `pelly doctor`의 「SQL 운영(읽기 전용)」 행. 정상이면 Ok(`<서버>/<DB>, 읽기 전용`), 권한이 없으면 Fail(`VIEW DEFINITION 권한 필요` — 운영 DB에서 읽기 전용 계정에 GRANT VIEW DEFINITION 권한을 부여하라는 안내), 접속할 수 없으면 Fail이고 종료 코드 4입니다. 운영 연결을 설정하지 않았으면 Ok(`운영 연결 없음(선택)`)입니다.
