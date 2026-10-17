// S5: 권한·배치 순서별 NOEXEC 컴파일 결과. 실행: dotnet run noexec.cs
#:package Microsoft.Data.SqlClient@7.1.1
using Microsoft.Data.SqlClient;

var server = Environment.GetEnvironmentVariable("PELLY_SPIKE_SQL_SERVER") ?? "localhost,14333";
var saPassword = Environment.GetEnvironmentVariable("PELLY_SPIKE_SA_PASSWORD") ?? "Pelly_Local_2026!";
string Conn(string user, string pwd) => $"Server={server};Database=PellyCorpus;User Id={user};Password={pwd};TrustServerCertificate=True";

var users = new (string Name, string Pwd)[]
{
    ("pelly_none", "Pelly_None_2026!"),
    ("pelly_view", "Pelly_View_2026!"),
    ("pelly_showplan", "Pelly_Plan_2026!"),
    ("pelly_alter", "Pelly_Alter_2026!"),
};

const string body = """
ALTER PROCEDURE dbo.usp_CancelOrder
    @OrderId int,
    @Reason  nvarchar(200)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Status tinyint;
    SELECT @Status = Status FROM dbo.Orders WHERE OrderId = @OrderId;
    IF @Status IN (2, 3)
    BEGIN
        RAISERROR(N'배송이 시작된 주문은 취소할 수 없습니다.', 16, 1);
        RETURN;
    END
    UPDATE dbo.Orders SET Status = 9, CancelledAt = SYSDATETIME(), CancelReason = @Reason WHERE OrderId = @OrderId;
END
""";

var cases = new (string Name, string Sql)[]
{
    ("a-valid", body),
    ("b-bad-column", body.Replace("CancelReason = @Reason", "CancelReasonX = @Reason")),
    ("c-missing-table", body.Replace("FROM dbo.Orders WHERE", "FROM dbo.OrdersX WHERE")),
    ("d-syntax", body.Replace("SELECT @Status", "SELEC @Status")),
};

string Definition()
{
    using var c = new SqlConnection(Conn("sa", saPassword));
    c.Open();
    using var cmd = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.usp_CancelOrder'))", c);
    return (string)cmd.ExecuteScalar();
}

var original = Definition();
Console.WriteLine("user,order,case,errors,definition_unchanged");
foreach (var (user, pwd) in users)
{
    foreach (var order in new[] { "noexec", "tran+noexec" })
    {
        foreach (var (name, sql) in cases)
        {
            var errors = new List<string>();
            using (var conn = new SqlConnection(Conn(user, pwd)))
            {
                conn.Open();
                void Exec(string s)
                {
                    try { using var cmd = new SqlCommand(s, conn); cmd.ExecuteNonQuery(); }
                    catch (SqlException ex)
                    {
                        foreach (SqlError e in ex.Errors) errors.Add($"{e.Number}@L{e.LineNumber}:{e.Message.Replace(',', ';')}");
                    }
                }
                if (order == "tran+noexec") Exec("BEGIN TRAN;");
                Exec("SET NOEXEC ON;");
                Exec(sql);
                Exec("SET NOEXEC OFF;");
                if (order == "tran+noexec") Exec("IF @@TRANCOUNT > 0 ROLLBACK;");
            }
            var unchanged = Definition() == original;
            Console.WriteLine($"{user},{order},{name},\"{(errors.Count == 0 ? "none" : string.Join(" | ", errors))}\",{unchanged}");
        }
    }
}

// 참고: NOEXEC 없이 sa로 실행하면 어떤 오류가 나는지 (c 사례: 지연 이름 확인 확인용) — 트랜잭션 안에서 실행 후 롤백
using (var conn = new SqlConnection(Conn("sa", saPassword)))
{
    conn.Open();
    using var tx = conn.BeginTransaction();
    foreach (var (name, sql) in cases)
    {
        try { using var cmd = new SqlCommand(sql, conn, tx); cmd.ExecuteNonQuery(); Console.WriteLine($"sa,real-alter-in-tran,{name},\"none\","); }
        catch (SqlException ex) { Console.WriteLine($"sa,real-alter-in-tran,{name},\"{string.Join(" | ", ex.Errors.Cast<SqlError>().Select(e => $"{e.Number}@L{e.LineNumber}:{e.Message.Replace(',', ';')}"))}\","); }
    }
    tx.Rollback();
}
Console.WriteLine($"final definition unchanged: {Definition() == original}");
