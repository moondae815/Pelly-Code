# Pelly-Coder CLI 제품 요구사항 정의서 (PRD)
> **타겟 모델:** `Qwen3.6-35B-A3B` (4-bit Quantization)  
> **타겟 런타임:** .NET 10 (LTS) CLI  
> **운영 환경:** 폐쇄망 Windows 11 x64 개발 PC + 폐쇄망 내부 원격 Ollama 서버  
> **개발 환경:** 외부 개발 노트북(macOS) → 검증 후 폐쇄망으로 이관  
> **문서 버전:** v1.7 (2026-10-18)

### 변경 이력
| 버전 | 날짜 | 주요 변경 |
| :--- | :--- | :--- |
| v1.0 | 2026-10-17 | 초안 |
| v1.1 | 2026-10-17 | 표기 오류 수정(`pelly`, `<think>`), KPI 재정의(불변 조건/벤치마크/기준 하드웨어), .NET 10 단일화, 범위·Non-goals 추가, CLI UX·LOCATE·컨텍스트 예산·토큰 계산·런타임 백엔드 명세, Baseline 빌드·퍼지 매칭 규칙·인코딩·롤백 명세, SQL 안전장치(ScriptDom 가드·최소 권한·호스트 화이트리스트), 비기능 요구사항, 평가·테스트 전략, 로드맵 M0 추가, 미결 사항 정리 |
| v1.2 | 2026-10-17 | 기본 LLM 서버를 Ollama로 변경(`/api/generate` raw 모드, keep_alive, Flash Attention + KV 양자화), 주 사용 환경을 Windows로 지정, Windows 특화 요구사항(파일 잠금, 콘솔 인코딩, 경로, 통합 인증) 및 CI 매트릭스 추가, 미결 사항 Q8~Q10 추가 |
| v1.3 | 2026-10-17 | 폐쇄망 운영·외부 개발 후 이관 구조 반영: 원격 Ollama 서버(관리자 관리), 완전 오프라인 동작, self-contained 배포 패키지, 이관 절차(§11), 외부 개발 환경 구성(§8.5), 로그 반출용 비식별 리포트. 레거시 .NET Framework 프로젝트 빌드 검증을 v1에 포함(`MSBuild.exe` + `vswhere`, Q8 해소). 로드맵에 M4(폐쇄망 이관·현장 검증) 추가, 미결 사항 Q9~Q13 갱신 |
| v1.4 | 2026-10-17 | §12 v2 로드맵 추가: 작업 명세 파일과 순차 실행(`pelly apply`), 테스트 우선 검증, 프로젝트 규칙 파일. 진입 조건·v2 KPI·v2에서도 하지 않을 것 명시 |
| v1.5 | 2026-10-17 | IDE 연동(Visual Studio·Rider External Tools, `--line`, `--pause-on-exit`) 추가, v2 로드맵에 V2-0 GUI 승인 창 추가 |
| v1.6 | 2026-10-17 | M0 스파이크 결과 반영: think 단계를 교정 시도부터 사용(`llm.thinkMode`), think 예산은 서버 `num_predict`로 제한, SQL 최소 권한 `VIEW DEFINITION`, 토크나이저 확정, 미결 사항 Q1~Q4 해소 |
| v1.7 | 2026-10-18 | M2a 반영(상세는 설계 문서 §3 D24·D25, §7.3, §8.4, §8.7.1, §8.9): FR-00 옵션 정리(`--line`, `--strict-baseline`, `--warn-as-error`, `--method`·요청문 선택, 요청문 생략 시 대화형 입력), FR-06 Baseline을 LOCATE 끝에서 시작하고 CS 외 오류면 `BuildUnverifiable`로 중단, 모델 제어 토큰 문자열 차단(종료 코드 4), 멤버 모양 검사 구체화 |

---

## 1. 제품 개요 (Product Overview)

### 1.1 프로젝트 명칭 및 정의
* **프로젝트 명칭:** **Pelly-Coder CLI** (Small-Active MoE 특화 결정론적 코딩 에이전트)
* **실행 명령어:** `pelly`
* **배포 형태:**
  * **폐쇄망 (운영):** `win-x64` self-contained 단일 실행 파일 zip 패키지. 대상 PC에 .NET 10 런타임이 없어도 동작합니다(§11).
  * **외부 개발 환경:** .NET Global Tool (패키지 ID: `PellyCoder.Cli`)
* **대상 모델:** `Qwen3.6-35B-A3B` (GGUF `Q4_K_M`, 폐쇄망 원격 Ollama 서버로 서빙)
* **실행 방식:** 일반 JIT 실행. Roslyn·`Spectre.Console.Cli`의 리플렉션 의존성 때문에 **Native AOT와 트리밍은 v1 범위에서 제외**합니다.

### 1.2 기획 배경 및 문제 정의
`Qwen3.6-35B-A3B`는 전체 35B 파라미터 중 토큰당 3B만 활성화되는 구조로 사내 GPU 서버에서 높은 추론 속도를 제공합니다. 폐쇄망에서는 외부 코딩 AI 서비스를 쓸 수 없으므로 이 모델이 현실적인 선택지입니다. 하지만 기존 자율 에이전트(ReAct 방식)에 4비트 양자화 모델을 그대로 연결하면 다음 한계가 나타날 것으로 예상합니다. (아래 수치는 가설이며 **M0에서 측정으로 확인**합니다.)
1. **컨텍스트 열화 (Lost in the Middle):** 입력 프롬프트가 길어질수록(가설: 8K 토큰 초과) 기존 변수명이나 아키텍처 규칙을 망각함.
2. **Tool Calling 파싱 실패:** 다중 중첩 JSON 스키마 안에 C# 코드나 SQL 쿼리를 이스케이프(`\"`, `\n`)하여 생성할 때 구문 오류 및 무한 재시도 루프 발생.
3. **논리적 환각 (Hallucination):** 활성 파라미터 3B의 추론 한계로 존재하지 않는 .NET API, Dapper 확장 메서드, 잘못된 테이블 컬럼명을 생성함.

### 1.3 제품 목표
모델의 자율성에 의존하는 대신 **엄격한 유한 상태 머신(FSM) 워크플로우, Roslyn AST 기반 마이크로 컨텍스트(Micro-Context), 컴파일러/DB 엔진 기반의 결정론적 검증 루프**를 결합하여, **"초기 응답 지연 1초 미만, 검증을 통과하지 못한 코드는 절대 남기지 않는"** 폐쇄망용 코딩 CLI 도구를 구축합니다.

### 1.4 범위 (Scope) 및 제외 범위 (Non-goals)
| 구분 | v1 포함 | v1 제외 (v2 이후 검토) |
| :--- | :--- | :--- |
| **C#** | 단일 파일 내 메서드·생성자·프로퍼티 수정, 같은 파일의 `using` 추가, 같은 클래스 내 `private` 헬퍼 메서드 추가, 한 파일 내 다중 SEARCH/REPLACE 블록. **SDK 스타일 프로젝트와 레거시 .NET Framework 프로젝트 모두 빌드 검증** | 다중 파일 동시 수정, 새 파일 생성, DTO/인터페이스 시그니처 변경, 대규모 리팩터링 |
| **SQL** | 리포지토리 내 `.sql` 파일의 단일 SP/함수/뷰 수정 | **DB 직접 배포(`ALTER` 실행)**, 다중 객체 동시 수정, 마이그레이션 생성 |
| **환경** | 폐쇄망 원격 Ollama 서버 연동, **인터넷 없이 완전 오프라인 동작** | 인터넷 접속이 필요한 기능 일체(자동 업데이트, 텔레메트리, 모델·토크나이저 다운로드), 외부 클라우드 LLM API |
| **공통** | 단발성 명령 실행 | 대화형 채팅 모드, IDE 플러그인 |

여러 작업에 걸친 변경, 시그니처 변경, 테스트 기반 검증은 v2에서 다룹니다(§12).

---

## 2. 핵심 성과 지표 (Target Metrics & KPIs)

### 2.1 측정 조건
* **KPI 최종 판정 환경 (폐쇄망):** 폐쇄망 Windows 개발 PC + 폐쇄망 Ollama 서버(사양 Q9). 응답 지연은 **네트워크 왕복을 포함**해 측정하며, 서버를 혼자 쓰는 상태(동시 요청 없음)를 기준으로 합니다.
* **사전 측정 환경 (외부):** §8.5 외부 개발 환경. 여기서 나온 수치는 참고치이며 KPI 판정에 쓰지 않습니다.
* **벤치마크 세트:**
  * **외부용:** 실제 코드 구조를 모사한 샘플 코퍼스 기반 C# 30건 + SQL 20건(§8.1)
  * **폐쇄망용:** 실제 코드베이스 기반 같은 규모의 세트(M4에서 폐쇄망 안에서 작성)
* 같은 모델 파일(GGUF SHA-256 일치)과 같은 Ollama 버전으로 측정합니다(Q10).

### 2.2 지표
| 지표 항목 | 목표 수치 (Target) | 측정 기준 및 달성 전략 |
| :--- | :--- | :--- |
| **검증 불변 조건** | **100% (예외 없음)** | 최종 승인된 패치는 항상 빌드/SQL 검증을 통과함. 통과하지 못하면 자동 롤백 (성공률 지표가 아닌 시스템 보장 사항) |
| **초기 응답 지연 (TTFT)** | **< 1.0초** | 입력 4,096 토큰 이내 제한 + 프롬프트 캐시 사용. 네트워크 왕복 포함. 참고: 외부 Mac(M5 Pro)에서 캐시 없는 4,300토큰 입력은 1.56초(S2) — 폐쇄망 서버 사양(Q9) 확인 후 목표 재검토 |
| **추론 예산 제어 (Thinking)** | **≤ 512 토큰** | 교정 시도부터 THINK 단계 사용(`llm.thinkMode = retry`). 서버 `num_predict = 512`로 제한하고, 잘리면 마지막 완결 줄까지 남긴 뒤 CODE 단계로 전환 (FR-05, 설계 D21·D22) |
| **패치 포맷 파싱 성공률** | **> 99.5%** | JSON Tool Calling 대신 `SEARCH/REPLACE` 텍스트 블록 파서 사용 |
| **최종 작업 성공률** | **≥ 85% (잠정)** | 최초 시도 1회 + 자가 교정 최대 3회 안에 검증 통과 및 판정 기준 충족. 폐쇄망 기준선 측정 후 확정 (Q6) |
| **단일 작업 완료 시간** | **중위값 < 15초 / P90 < 30초** | 코드 탐색 → 패치 생성 → 증분 빌드 검증까지. 대상 프로젝트 단위 빌드 기준. 레거시 프로젝트는 빌드 시간 실측 후 별도 목표 설정 가능 |

---

## 3. 시스템 아키텍처 및 상태 머신 워크플로우

### 3.1 배포 구성
```text
[폐쇄망]
┌──────────────────────────────┐          ┌──────────────────────────┐
│ Windows 11 개발 PC            │  HTTP    │ Ollama 서버 (GPU)         │
│  pelly.exe                   │ ───────> │  Qwen3.6-35B-A3B Q4_K_M  │
│  dotnet SDK / MSBuild.exe    │  NDJSON  │  (서버 관리자 관리)        │
│  대상 소스 코드               │ <─────── │                          │
└──────────────┬───────────────┘          └──────────────────────────┘
               │ SqlClient (Windows 인증)
               ▼
        개발 DB (SQL Server)
```

### 3.2 FSM 다이어그램
```text
[User Request]
      │
      ▼
[Phase 0: PREFLIGHT] ──(Ollama 서버 연결·모델 확인 / 설정 검증 / 제어 토큰 검사)  ·  Baseline 빌드·에러 스냅샷은 LOCATE 끝에서 시작
      │
      ▼
[Phase 1: LOCATE] ──(Roslyn 심볼 검색 / ripgrep / SQL 객체 검색)──> 대상 심볼 특정
      │        └──[후보 다수]──> 사용자 선택 (비대화형이면 종료 코드 3)
      ▼
[Phase 2: SLICE]  ──(Micro-Context Slicer)──> 수정 대상 + 참조 정의만 추출 (예산 §3.3)
      │
      ▼
[Phase 3: EDIT]   ──(2단계 생성: THINK ≤512 → CODE)──> SEARCH/REPLACE 블록
      │
      ▼
[Phase 4: APPLY]  ──(백업 → 퍼지 매칭 → 원자적 쓰기)
      │        └──[매칭 실패]──┐
      ▼                        │
[Phase 5: VERIFY] ──(Roslyn 구문 검사 → dotnet build 또는 MSBuild.exe / SQL 가드 → NOEXEC)
      │                        │
      ├──[신규 에러 있음]──────┴──> 재시도 횟수 < 3 ? ──[예]──> Phase 2(패치된 파일 기준 재SLICE)
      │                                              └─[아니오]──> [ROLLBACK] ──> 종료 코드 1
      ▼
[Phase 6: REVIEW] ──(최종 diff 표시 → 사용자 승인)──[거부]──> [ROLLBACK] ──> 종료 코드 2
      │
      ▼
   [DONE] ──> 종료 코드 0
```
* 재시도 횟수: **최초 시도 1회 + 자가 교정 최대 3회**. SEARCH 매칭 실패도 1회로 셉니다.
* 재시도할 때는 패치가 적용된 상태에서 에러 위치를 포함해 **다시 SLICE**합니다(Fix-forward).
* Ctrl+C 등 어느 단계에서 중단되더라도 ROLLBACK을 실행합니다.

### 3.3 컨텍스트 예산 (`num_ctx = 8192`)
| 구성 요소 | 상한 (토큰) | 비고 |
| :--- | ---: | :--- |
| 시스템 프롬프트 (few-shot 포함) | 600 | |
| 사용자 요청 | 200 | 초과하면 요약 요청 없이 에러로 거부 |
| 코드/스키마 컨텍스트 | 3,000 | 슬라이서가 우선순위대로 잘라냄 |
| 재시도 피드백 | 296 | **누적하지 않고 최신 피드백으로 교체** |
| **입력 합계 (하드 리밋)** | **4,096** | |
| Think 출력 | 512 | FR-05 |
| Code 출력 (`num_predict`) | 2,048 | |
| 여유분 | 1,536 | 템플릿 토큰·오차 흡수 |

### 3.4 토큰 계산 방식
Ollama에는 공개된 토큰화 전용 엔드포인트가 없으므로 다음과 같이 나눕니다.
* **사전 예산 계산 (입력):** `Microsoft.ML.Tokenizers`에 Qwen vocab(`tokenizer.json`)을 로드한 **로컬 토크나이저**로 계산합니다. chat template 토큰을 포함해 계산하고, 오차를 고려해 여유분(§3.3)을 둡니다. 폐쇄망에서는 다운로드할 수 없으므로 **토크나이저 파일을 실행 파일에 내장**합니다.
* **사후 보정:** 응답의 `prompt_eval_count`/`eval_count`를 로그에 기록하고, 로컬 계산값과의 오차를 벤치마크에서 주기적으로 확인합니다.
* **Think 예산 (실시간):** 스트림으로 받은 누적 텍스트를 로컬 토크나이저로 계산합니다. 스트림 청크 수는 참고값으로만 씁니다.

---

## 4. 상세 기능 요구사항 (Functional Requirements)

### FR-00. CLI 명령 및 사용자 경험
* **명령 형식:**
  ```text
  pelly cs     <file.cs>  ["<요청>"] [--class <Name>] [--method <Name>] [--line <n>]
  pelly sql    <file.sql> [--object <Name>] [--line <n>] ["<요청>"]
  pelly doctor                       # 환경 점검 (FR-08)
  pelly report [--sanitize] [--since <date>]   # 실행 기록 요약 (FR-09)
  ```
* **대상 지정 (`pelly cs`):** `--method`(+선택 `--class`), `--line <n>`(커서 줄이 속한 멤버, `--class`/`--method`와 함께 쓸 수 없고 1 이상), 또는 둘 다 생략(요청문의 식별자 → 없으면 모델이 멤버 목록에서 선택, 최대 60개). `--method`와 요청문은 모두 선택입니다. 요청문을 생략하면 대화형 실행에서는 입력을 묻고 비대화형이면 종료 코드 4입니다.
* **빌드 검증 옵션:** `--strict-baseline`(원본에 이미 있던 CS 오류도 종료 코드 4로 처리), `--warn-as-error`(새 경고도 오류로 취급).
* **입력 제한:** 요청문이나 대상 파일에 모델 제어 토큰 문자열(`<|im_end|>` 등)이 있으면 서버를 부르기 전에 종료 코드 4로 끝납니다.
* **공통 옵션:** `--dry-run`(패치만 보여주고 적용 안 함), `--yes`(최종 승인 생략), `--test`(검증 시 테스트 실행), `--max-retries <n>`(기본 3), `--warn-as-error`, `--verbose`(단계별 타이밍·프롬프트 출력), `--config <path>`
* **IDE 연동:** 사용자는 Visual Studio와 JetBrains Rider를 혼용합니다. 두 IDE의 External Tools에 `pelly cs "<현재 파일>" --line <커서 줄>`을 등록해, 커서가 있는 메서드를 대상으로 실행합니다. 요청문을 생략하면 콘솔에서 입력받습니다. `--pause-on-exit`는 결과를 보여준 뒤 키 입력을 기다립니다(External Tools 콘솔 창이 바로 닫히지 않게). 설정 가이드는 반입 패키지 문서에 포함합니다.
* **최종 승인 (REVIEW):** 검증을 통과하면 `Spectre.Console`로 컬러 diff를 표시하고 `[y/N]` 확인을 받습니다. 거부하면 원본으로 복구합니다.
* **종료 코드:**

  | 코드 | 의미 |
  | :--- | :--- |
  | 0 | 성공 (패치 적용) |
  | 1 | 검증 실패 → 롤백 |
  | 2 | 사용자 거부/중단 → 롤백 |
  | 3 | LOCATE 실패 또는 후보 모호 (비대화형) |
  | 4 | 환경 오류 (LLM 서버·DB·빌드 도구) |
  | 5 | 안전 가드에 의한 거부 (FR-07) |

### FR-01. 대상 탐색 (`SymbolLocator`)
1. `--class`/`--method`/`--object`가 지정되면 Roslyn(SQL은 ScriptDom)으로 정확히 일치하는 심볼을 찾습니다.
2. 지정되지 않았으면 요청문에서 식별자 후보(PascalCase 토큰, 따옴표·백틱 안 문자열)를 규칙 기반으로 추출해 파일 안 심볼과 대조합니다.
3. 그래도 특정되지 않으면 파일의 **심볼 이름·시그니처 목록만** 모델에 주고 하나를 고르게 합니다(출력은 목록 중 하나의 이름으로 제한).
4. 후보가 여러 개면 `SelectionPrompt`로 사용자에게 선택하게 하고, 비대화형(`--yes`/CI)이면 종료 코드 3으로 끝냅니다.

### FR-02. AST 및 스키마 기반 마이크로 컨텍스트 슬라이서 (Micro-Context Slicer)
* **C# / .NET 코드 슬라이싱 (`RoslynSlicer`):**
  * `Microsoft.CodeAnalysis.CSharp`로 수정 대상 메서드 본문 전체를 추출합니다. 파일 단위 구문 분석만 사용하므로 프로젝트 형식(SDK/레거시)과 관계없이 동작합니다.
  * 같은 클래스의 다른 멤버는 본문(`{ ... }`)을 제거하고 시그니처만 남깁니다. DI 필드 선언부, 파일의 `using` 목록, 입출력 DTO의 프로퍼티 정의를 발췌해 가상 컨텍스트를 조립합니다.
  * 레거시 코드의 C# 언어 버전(예: C# 7.3)을 프로젝트 파일에서 읽어 시스템 프롬프트에 명시합니다. 사용할 수 없는 최신 문법(record, 파일 범위 namespace 등)을 생성하지 않도록 하기 위함입니다.
  * 예산을 넘으면 다음 순서로 제거합니다: 다른 멤버 시그니처 → DTO 정의 → `using` 목록. 수정 대상 본문은 절대 자르지 않으며, 본문만으로 예산을 넘으면 종료 코드 4로 알립니다.
* **MS SQL Server 스키마/SP 슬라이싱 (`SqlSchemaSlicer`):**
  * ScriptDom으로 SP 본문을 파싱해 참조 객체를 추출합니다(기존 객체는 `sys.dm_sql_referenced_entities` 병행).
  * 참조 테이블의 `sys.columns`, `sys.indexes` 메타데이터만 조회해 DDL 요약본으로 주입합니다.
* **토큰 하드 리밋:** 입력 합계 **4,096 토큰** 이하 (§3.3).

### FR-03. Zero-JSON Diff 출력 및 패처 (`SearchReplacePatcher`)
* **강제 출력 포맷** (한 응답에 여러 블록 허용):
  ```text
  <<<<<<< SEARCH
  [기존 코드 원본 (공백 및 들여쓰기 포함)]
  =======
  [수정할 새로운 코드]
  >>>>>>> REPLACE
  ```
* **매칭 단계:** ① 정확 일치 → ② 줄끝 공백·줄바꿈 문자 정규화 → ③ 줄 앞 들여쓰기 정규화. 앞 단계에서 찾지 못할 때만 다음 단계로 넘어갑니다.
* **유일 매칭 원칙:** 어느 단계든 일치 위치가 **2곳 이상이면 실패**로 처리합니다(잘못된 위치 패치 방지).
* **들여쓰기 재적용:** ③단계로 매칭됐으면 원본과 SEARCH 블록의 들여쓰기 차이만큼 REPLACE 블록 전체를 보정합니다.
* **매칭 실패 피드백:** "SEARCH 블록 불일치"와 함께 가장 비슷한 원본 구간(±3줄)을 다음 시도에 전달합니다.
* **마커 충돌:** 대상 파일에 이미 `<<<<<<<`, `=======`, `>>>>>>>`로 시작하는 줄이 있으면(병합 충돌 흔적) 작업 전에 경고하고 중단합니다.

### FR-04. 파일 안전장치 (`FileGuard`)
* **인코딩·줄바꿈 보존:** 읽을 때 BOM·인코딩(UTF-8, UTF-8 BOM, EUC-KR/CP949 — `CodePagesEncodingProvider` 등록)과 줄바꿈(CRLF/LF)을 감지하고, 쓸 때 그대로 유지합니다. 매칭은 정규화된 사본에서 수행합니다. 레거시 코드에는 CP949 파일이 많을 수 있으므로 필수 테스트 대상입니다.
* **백업:** 패치 전 원본을 `.pelly/backup/<run-id>/`에 저장합니다.
* **원자적 쓰기:** 같은 폴더의 임시 파일에 쓴 뒤 교체합니다(Windows는 `File.Replace`, 그 외 `File.Move(overwrite: true)`).
* **Windows 파일 잠금:** Visual Studio, 백신, MSBuild 노드가 파일을 잡고 있으면 공유 위반(sharing violation)이 납니다. 짧은 간격으로 최대 5회 재시도하고, 그래도 실패하면 잠금 중인 파일 경로를 알리고 종료 코드 4로 끝냅니다.
* **동시 수정 감지:** 시작 시 파일 해시·mtime을 기록하고, 적용·복구 직전에 다시 확인합니다. 외부(에디터)에서 바뀌었으면 덮어쓰지 않고 중단합니다.
* **형상 관리 연동:** git 저장소이고 대상 파일에 커밋되지 않은 변경이 있으면 경고합니다(진행 여부는 사용자 확인). 폐쇄망에서 git 외 형상 관리 도구(SVN, TFVC 등)를 쓰면 이 경고는 생략하고 백업에만 의존합니다(Q12).

### FR-05. 추론 예산 제어기 (Thinking Budget Controller)
**2단계 생성** 방식을 사용합니다.
* **사용 시점 (`llm.thinkMode`, 기본 `retry`):** 첫 시도는 생각 없이(빈 think 프리필) 바로 코드를 만들고, 빌드·매칭 실패로 교정할 때부터 THINK 단계를 켭니다. `always`(매 시도), `off`(사용 안 함)로 바꿀 수 있습니다. 근거: M0 S3 — 이 모델은 512토큰 안에 생각을 끝내지 못했고(30/30), 생략해도 10건 결과가 같았으며 평균 9.0초 → 1.7초.
1. **THINK 단계:** think 전용 샘플링 값(§7.2)으로 `num_predict = 512`, stop `</think>`로 요청합니다. 서버가 512토큰에서 멈추면(`done_reason = length`) 잘린 것으로 봅니다. 클라이언트가 스트림을 끊으면 다음 요청이 약 2초 늦어지므로 끊지 않습니다(S2).
2. 잘린 경우 think 내용을 마지막 완결 줄까지만 남깁니다.
3. **CODE 단계:** `[원래 프롬프트] + <think>[think 내용]</think>\n<<<<<<< SEARCH\n`(생각을 생략하면 빈 think)을 프리필(Prefill)로 붙여 code 전용 샘플링 값으로 요청합니다.
* Ollama `/api/generate`를 **`raw: true`** 로 호출합니다. 이 모드에서는 Ollama가 템플릿을 적용하지 않으므로, `QwenChatTemplate`이 chat template을 직접 조립하고 프리필 문자열을 끝에 붙입니다. `/api/chat`과 `think` 파라미터는 프리필을 제어할 수 없어 쓰지 않습니다.
* 재요청은 앞부분 프롬프트가 같아 Ollama가 접두사 KV 캐시를 재사용합니다(S2: 2단계 TTFT 중앙값 299ms). 모델 언로드로 인한 재로딩을 막기 위해 모든 요청에 `keep_alive`(기본 `"30m"`)를 지정합니다.
* think 태그 문자열은 모델 chat template에서 확인한 값을 설정으로 주입합니다(미결 사항 Q1).

### FR-06. 결정론적 검증 및 자가 치유 하네스 (Deterministic Verification Harness)
* **Baseline 스냅샷 (LOCATE 끝):** 대상 프로젝트와 빌드 도구가 정해지면(LOCATE 끝) 패치 전 프로젝트 빌드를 시작해 에러 집합(파일, 에러 코드, 메시지 — 줄 번호 제외)을 기록합니다. 빌드는 모델 생성과 병렬로 돌고 파일을 처음 쓰기 전에 완료를 기다립니다. 검증 단계에서는 **새로 생긴 에러만** 실패로 판단합니다. Baseline에 CS 오류만 있으면 사용자에게 알리고 계속 진행합니다(`--strict-baseline`이면 종료 코드 4). NETSDK·MSB·NU 같은 CS가 아닌 오류가 있으면(예: 패키지 복원 안 됨) 컴파일러가 돌지 않은 것이므로 `--strict-baseline`과 무관하게 종료 코드 4로 중단하고 복원을 안내합니다.
* **빌드 도구 선택 (`BuildToolResolver`):**

  | 프로젝트 형식 | 판별 기준 | 빌드 도구 |
  | :--- | :--- | :--- |
  | SDK 스타일 (`net48` 타겟 포함) | `.csproj` 루트가 `<Project Sdk="...">` | `dotnet build` |
  | 레거시 .NET Framework | `Sdk` 속성 없음 (`ToolsVersion`, `packages.config` 등) | Visual Studio / Build Tools의 `MSBuild.exe` |

  * `MSBuild.exe` 경로는 `vswhere.exe -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`로 찾고, 설정 `build.msbuildPath`로 직접 지정할 수도 있습니다.
  * 찾지 못하면 LOCATE 끝(Baseline 시작 전)에서 종료 코드 4로 끝내고, `pelly doctor`에서 설치 안내를 표시합니다.
* **C# 검증 파이프라인 (`BuildVerifier`):**
  1. **사전 검사:** 패치된 파일을 프로젝트의 언어 버전에 맞춘 Roslyn `SyntaxTree`로 파싱해, 구문 오류가 있으면 빌드 없이 바로 피드백합니다.
  2. **빌드:** 대상 파일이 속한 가장 가까운 `.csproj`만 빌드합니다. 패키지 복원은 하지 않습니다(폐쇄망 개발 PC에 이미 복원돼 있다고 가정).
     * SDK 스타일: `dotnet build <project.csproj> --no-restore --nologo -v q -clp:ErrorsOnly`
     * 레거시: `MSBuild.exe <project.csproj> /t:Build /nologo /v:q /clp:ErrorsOnly /nr:true`
     * 두 도구의 에러 출력 형식(`파일(줄,열): error CSxxxx: 메시지`)은 같으므로 파서는 하나로 처리합니다.
  3. **피드백:** 신규 에러를 `[파일명(줄번호): CSxxxx - 메시지]`로 추출하고, **에러 줄 앞뒤 ±3줄 코드**를 함께 모델에 전달합니다(296 토큰 이내, 에러가 많으면 앞에서부터).
  4. **경고 정책:** 신규 경고는 사용자에게만 보고합니다. `--warn-as-error`이면 에러와 똑같이 처리합니다.
  5. **테스트 (선택):** `--test`이면 빌드 통과 후 테스트를 실행하고(SDK 스타일: `dotnet test`, 레거시: 설정 `test.command`로 지정한 러너), 실패한 테스트 이름과 메시지를 피드백합니다.
* **MS SQL 검증 파이프라인 (`SqlSyntaxVerifier`):**
  1. **안전 가드 (FR-07)** 통과 여부를 먼저 확인합니다.
  2. **오프라인 구문 검사:** ScriptDom 파서로 파싱합니다(DB 접속 없음). 파서 버전은 대상 SQL Server 버전에 맞춰 설정 `sql.compatLevel`로 고릅니다.
  3. **객체 존재 검사:** ScriptDom으로 추출한 참조 테이블·뷰·SP를 `sys.objects`에서 조회합니다. 지연 이름 확인(Deferred Name Resolution) 때문에 `NOEXEC`가 놓치는 존재하지 않는 객체를 여기서 Error 208로 보고합니다. `#temp` 테이블은 제외합니다.
  4. **컴파일 검사:** 개발 DB 세션에서 `SET NOEXEC ON;` 상태로 가실행해 구문 오류, 잘못된 컬럼명(Error 207) 등을 잡습니다. 전체를 트랜잭션으로 감싸고 항상 `ROLLBACK`합니다.
  5. **알려진 한계 (사용자에게 경고로 표시):** 동적 SQL(`sp_executesql`, `EXEC(@sql)`) 내부, `#temp` 테이블의 컬럼은 검증되지 않습니다.
* **회귀 차단 (Rollback):** 자가 교정을 최대 횟수까지 시도해도 실패하면 `FileGuard` 백업으로 원본을 복구합니다.

### FR-07. SQL 안전 가드 (`SqlSafetyGuard`)
* **문장 허용 목록:** 배치의 최상위는 **대상 객체에 대한 단일 `CREATE`/`ALTER PROCEDURE|FUNCTION|VIEW` 문**만 허용합니다. `GO` 구분자로 나뉜 다중 배치는 거부합니다.
* **금지 구문 (위치 무관):** `SET NOEXEC`, `SET PARSEONLY`, `SET FMTONLY`, `SHUTDOWN`, `RECONFIGURE`, `xp_cmdshell`, `OPENROWSET`/`OPENDATASOURCE`, `ALTER DATABASE`/`ALTER LOGIN`/`CREATE LOGIN`, `GRANT`/`REVOKE`/`DENY`
* **접속 대상 제한:** 설정의 `sql.allowedHosts`/`sql.allowedDatabases` 화이트리스트에 있는 대상에만 접속합니다. 없으면 종료 코드 5로 끝냅니다.
* **최소 권한 계정:** 검증 전용 계정에 **DB 수준 `VIEW DEFINITION`만** 부여합니다. `ALTER`·`SHOWPLAN`은 필요 없습니다. 이 권한이 없으면 객체 존재 검사와 스키마 요약이 동작하지 않습니다(S5).
* **Windows 통합 인증 (기본):** 폐쇄망에서는 `Integrated Security=true`를 기본으로 사용합니다. 연결 문자열에 비밀번호가 없으므로 설정 파일에 두어도 됩니다.
* **SQL 인증 (외부 개발용):** 비밀번호가 필요한 연결 문자열은 환경 변수 `PELLY_SQL_CONNECTION` 또는 `dotnet user-secrets`로만 받으며, 설정 파일·로그에 평문으로 남기지 않습니다.

### FR-08. 환경 점검 (`pelly doctor`)
폐쇄망에서는 문제가 생겨도 외부에서 원격 지원을 받기 어려우므로, 사용자가 스스로 환경 문제를 찾을 수 있게 합니다.
* **Ollama 서버:** 연결 가능 여부·지연(`GET /api/version`), 대상 모델 존재(`/api/tags`), 로드 상태(`/api/ps`), 테스트 생성 1회로 TTFT 실측. 서버 환경 변수(Flash Attention, KV 캐시 타입 등)는 API로 확인할 수 없으므로 **서버 관리자 확인 체크리스트**(§7.1)를 함께 표시합니다.
* **빌드 도구:** .NET SDK 버전, `vswhere`·`MSBuild.exe` 경로와 버전, .NET Framework Developer Pack(대상 타겟팅 팩) 설치 여부
* **SQL:** 접속 가능 여부, 화이트리스트 일치, 검증 계정 권한
* **pelly 자체:** 버전, 내장 토크나이저 해시, 설정 파일 경로와 적용 값
* 결과는 화면 출력과 함께 `--sanitize` 형식(FR-09)으로 파일 저장이 가능해야 합니다.

### FR-09. 실행 기록 요약 및 비식별 리포트 (`pelly report`)
폐쇄망의 실행 로그에는 소스 코드가 들어 있어 외부로 반출할 수 없습니다. 외부에서 개선 작업을 하려면 코드 없이 문제를 파악할 수 있는 자료가 필요합니다.
* `pelly report`는 실행 기록에서 KPI(§2.2)와 단계별 소요 시간, 실패 유형 분포를 집계합니다.
* `--sanitize`를 붙이면 **소스 코드, 프롬프트·응답 원문, 파일 경로, 식별자 이름, 연결 정보를 모두 제거**하고 다음만 남깁니다: 단계별 타이밍, 토큰 수, 재시도 횟수, 종료 코드, 에러 코드(`CSxxxx`, SQL 에러 번호), 매칭 단계, pelly·Ollama·모델 버전.
* 반출 가능 여부는 사내 보안 규정을 따릅니다(Q11). 리포트 형식은 사람이 읽고 검토할 수 있는 JSON/Markdown으로 합니다.

---

## 5. 비기능 요구사항 (Non-Functional Requirements)

| 항목 | 요구사항 |
| :--- | :--- |
| **오프라인 동작** | 실행 중 인터넷 접속을 일절 하지 않습니다(설정된 Ollama 서버·SQL 서버만 접속). 업데이트 확인, 텔레메트리, 외부 리소스 다운로드 없음. 토크나이저·기본 설정은 실행 파일에 내장 |
| **원격 Ollama 연동** | 엔드포인트는 설정 `llm.endpoint`(예: `http://ollama.internal:11434`). 사내 프록시 설정(`HTTP_PROXY`, `NO_PROXY`)을 따르되, 내부 주소는 프록시를 우회하도록 안내. 서버 앞단에 인증 프록시가 있으면 `llm.headers`로 헤더 지정(Q13). 연결 타임아웃 3초 |
| **공용 서버 대기** | Ollama 서버를 여러 사람이 함께 쓰면 요청이 대기할 수 있습니다. 응답 첫 토큰이 늦어지면 "서버 대기 중" 상태를 표시하고, 대기 시간은 KPI의 TTFT와 분리해 기록 |
| **설정** | 우선순위: CLI 옵션 > 프로젝트 `pelly.json` > 사용자 `%APPDATA%\pelly\config.json` (macOS/Linux: `~/.config/pelly/config.json`) > 내장 기본값. 주요 키: `llm.endpoint`, `llm.model`, `llm.thinkTag`, `llm.headers`, `budget.*`, `sampling.think/code`, `build.msbuildPath`, `test.command`, `sql.allowedHosts`, `sql.allowedDatabases`, `sql.compatLevel` |
| **실행 로그** | `.pelly/runs/<run-id>/`에 프롬프트, 모델 응답 원문, 패치, 빌드/SQL 로그, 단계별 타이밍을 JSONL로 저장(디버깅·KPI 수집용). 연결 문자열·비밀번호는 마스킹. `.pelly/`는 형상 관리 제외 대상으로 안내. 보관 기간 기본 30일 후 자동 삭제 |
| **장애 처리** | 시작 시 Ollama 연결 확인(타임아웃 3초)과 대상 모델 존재 확인. 모델이 로드되지 않았으면 "첫 요청은 모델 로딩으로 느릴 수 있음"을 표시. 스트림 무응답 10초 시 1회 재연결 후 실패하면 종료 코드 4. 빌드 타임아웃 기본 120초(레거시 대형 프로젝트는 설정으로 조정) |
| **중단 처리** | Ctrl+C(`Console.CancelKeyPress`) 시 진행 중인 요청·빌드를 취소하고 롤백. 빌드 프로세스는 자식 프로세스 트리까지 종료(`Process.Kill(entireProcessTree: true)`) |
| **지원 OS** | **운영: Windows 11 x64 (폐쇄망).** 개발: macOS (Apple Silicon), Windows 11 ARM64 VM (§8.5) |
| **Windows 콘솔** | 시작 시 `Console.OutputEncoding`/`InputEncoding`을 UTF-8로 설정해 한글 깨짐 방지. Windows Terminal·PowerShell 기준으로 렌더링을 확인하고, 구형 콘솔(conhost)에서는 `Spectre.Console`의 색상·유니코드 감지에 맡김 |
| **경로 처리** | 경로는 항상 `Path` API로 조합(구분자 하드코딩 금지). 대소문자를 구분하지 않는 비교(Windows) 고려. 260자 초과 경로 대비 |
| **외부 프로세스** | `dotnet`, `MSBuild.exe`, `git` 실행 시 `ProcessStartInfo.ArgumentList`를 사용해 공백·한글 경로 인용 문제 방지. 출력 인코딩은 UTF-8로 지정(레거시 MSBuild 출력이 CP949로 나오는 경우 감지해 변환) |
| **성능 가시성** | `--verbose` 시 단계별 소요 시간과 토큰 사용량 출력 |

---

## 6. CLI 기술 스택 및 폴더 구조

### 6.1 추천 NuGet 패키지
* **CLI 프레임워크 & UI:** `Spectre.Console` / `Spectre.Console.Cli`
* **C# AST 코드 슬라이서:** `Microsoft.CodeAnalysis.CSharp` (Roslyn)
* **SQL 파싱 및 안전 가드:** `Microsoft.SqlServer.TransactSql.ScriptDom`
* **DB 스키마/구문 검증:** `Microsoft.Data.SqlClient` + `Dapper`
* **토큰 계산:** `Microsoft.ML.Tokenizers` 2.0.1 (Qwen `vocab.json`·`merges.txt` 내장 리소스, S4에서 서버와 오차 0%)
* **설정:** `Microsoft.Extensions.Configuration` (+ `UserSecrets`, 외부 개발용)
* **LLM 스트리밍 연동:** `System.Net.Http` (Ollama NDJSON 스트림 직접 제어)
* **테스트:** `xUnit`, `Verify` (스냅샷 테스트)
* **배포 점검:** CycloneDX SBOM 생성 도구, 서드파티 라이선스 목록 생성 (§11)

모든 패키지는 폐쇄망 반입 심사를 고려해 **라이선스가 명확하고(MIT/Apache 2.0 등) 실행 중 외부 통신이 없는 것**만 사용합니다.

### 6.2 솔루션 폴더 구조
```text
PellyCoder.sln
├── src/PellyCoder.Cli/
│   ├── Program.cs                      # CLI 엔트리포인트 및 DI 컨테이너 설정
│   ├── Commands/
│   │   ├── CsCommand.cs                # pelly cs
│   │   ├── SqlCommand.cs               # pelly sql
│   │   ├── DoctorCommand.cs            # pelly doctor (FR-08)
│   │   └── ReportCommand.cs            # pelly report (FR-09)
│   ├── Config/
│   │   └── PellyOptions.cs             # 설정 바인딩 및 검증
│   ├── Core/
│   │   ├── FsmOrchestrator.cs          # PREFLIGHT → LOCATE → SLICE → EDIT → APPLY → VERIFY → REVIEW
│   │   ├── RunContext.cs               # 실행 단위 상태 (run-id, 재시도 횟수, 백업 정보)
│   │   └── ContextBudget.cs            # §3.3 예산 계산 및 강제
│   ├── Llm/
│   │   ├── ILlmBackend.cs              # 백엔드 추상화
│   │   ├── OllamaBackend.cs            # /api/generate (raw), keep_alive, 연결·모델 확인
│   │   ├── QwenStreamClient.cs         # 2단계 생성 및 Thinking 예산 제어
│   │   ├── QwenChatTemplate.cs         # raw 모드용 chat template 직접 조립
│   │   ├── TokenCounter.cs             # 로컬 토크나이저 + 서버 카운트 보정
│   │   ├── Prompts.cs                  # 시스템 프롬프트·few-shot 템플릿 (버전 관리)
│   │   └── Resources/tokenizer.json    # 내장 Qwen 토크나이저 (오프라인용)
│   ├── Locators/
│   │   └── SymbolLocator.cs            # 대상 심볼 탐색 (FR-01)
│   ├── Slicers/
│   │   ├── RoslynSlicer.cs             # C# 메서드 및 연관 정의 추출
│   │   └── SqlSchemaSlicer.cs          # SP 참조 테이블 DDL 요약 추출
│   ├── Patchers/
│   │   ├── SearchReplaceParser.cs      # SEARCH/REPLACE 블록 파싱
│   │   ├── SearchReplacePatcher.cs     # 단계별 매칭·유일성 검사·들여쓰기 보정
│   │   └── FileGuard.cs                # 인코딩 보존, 백업, 원자적 쓰기, 동시 수정 감지
│   ├── Verifiers/
│   │   ├── BaselineSnapshot.cs         # 패치 전 에러 집합 기록·비교
│   │   ├── BuildToolResolver.cs        # 프로젝트 형식 판별, dotnet / MSBuild.exe(vswhere) 선택
│   │   ├── BuildVerifier.cs            # 구문 사전 검사 + 프로젝트 단위 빌드
│   │   ├── BuildErrorParser.cs         # CS 에러 정규식 파서 + ±3줄 컨텍스트
│   │   ├── SqlSafetyGuard.cs           # FR-07 허용/금지 구문 검사
│   │   └── SqlSyntaxVerifier.cs        # ScriptDom 파싱 + 객체 존재 + NOEXEC 컴파일
│   └── Logging/
│       ├── RunLogger.cs                # .pelly/runs/ JSONL 기록
│       └── ReportSanitizer.cs          # 비식별 리포트 생성 (FR-09)
├── tests/PellyCoder.Tests/             # 단위·스냅샷 테스트
├── eval/
│   ├── PellyCoder.Eval/                # 벤치마크 실행기 및 KPI 집계 (폐쇄망에서도 동작)
│   ├── benchmark/                      # 작업 정의 (요청, 대상, 판정 기준)
│   └── corpus/                         # 외부용 샘플 코퍼스 (SDK·레거시 솔루션, 샘플 DB 스키마)
└── deploy/
    ├── publish.ps1 / publish.sh        # self-contained 패키지 빌드
    ├── package-manifest.md             # 반입 패키지 구성·체크섬 기록 양식
    └── server-checklist.md             # Ollama 서버 관리자 확인 체크리스트
```

---

## 7. LLM 런타임 튜닝 파라미터

### 7.1 Ollama 서버 (폐쇄망, 서버 관리자 관리)
v1은 Ollama만 지원합니다(`/api/generate`, `raw: true`). 서버 설정은 pelly가 바꿀 수 없으므로 아래 값을 **서버 관리자에게 요청·확인**합니다(`deploy/server-checklist.md`).

| 설정 | 권장 값 | 이유 | 확인 방법 |
| :--- | :--- | :--- | :--- |
| Ollama 버전 | 외부 개발 환경과 동일 버전 | raw 모드·캐시 동작이 버전마다 다를 수 있음 | `/api/version` (doctor 자동) |
| 모델 | `Qwen3.6-35B-A3B` GGUF `Q4_K_M`, 외부와 같은 파일 | 결과 재현성 | 관리자에게 GGUF SHA-256 확인 (Q10) |
| `OLLAMA_FLASH_ATTENTION` | `1` | KV 캐시 양자화를 쓰려면 필요 | 관리자 확인 |
| `OLLAMA_KV_CACHE_TYPE` | `q8_0` (4비트 금지) | 변수명 철자·괄호 짝 기억력 유지하면서 VRAM 절약 | 관리자 확인 |
| `OLLAMA_NUM_PARALLEL` | 동시 사용자 수에 맞춰 협의 | 슬롯 수만큼 KV 메모리(8K × 슬롯)가 늘어남. 1이면 요청이 대기 | 관리자 확인 (Q9) |
| `OLLAMA_HOST` | 폐쇄망 내부 주소로 바인딩 | 개발 PC에서 원격 접속 가능하도록 | doctor 연결 확인 |
| `OLLAMA_KEEP_ALIVE` | 업무 시간 동안 언로드되지 않는 값 | 재로딩 시 TTFT 목표가 깨짐. pelly도 요청마다 `keep_alive: "30m"` 지정 | `/api/ps` (doctor 자동) |

pelly가 요청마다 지정하는 값: `options.num_ctx = 8192`, `keep_alive = "30m"`, 단계별 샘플링 값(§7.2).

### 7.2 파라미터
| 설정 항목 | 필수 적용 값 | 기술적 근거 (Why) |
| :--- | :--- | :--- |
| **Model Weights** | GGUF `Q4_K_M` | 메모리 대역폭 최적화로 고속 토큰 생성 |
| **KV Cache Quantization** | **`FP16` 또는 `Q8_0` (4비트 금지)** | KV 캐시를 4비트로 낮추면 변수명 철자와 괄호 짝 기억력이 손상됨 |
| **Context Window (`num_ctx`)** | **`8192` (8K 고정)** | 메모리 사용량 예측 가능성 확보, §3.3 예산의 기준 |
| **THINK 단계 샘플링** | 모델 카드 권장값 (Qwen3 계열 기준 예: `Temp 0.6` / `Top-P 0.95` / `Top-K 20`) | Qwen3 계열은 thinking 모드에서 greedy에 가까운 디코딩 시 반복 루프 위험을 경고함. Qwen3.6 값은 M0에서 확인 (Q2) |
| **CODE 단계 샘플링** | `Temp 0.1` / `Top-P 0.9` | 창의성을 배제하고 가장 확률 높은 구문 선택 유도 |
| **Repeat Penalty (CODE)** | `1.05` | 과도한 패널티로 필수 예약어(`public`, `SELECT`, `await` 등)를 피하는 현상 방지 |

샘플링 값은 설정 파일로 바꿀 수 있어야 합니다. 폐쇄망 안에서 코드 재반입 없이 튜닝하기 위함입니다.

---

## 8. 평가 및 테스트 전략

### 8.1 벤치마크 (`eval/benchmark`)
* **외부 샘플 코퍼스 (`eval/corpus`):** 실제 코드는 반출할 수 없으므로, 실제 코드의 특징을 모사한 샘플 솔루션을 만듭니다.
  * 레거시 .NET Framework 프로젝트(SDK 스타일 아님, `packages.config`, C# 7.3)와 SDK 스타일 프로젝트를 모두 포함
  * CP949 인코딩·CRLF 파일, Dapper 사용 코드, 실제와 비슷한 명명 규칙
  * 샘플 DB 스키마와 SP (SQL Server 컨테이너에 배포)
* **구성:** C# 30건(메서드 로직 수정, null 처리 추가, Dapper 쿼리 변경, async 전환 등) + SQL 20건(SP 조건 추가, 컬럼 추가 조회, JOIN 변경 등)
* **작업 정의:** 요청문, 대상 파일·심볼, 판정 기준(빌드/검증 통과 + 지정 테스트 통과 또는 기대 diff와의 의미 비교)
* **수집 지표:** §2.2의 모든 KPI를 실행 로그(`.pelly/runs`)에서 자동 집계
* **폐쇄망 벤치마크:** M4에서 같은 형식으로 실제 코드 기반 세트를 폐쇄망 안에서 작성합니다. 평가 실행기는 폐쇄망에서도 그대로 동작해야 합니다.

### 8.2 회귀 평가
* `Prompts.cs`, 샘플링 값, 슬라이서 로직을 바꿀 때마다 외부 벤치마크 전체를 실행합니다.
* 최종 작업 성공률이 기준선보다 떨어지면 병합하지 않습니다.

### 8.3 단위·스냅샷 테스트 (`tests/`)
* **Patcher:** 들여쓰기 오차, CRLF/LF 혼합, BOM, EUC-KR/CP949, 다중 매칭 거부, 마커 충돌
* **Slicer:** 대표 파일에 대한 슬라이스 결과 스냅샷, 예산 초과 시 제거 순서, 언어 버전 감지
* **SqlSafetyGuard:** 금지 구문·다중 배치·`SET NOEXEC OFF` 삽입 시도 거부 케이스
* **BuildToolResolver:** SDK/레거시 프로젝트 판별, `vswhere` 결과 파싱, 경로 직접 지정
* **BuildErrorParser / BaselineSnapshot:** `dotnet build`·`MSBuild.exe` 실제 출력 샘플 기반 파싱·신규 에러 판별
* **QwenStreamClient:** 모의 Ollama 스트림 서버(NDJSON)로 512 토큰 중단·프리필 재요청 동작 검증
* **ReportSanitizer:** 비식별 리포트에 코드·경로·식별자가 남지 않는지 검증 (반출 사고 방지용 필수 테스트)
* **오프라인 동작:** 네트워크를 Ollama·SQL 주소 외에는 차단한 상태에서 전체 E2E가 동작하는지 확인

### 8.4 CI 매트릭스 (외부)
* GitHub Actions에서 **`windows-latest`와 `macos-latest`** 양쪽으로 단위·스냅샷 테스트를 실행합니다. LLM이 필요 없는 테스트만 돌립니다.
* `windows-latest` 러너에는 Visual Studio가 설치돼 있으므로 **레거시 프로젝트 빌드 검증 테스트**(샘플 코퍼스)를 여기서 실행합니다.
* Windows 전용 케이스(CRLF, CP949, 파일 잠금 재시도, 경로 대소문자, 콘솔 인코딩, MSBuild.exe)는 Windows 러너에서만 실행되도록 표시합니다.

### 8.5 외부 개발 환경 구성
폐쇄망 구성(개발 PC와 원격 Ollama 서버 분리)을 노트북 한 대에서 최대한 비슷하게 재현합니다.

```text
[macOS 노트북 (Apple Silicon)]
 ├─ Ollama (Metal GPU) ─ 폐쇄망과 같은 버전·같은 GGUF, OLLAMA_HOST=0.0.0.0
 │      ▲  HTTP (원격 서버 역할)
 │      │
 ├─ Windows 11 ARM64 VM (Parallels / UTM)
 │      ├─ .NET 10 SDK, Visual Studio Build Tools (MSBuild.exe), .NET Framework Developer Pack
 │      ├─ pelly 실행 → 호스트 Ollama 접속
 │      └─ 샘플 코퍼스 빌드 검증 (레거시 포함)
 │
 └─ SQL Server (Docker 컨테이너) ─ 샘플 DB, SQL 인증
```
* **일상 개발:** macOS에서 코드 작성과 단위 테스트를 합니다. 레거시 빌드 검증이 필요한 작업은 VM에서 실행합니다.
* **원격 서버 재현:** VM의 pelly가 호스트의 Ollama에 네트워크로 접속하므로, 폐쇄망의 "개발 PC ↔ 원격 서버" 구조와 같은 경로를 테스트할 수 있습니다.
* **한계:**
  * 성능 수치는 폐쇄망 GPU 서버와 다르므로 참고만 합니다.
  * VM은 ARM64 Windows이므로 운영 환경(x64)과 다릅니다. x64 패키지 동작은 CI의 `windows-latest`(x64)와 M4 폐쇄망 검증으로 확인합니다.
  * Windows 통합 인증은 외부에서 테스트하지 않고 M4에서 확인합니다.

---

## 9. 구현 로드맵 (Implementation Phases)

* **Milestone 0 (1주 차) — 환경 구성 및 기준선:**
  * 외부 개발 환경 구성(§8.5): Ollama, Windows ARM VM, Build Tools, SQL Server 컨테이너
  * 폐쇄망 정보 수집: Ollama 버전·모델 파일 해시·서버 설정·사양·동시 사용자 수, 개발 PC 도구 버전, 반입 절차 (Q9~Q13)
  * Ollama raw 모드 Prefill·접두사 캐시 재사용·think 태그 동작 확인 (Q1~Q3)
  * SQL 검증 계정의 최소 권한 확인 (Q4)
  * 샘플 코퍼스·외부 벤치마크 작성, 평가 하네스 골격 구축
* **Milestone 1 (2주 차) — C# 최소 E2E:**
  * `Spectre.Console.Cli` 스캐폴딩, 설정·로그, `pelly doctor` 기본 항목
  * Windows/macOS CI 매트릭스 구성 (§8.4)
  * `QwenStreamClient`(2단계 생성, 512 토큰 예산, 내장 토크나이저), `SearchReplacePatcher` + `FileGuard`
  * 기본 `RoslynSlicer`(대상 메서드 + 시그니처) → **C# 경로 E2E 최초 동작 (SDK 스타일)**
* **Milestone 2 (3주 차) — 검증 루프 및 SQL:**
  * `BuildToolResolver`(레거시 MSBuild.exe 포함), `BaselineSnapshot`, `BuildVerifier`, 자가 교정 루프·롤백·REVIEW 단계
  * `SymbolLocator`
  * `SqlSafetyGuard`, `SqlSyntaxVerifier`, 기본 `SqlSchemaSlicer` → **레거시 C#·SQL 경로 E2E 동작**
* **Milestone 3 (4주 차) — 품질 및 반입 패키지:**
  * 슬라이서 고도화, 외부 벤치마크 기반 프롬프트·파라미터 튜닝
  * `pelly report --sanitize`, `pelly doctor` 완성
  * self-contained 패키지·SBOM·라이선스 목록·체크섬 생성, 반입 신청 (§11)
* **Milestone 4 (5주 차 이후, 반입 승인 시점에 따라 조정) — 폐쇄망 이관 및 현장 검증:**
  * 설치, `pelly doctor`로 서버·빌드 도구·SQL·Windows 인증 점검
  * 실제 코드 기반 폐쇄망 벤치마크 작성 및 실행 → **KPI 최종 판정**
  * 설정 파일로 가능한 튜닝은 폐쇄망 안에서 진행. 코드 수정이 필요하면 비식별 리포트를 반출해 외부에서 수정 → 재반입

---

## 10. 미결 사항 (Open Questions)

| ID | 질문 | 확인 시점 | 담당 |
| :--- | :--- | :--- | :--- |
| Q1 | ~~think 태그·chat template~~ → **해소: ChatML + `<think>` (S1)**. 폐쇄망과 같은 모델로 재확인 | — | |
| Q2 | ~~권장 샘플링 값~~ → **해소: 모델 카드 "정밀 코딩" 값 0.6/0.95/20 (S3)** | — | |
| Q3 | ~~접두사 캐시 재사용~~ → **해소: 재사용됨, 2단계 TTFT 299ms (S2)** | — | |
| Q4 | ~~NOEXEC 최소 권한~~ → **해소: DB 수준 `VIEW DEFINITION` (S5)** | — | |
| Q5 | 외부 샘플 코퍼스가 모사할 실제 코드의 특징(프로젝트 구성, C# 버전, 인코딩, 데이터 접근 패턴) | M0 | |
| Q6 | 최종 작업 성공률 목표치 확정 (잠정 85%) | M4 | |
| Q7 | ~~기준 하드웨어 확정~~ → 폐쇄망 Ollama 서버로 확정 (사양은 Q9) | — | |
| Q8 | ~~레거시 프로젝트 지원 여부~~ → **해소: 레거시 .NET Framework 프로젝트 있음, v1에 포함 (FR-06)** | — | |
| Q9 | 폐쇄망 Ollama 서버의 GPU 사양·VRAM, 동시 사용자 수, `OLLAMA_NUM_PARALLEL` 등 현재 설정 | M0 | |
| Q10 | 폐쇄망 서버의 Ollama 버전과 모델 태그·GGUF 파일 SHA-256 (외부에서 같은 파일 확보 가능 여부) | M0 | |
| Q11 | 반입 절차: 바이너리만 vs 소스 포함, 보안 심사 제출 자료(SBOM, 라이선스, 백신 검사 결과 등), 소요 기간, 비식별 리포트 반출 허용 여부 | M0 | |
| Q12 | 폐쇄망 개발 PC 환경: .NET SDK·Visual Studio/Build Tools 버전, .NET Framework 타겟 버전, 내부 NuGet 미러 유무, 형상 관리 도구(git/SVN/TFVC), PowerShell 실행 정책 | M0 | |
| Q13 | 개발 PC → Ollama 서버 접근 방식: 주소·포트, 사내 프록시, 앞단 인증 여부 | M0 | |

---

## 11. 폐쇄망 이관 (배포 패키지 및 절차)

### 11.1 반입 패키지 구성
| 항목 | 내용 |
| :--- | :--- |
| 실행 파일 | `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` 결과 `pelly.exe` (트리밍 사용 안 함). 단일 파일 모드에서 Roslyn·ScriptDom·토크나이저 리소스가 정상 동작하는지 M3에서 확인 |
| 기본 설정 | `pelly.json` 템플릿 (Ollama 주소·모델명·SQL 화이트리스트 자리 표시) |
| 문서 | 설치 가이드, 사용 가이드, `server-checklist.md`(서버 관리자용) |
| 무결성 | 모든 파일의 SHA-256 체크섬 목록 |
| 보안 심사 자료 | SBOM(CycloneDX), 서드파티 라이선스 목록, "실행 중 외부 통신 없음" 명세(§5) |
| 버전 기록 | pelly 버전, 외부 검증에 쓴 Ollama 버전과 모델 GGUF SHA-256, 벤치마크 결과 요약 |

### 11.2 설치
* zip을 원하는 폴더에 풀고 `PATH`에 추가합니다. 관리자 권한이나 .NET 런타임 설치가 필요 없습니다.
* `pelly doctor`로 Ollama 서버, 빌드 도구, SQL 접속을 확인합니다.
* 대상 PC에 .NET 10 SDK가 있으면 대안으로 로컬 폴더 피드에서 `dotnet tool install -g PellyCoder.Cli --add-source <폴더>`로 설치할 수도 있습니다.

### 11.3 업데이트 주기
* 반입 절차에 시간이 걸리므로, 자주 바뀌는 값(샘플링, 예산, 프롬프트 일부)은 **설정 파일로 빼서** 재반입 없이 바꿀 수 있게 합니다.
* 코드 수정이 필요한 개선은 모아서 정기 반입합니다. 반입본마다 버전 기록(§11.1)을 남겨 폐쇄망 결과와 외부 결과를 대조할 수 있게 합니다.

---

## 12. v2 로드맵

### 12.1 원칙
v2는 Superpowers, OpenSpec 같은 개발 하네스의 아이디어를 가져오되, **방식은 Pelly에 맞게 바꿉니다.**
* **프로세스는 프롬프트가 아니라 코드로 강제합니다.** 긴 스킬·지침 문서를 모델에 넣지 않습니다. 단계 진행, 검증, 상태 기록은 모두 FSM과 C# 코드가 맡습니다.
* **v1 FSM을 그대로 재사용합니다.** v2 기능은 v1의 단일 작업 실행(PREFLIGHT → … → REVIEW)을 여러 번 호출하거나, VERIFY 단계에 검사를 추가하는 방식으로만 확장합니다.
* **토큰 예산(§3.3)은 바꾸지 않습니다.** 새로 넣는 정보도 기존 예산 안에서 나눠 씁니다.
* **작업을 쪼개고 계획하는 일은 사람이 합니다.** 활성 3B 모델이 약한 부분이기 때문입니다.

### 12.2 진입 조건 및 순서
* **진입 조건:** M4 폐쇄망 검증에서 ① 최종 작업 성공률 목표(Q6) 달성, ② 검증 불변 조건 위반 0건.
* **순서:**

  | 단계 | 기능 | 예상 기간 | 의존성 |
  | :--- | :--- | :--- | :--- |
  | V2-0 | GUI 승인 창 (`pelly ui`, Avalonia): External Tools에서 띄우는 작은 창으로 요청 입력·진행 표시·diff 승인 | 1~2주 | v1 (진입 조건과 무관하게 v1 직후 착수 가능) |
  | V2-1 | 작업 명세 파일과 순차 실행 | 2주 | v1 |
  | V2-2 | 테스트 우선 검증 | 2주 | V2-1 (수용 기준 ↔ 테스트 연결) |
  | V2-3 | 프로젝트 규칙 파일 | 1주 | v1 (다른 단계와 독립적이라 앞당길 수 있음) |

* 각 단계가 끝날 때마다 폐쇄망으로 반입해 검증합니다(§11.3).

### 12.3 V2-1. 작업 명세 파일과 순차 실행 (`pelly apply`)
OpenSpec의 "변경 제안 → 작업 목록 → 적용 → 보관" 흐름에서 착안했습니다. v1은 한 번에 메서드 하나만 고칠 수 있습니다. 이 기능은 그 단위를 유지한 채, 여러 파일에 걸친 변경을 사람이 정한 순서대로 처리합니다.

* **명세 파일 형식** (`changes/<id>.md`, 사람이 작성):
  ```markdown
  ---
  id: order-cancel
  status: draft            # draft | in-progress | done
  ---
  # 주문 취소 시 배송 상태 확인

  ## 목표
  이미 배송된 주문은 취소할 수 없게 한다.

  ## 수용 기준
  - AC1: 배송 완료 주문을 취소하면 InvalidOperationException 발생
    test: OrderServiceTests.Cancel_WhenShipped_Throws
  - AC2: 취소 SP는 배송 완료 주문의 상태를 바꾸지 않는다

  ## 작업
  - [ ] T1 cs  src/Orders/OrderService.cs#Cancel     배송 완료 상태면 InvalidOperationException 던지기 (AC1)
  - [ ] T2 sql db/sp/usp_CancelOrder.sql             WHERE 조건에 배송 완료 제외 추가 (AC2)
  ```
* **작업 줄 문법:** `- [ ] <ID> <cs|sql> <파일>[#심볼] <요청> [(AC 참조)]`. 정해진 문법으로만 파싱하며, 모델이 명세를 해석하지 않습니다.
* **보조 명령:**
  * `pelly spec new <id>`: 빈 템플릿 생성
  * `pelly spec check <파일>`: 문법, 대상 파일·심볼 존재 여부, AC 참조 유효성, 작업 수 상한(20개) 검사
* **실행 (`pelly apply changes/<id>.md`):**
  1. 작업을 위에서부터 하나씩 v1 FSM으로 실행합니다. 앞 작업의 결과는 파일에 반영돼 있으므로, 다음 작업의 SLICE가 바뀐 코드를 기준으로 컨텍스트를 만듭니다.
  2. 성공하면 명세 파일의 체크박스를 `[x]`로 바꾸고 run-id를 붙입니다(`- [x] T1 … <!-- run: 20261017-0931-ab12 -->`).
  3. 실패하면 기본적으로 멈춥니다(`--continue`이면 다음 작업으로 진행). `pelly apply --resume`으로 실패한 작업부터 다시 시작합니다.
  4. 모든 작업이 끝나면 `status: done`으로 바꾸고 `changes/archive/<날짜>-<id>.md`로 옮깁니다.
* **작업 그룹 (시그니처 변경용):**
  * v1에서는 메서드 시그니처를 바꾸면 호출하는 쪽에서 빌드 에러가 나서 검증을 통과할 수 없었습니다.
  * 작업에 `[G1]`처럼 그룹을 붙이면, 그룹 안의 작업은 구문 검사만 하고 **그룹의 마지막 작업 뒤에 한 번에 빌드 검증**합니다. 빌드는 영향을 받는 모든 프로젝트를 대상으로 합니다.
  * 그룹 검증이 실패하면 그룹 전체를 롤백합니다. 자가 교정 피드백은 에러가 난 파일을 대상으로 한 작업에 전달합니다.
  * 이로써 v1 Non-goal이던 "DTO/인터페이스 시그니처 변경"을 지원합니다.
* **REVIEW:** 기본은 작업마다 diff를 승인받고, `--review-at-end`이면 전체 변경을 마지막에 한 번 승인받습니다(거부하면 전체 롤백).
* **기대 효과:** 변경 이력이 리포지토리에 문서로 남아, 폐쇄망 환경의 감사 추적과 코드 리뷰 자료로 쓸 수 있습니다.
* **추가 구성 요소:** `Specs/ChangeSpecParser.cs`, `Specs/SpecRunner.cs`, `Specs/SpecArchiver.cs`, `Commands/ApplyCommand.cs`, `Commands/SpecCommand.cs`

### 12.4 V2-2. 테스트 우선 검증 (Test-First Verification)
Superpowers의 TDD(Red → Green) 원칙에서 착안했습니다. v1 검증은 "컴파일이 되는가"까지만 확인합니다. 이 기능은 "요청한 동작을 하는가"까지 결정론적으로 확인합니다.

* **연결:** 명세의 수용 기준에 `test:`로 테스트 이름을 지정합니다. 단독 실행에서는 `pelly cs … --verify-test <테스트 이름>`으로 지정합니다.
* **흐름:**
  1. **RED 확인 (패치 전):** 지정한 테스트를 실행해 **실패하는지** 확인합니다. 이미 통과하면 "이 테스트는 변경을 검증하지 못함"으로 경고하고 사용자 확인을 받습니다.
  2. **패치 및 빌드 검증:** v1과 같습니다.
  3. **GREEN 확인 (패치 후):** 지정한 테스트가 **통과**해야 합니다. 실패하면 자가 교정 루프로 돌아갑니다.
  4. **회귀 확인:** 같은 테스트 프로젝트의 다른 테스트는 패치 전 결과(Baseline)와 비교해 **새로 실패한 테스트가 없어야** 합니다.
* **테스트 실행 도구:**
  * SDK 스타일: `dotnet test <project> --no-build --filter "FullyQualifiedName~<이름>"`
  * 레거시: `vswhere`로 찾은 `vstest.console.exe <dll> /Tests:<이름>`
* **피드백:** 실패한 테스트 이름, assert 메시지, 대상 파일 안의 스택 프레임만 전달합니다(재시도 피드백 예산 296 토큰 이내).
* **테스트 작성:** v2에서는 사람이 작성합니다. 명세에 `test` 유형 작업을 두어 모델이 테스트를 먼저 쓰게 하는 방식은 V2-2 결과를 보고 v2.x에서 검토합니다. 이때 테스트 작업은 "빌드는 되고 테스트는 실패(RED)"를 통과 기준으로 삼습니다.
* **추가 구성 요소:** `Verifiers/TestRunner.cs`(dotnet test / vstest 선택), `Verifiers/TestBaseline.cs`

### 12.5 V2-3. 프로젝트 규칙 파일 (`pelly.rules.md`)
Claude Code의 `CLAUDE.md`, OpenSpec의 프로젝트 문서에서 착안했습니다. 다만 긴 문서를 통째로 넣지 않고, **지금 작업과 관련된 짧은 규칙만** 골라 넣습니다.

* **형식:** 한 줄에 규칙 하나(100자 이내). 앞에 태그를 붙입니다.
  ```markdown
  - [*] 주석과 로그 메시지는 한국어로 작성한다
  - [dapper,sql] SQL은 Dapper 파라미터로만 전달하고 문자열 연결을 쓰지 않는다
  - [async] async 메서드 이름은 Async로 끝낸다
  - [async] .Result / .Wait() 를 쓰지 않는다 | forbid: \.Result\b|\.Wait\(\)
  ```
* **선택 규칙 (결정론적):**
  * `[*]` 규칙은 항상 넣습니다.
  * 나머지는 태그 키워드가 SLICE 결과 텍스트에 나타날 때만 넣습니다(대소문자 무시).
  * 규칙 예산은 **150 토큰**이며, 시스템 프롬프트 예산(600)에서 나눠 씁니다(시스템 450 + 규칙 150). 넘치면 파일에서 아래쪽 규칙부터 뺍니다. `--verbose`에서 들어간 규칙과 빠진 규칙을 보여줍니다.
* **금지 패턴 검사:** `| forbid: <정규식>`이 붙은 규칙은 VERIFY 단계에서 **REPLACE 블록에 새로 추가된 줄**을 검사합니다. 일치하면 빌드 에러와 똑같이 자가 교정 피드백으로 보냅니다. 프롬프트로 부탁만 하는 것이 아니라 코드로 강제하는 장치입니다.
* **보조 명령:** `pelly rules check`: 문법, 길이, 중복, 정규식 유효성, 전체 토큰 수 검사
* **추가 구성 요소:** `Rules/RulesLoader.cs`, `Rules/RuleSelector.cs`, `Verifiers/ForbiddenPatternVerifier.cs`

### 12.6 v2 KPI (추가분)
| 지표 | 목표 (잠정) | 측정 기준 |
| :--- | :--- | :--- |
| **명세 완료율** | ≥ 70% | 명세 하나의 모든 작업이 사람 개입 없이 성공한 비율 (작업 3~8개 규모 벤치마크 명세 기준) |
| **수용 기준 충족률** | ≥ 80% | 테스트가 연결된 작업 중 GREEN·회귀 없음까지 통과한 비율 |
| **RED 확인 경고율** | 기록만 함 | 이미 통과하는 테스트가 지정된 비율. 높으면 명세 작성 가이드 보완 |
| **규칙 위반 잔존율** | 0% | 최종 승인된 패치에 `forbid` 패턴이 남은 비율 (불변 조건) |
| **규칙 도입 효과** | 성공률 +5%p 이상 | 규칙 파일 유무에 따른 v1 최종 작업 성공률 차이 |

벤치마크(§8.1)에 다중 작업 명세 10건과 테스트가 연결된 작업 20건을 추가합니다. 외부 샘플 코퍼스와 폐쇄망 세트 모두에 추가합니다.

### 12.7 v2에서도 하지 않을 것
* **모델이 스스로 작업을 분해하거나 계획하는 기능.** 명세 작성과 작업 분해는 사람이 합니다.
* **스킬·지침 문서를 프롬프트에 통째로 넣는 방식.** 필요한 정보는 규칙 파일처럼 짧게 골라 넣습니다.
* **종료 조건 없이 모델이 다음 행동을 고르는 자율 에이전트 루프.** 모든 반복은 FSM이 정한 횟수 안에서만 일어납니다.
