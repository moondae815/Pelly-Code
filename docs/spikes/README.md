# M0 스파이크·완료 점검

계획: `docs/superpowers/plans/2026-10-17-pelly-m0-foundation.md` / 설계 반영: 설계 문서 §9.1

## 스파이크
| ID | 문서 | 결과 |
| :--- | :--- | :--- |
| S1 | [chat template·think 태그](S1-template.md) | 통과 |
| S2 | [raw 프리필·접두사 캐시](S2-prefill-cache.md) | 통과 (클라이언트 취소 금지 → 설계 D22) |
| S3 | [샘플링 기본값](S3-sampling.md) | 기본값 유지, think는 교정 시도부터 (설계 D21, 사용자 결정) |
| S4 | [.NET 토크나이저](S4-tokenizer.md) | 통과 (오차 0%) |
| S5 | [NOEXEC 권한·배치 순서](S5-sql-noexec.md) | 확정 (최소 권한 VIEW DEFINITION) |

모든 측정은 `qwen3.6:35b-a3b-coding-nvfp4`로 했다. 폐쇄망 서버와 같은 모델을 내려받으면 S1·S2·S4를 다시 실행한다.

## M0 완료 기준 (설계 §11)
| 기준 | 상태 |
| :--- | :--- |
| 스파이크 문서 5개 | ✅ 위 표 |
| CI 녹색 | ✅ GitHub Actions `ci` (unit mac/win, windows-integration, sql-integration) |
| 코퍼스가 CI에서 빌드됨 | ✅ Modern(`unit`), Legacy(`windows-integration` — `build.ps1`: MSBuild + vstest), Sql(`sql-integration` — `deploy.sh`) |
| 벤치마크 YAML 10건 | ✅ `eval/benchmark/tasks/` (C# 5: Legacy 3·Modern 2, SQL 5) |
