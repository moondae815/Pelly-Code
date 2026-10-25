# apply 벤치마크 사례 (`pelly-eval apply`)

이 폴더의 `<id>.md` 12건은 `pelly apply`를 끝까지 돌리는 E2E 벤치마크 명세입니다. 실행 방법·산출물은 상위 `eval/benchmark/README.md`의 「apply 벤치마크」 절을 보십시오.

## 출처
* S6 탐침의 정답 명세 `spikes/S6-planning/cases/<id>/gold.md` 12건입니다. 사람이 승인했습니다(`spikes/S6-planning/cases/APPROVED.json`: 승인자·날짜·`goldCommit`).
* `spikes/`는 고치지 않습니다. 정답을 바꿔야 하면 S6 정답을 다시 승인받은 뒤 이 폴더를 같은 규칙으로 다시 만듭니다.

## 변환 규칙
* 머리말은 `id`, `status: draft`, `scope`(정답 그대로) 셋만 둡니다. `category`·`request`·`status: approved`는 뺍니다. `scope`는 파서가 모르는 키라 무시하고, 벤치마크 실행기만 읽습니다(Legacy → Windows 전용, Sql → SQL 연결 필요).
* 본문은 `# <request>`(YAML 큰따옴표 문자열을 풀어 쓴 요청문), 빈 줄, `## 작업`, 정답의 작업 줄 **글자 그대로**입니다.
* `ApplyCasesTests.Cases_match_the_approved_S6_gold`가 작업 줄이 정답과 같은지, `APPROVED.json`이 있는지, `ChangeSpecParser.Parse` 이슈가 0인지 검사합니다.
