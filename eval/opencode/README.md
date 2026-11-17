# OpenCode로 벤치마크 돌리기

pelly의 오케스트레이터 대신 다른 코딩 에이전트(OpenCode)가 같은 작업을 하고, 같은 판정(빌드·테스트·독립 검증)으로 채점한다. `pelly_impact`·`pelly_verify` MCP 서버의 효과를 재는 용도다.

## 연결

* 환경 변수 `PELLY_EVAL_AGENT`가 스크립트 경로면 `PellyCode.Eval`이 FSM 대신 `ExternalAgent`로 그 스크립트를 부른다(`eval/PellyCode.Eval/ExternalAgent.cs`).
* 인자 순서: 작업 폴더, 대상 파일(작업 폴더 기준), 종류(`cs`|`sql`), 메서드, 줄, 객체, 요청문. 출력은 작업 폴더의 `agent.log`에 남고, 15분을 넘기면 종료 코드 124다.
* `opencode-agent.sh`: 한국어 프롬프트를 만들어 `opencode run --pure --auto -m "$OC_MODEL"`를 `OPENCODE_CONFIG="$OC_CONFIG"`로 돌린다. 표준 입력은 `/dev/null`이다(터미널이 아니면 `opencode run`이 멈춘다). 작업 사본은 `.git` 없이 복사되므로(`pelly_verify`는 HEAD 기준이 필요) 폴더가 저장소가 아니면 시작 전에 `git init`과 첫 커밋을 한다.

## 변형

| 변형 | 설정 | 내용 |
| :--- | :--- | :--- |
| `base` | `opencode.base.json` | MCP 없음 |
| `mcp` | `opencode.mcp.json` | `pelly` MCP 서버 등록(작업 폴더가 루트). AGENTS.md 줄 없음 |
| `mcp-agents` | `opencode.mcp.json` | `mcp`와 같고, 스위트 `overlay`로 `overlay/AGENTS.md`(설계 §8 권장 줄)를 각 루트에 넣는다 |

셋 다 Ollama `qwen3.6:35b-a3b-q4_K_M`, `--pure --auto`, 전역 MCP(context7, playwright)는 끈다. 설정 파일에는 로컬 경로가 없다. `__PELLY_MCP_DLL__`은 `run.sh`가 채운다. 도구 이름은 OpenCode가 서버 이름을 붙여 `pelly_pelly_verify`, `pelly_pelly_impact`로 보인다.

## 실행

```bash
# pelly.json: eval이 Ollama에 모델이 있는지 확인하므로 llm.model이 필요하다 (예: {"llm":{"model":"qwen3.6:35b-a3b-q4_K_M"}})
eval/opencode/run.sh <base|mcp|mcp-agents> <결과 폴더> --config <pelly.json> [--kind cs|sql] [--tasks cs-modern-001] [--keep-work all]
```

* `run.sh`는 결과 폴더에 `opencode.json`(채운 설정), `suite/`(내장 스위트의 작업·판정 복사본과 변형별 `suite.json`), `results/`를 만든다. 내장 스위트(`eval/benchmark`)는 건드리지 않는다.
* MCP 서버는 `PELLY_MCP_DLL`(기본: `src/PellyCode.McpServer`의 Release 빌드, 없으면 빌드)로 `dotnet`이 띄운다.
* `OC_MODEL`(기본 `ollama/qwen3.6:35b-a3b-q4_K_M`)로 모델을 바꾼다.
* SQL 작업은 `PELLY_SQL_CONNECTION`이 필요하다. 비밀번호가 든 값이라 사람이 직접 `!`로 돌린다.

## 결과 읽기

* `results/report.md`: 성공 수, 시간 중간값. `--keep-work all`이면 `results/work/<id>/agent.log`가 남는다.
* 도구 호출 작업 수: `grep -l pelly_verify results/work/*/agent.log | wc -l` (`pelly_impact`도 같은 방식).
* 모델 digest가 현장 파일과 다를 수 있고 1회 측정이라 편차가 크다.
