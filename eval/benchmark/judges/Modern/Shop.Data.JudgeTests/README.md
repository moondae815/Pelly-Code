# Shop.Data 판정 테스트

데이터 접근 코드(Dapper)의 동작 판정 프로젝트입니다. 규칙과 현재 테스트 표는 `../Shop.Api.JudgeTests/README.md`에 함께 있습니다.

* 코퍼스 소스(`Shop.Api/Models`, `Shop.Api/Data`, `Shop.Jobs/DailySettlementJob.cs`)를 프로젝트 참조 없이 직접 컴파일합니다.
* Dapper·Microsoft.Data.SqlClient 패키지를 참조하지 않습니다. `FakeSql.cs`가 같은 네임스페이스·이름의 가짜를 줍니다.
* 테스트는 `FakeDatabase.Create()`의 연결 문자열로 코퍼스 코드를 만들고, 기록된 SQL·매개변수(`Commands`)와 결과를 확인합니다.
