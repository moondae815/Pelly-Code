using System.IO.Abstractions;
using PellyCode.McpServer.Impact;

namespace PellyCode.McpServer.Tests;

/// <summary>
/// One repo with copies of eval/corpus/Asp, Sql and Modern, a pelly.json with <c>asp</c>·<c>web</c> sections (site root Asp)
/// and a C# file that names stored procedures in string literals.
/// </summary>
public sealed class WebSqlRepoFixture : IDisposable
{
    public const string ProceduresFile = "Modern/Shop.Api/Data/OrderProcedures.cs";

    public const string Procedures = """
        namespace Shop.Api.Data;

        public static class OrderProcedures
        {
            public static string Cancel() => "dbo.usp_CancelOrder";

            public static string Ship() => "EXEC dbo.usp_ShipOrder @OrderId";

            public static string Expire() => "usp_ExpirePendingOrders";
        }

        """;

    public const string AspConfig = """
        {
          "asp": {
            "siteRoot": "Asp",
            "sqlSanitizers": ["SafeSQL"],
            "spCallFunctions": [{ "name": "ExecSP", "argument": 1 }]
          },
          "web": {
            "globals": ["gfnOpenPopup"]
          }
        }

        """;

    public string Root { get; } = Create(AspConfig);

    /// <summary>A committed repo holding Asp, Sql and Modern, the procedures file and <paramref name="pellyJson"/>.</summary>
    public static string Create(string pellyJson)
    {
        var dir = TempRepo.Create();
        TempRepo.CopyCorpus(dir, "Asp");
        TempRepo.CopyCorpus(dir, "Sql");
        TempRepo.CopyCorpus(dir, "Modern");
        File.WriteAllText(Path.Combine(dir, ProceduresFile), Procedures);
        File.WriteAllText(Path.Combine(dir, "pelly.json"), pellyJson);
        File.WriteAllText(Path.Combine(dir, ".gitignore"), ".pelly/\n");
        TempRepo.Commit(dir);
        return dir;
    }

    public void Dispose() => TempRepo.Delete(Root);
}

public sealed class ImpactWebSqlTests(WebSqlRepoFixture fx) : IClassFixture<WebSqlRepoFixture>
{
    private static readonly FileSystem Fs = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ImpactService Service(string root, int maxSiteFiles = 2000) =>
        new(Fs, ServerContext.TryCreate(Fs, root, out var error) ?? throw new InvalidOperationException(error), new FailingLoader(), maxSiteFiles);

    private static string[] Lines(string text) => text.Split('\n');

    [Fact]
    public async Task Sql_ByLine_ListsAspAndCsCallers()
    {
        var text = await Service(fx.Root).FindAsync("Sql/objects/usp_CancelOrder.sql", 20, null, Ct);

        var lines = Lines(text);
        Assert.Equal("대상: dbo.usp_CancelOrder (Sql/objects/usp_CancelOrder.sql:9)", lines[0]);
        Assert.True(lines[1].StartsWith("부르는 곳 ", StringComparison.Ordinal) && lines[1].EndsWith("곳 (이름 기준):", StringComparison.Ordinal), text);
        Assert.Contains("- Asp/admin/order/save.asp:23 페이지 본문 — SP 호출", lines);
        Assert.Contains("- Modern/Shop.Api/Data/OrderProcedures.cs:5 OrderProcedures.Cancel — 이름 호출(문자열)", lines);
        Assert.DoesNotContain(lines, l => l.Contains("usp_ShipOrder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sql_ByName()
    {
        var text = await Service(fx.Root).FindAsync("Sql/objects/Order_Batch.sql", null, "usp_ExpirePendingOrders", Ct);

        var lines = Lines(text);
        Assert.Equal("대상: dbo.usp_ExpirePendingOrders (Sql/objects/Order_Batch.sql:49)", lines[0]);
        Assert.Contains("- Modern/Shop.Api/Data/OrderProcedures.cs:9 OrderProcedures.Expire — 이름 호출(문자열)", lines);
        Assert.DoesNotContain(lines, l => l.Contains("Ship", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sql_NoLine_AllObjectsInFile()
    {
        var text = await Service(fx.Root).FindAsync("Sql/objects/Order_Batch.sql", null, null, Ct);

        var lines = Lines(text);
        Assert.Equal("대상: dbo.usp_ShipOrder, dbo.usp_ExpirePendingOrders (Sql/objects/Order_Batch.sql)", lines[0]);
        Assert.Contains("- Modern/Shop.Api/Data/OrderProcedures.cs:7 OrderProcedures.Ship — 이름 호출(문자열) (dbo.usp_ShipOrder)", lines);
        Assert.Contains("- Modern/Shop.Api/Data/OrderProcedures.cs:9 OrderProcedures.Expire — 이름 호출(문자열) (dbo.usp_ExpirePendingOrders)", lines);
    }

    [Fact]
    public async Task Sql_LineOutsideObject_Error()
    {
        var text = await Service(fx.Root).FindAsync("Sql/objects/usp_CancelOrder.sql", 1, null, Ct);

        Assert.Equal("Sql/objects/usp_CancelOrder.sql:1 줄에 SQL 객체(프로시저·함수·뷰)가 없습니다", text);
    }

    [Fact]
    public async Task Js_ByName_ListsPageCallers()
    {
        var text = await Service(fx.Root).FindAsync("Asp/js/order/history.js", null, "fnHistory", Ct);

        var lines = Lines(text);
        Assert.Equal("대상: fnHistory (Asp/js/order/history.js:2)", lines[0]);
        Assert.True(lines[1].StartsWith("부르는 곳 ", StringComparison.Ordinal) && lines[1].EndsWith("곳 (이름 기준):", StringComparison.Ordinal), text);
        Assert.Contains("- Asp/admin/order/history.asp:12 onclick 속성 — 호출", lines);
        Assert.Contains("- Asp/admin/order/history.asp:17 goHistory — 호출", lines);
    }

    [Fact]
    public async Task Js_InPageScript_ByLine()
    {
        var text = await Service(fx.Root).FindAsync("Asp/admin/order/memo.asp", 17, null, Ct);

        var lines = Lines(text);
        Assert.Equal("대상: fnSaveMemo (Asp/admin/order/memo.asp:15)", lines[0]);
        Assert.Contains("- Asp/admin/order/memo.asp:12 onclick 속성 — 호출", lines);
    }

    [Fact]
    public async Task Js_NoLineNoName_Error()
    {
        var text = await Service(fx.Root).FindAsync("Asp/js/order/history.js", null, null, Ct);

        Assert.Equal("line이나 name을 주세요", text);
    }

    [Fact]
    public async Task Js_LineOutsideFunction_Error()
    {
        var text = await Service(fx.Root).FindAsync("Asp/admin/order/memo.asp", 3, null, Ct);

        Assert.Equal("Asp/admin/order/memo.asp:3 줄에 JS 함수가 없습니다", text);
    }

    [Fact]
    public async Task NoAspSection_SkipsWebPart()
    {
        var root = WebSqlRepoFixture.Create("{}\n");
        try
        {
            var sql = await Service(root).FindAsync("Sql/objects/usp_CancelOrder.sql", 20, null, Ct);
            var sqlLines = Lines(sql);
            Assert.Equal("설정에 asp 절이 없어 ASP·JS 부분은 건너뜀", sqlLines[1]);
            Assert.Contains("- Modern/Shop.Api/Data/OrderProcedures.cs:5 OrderProcedures.Cancel — 이름 호출(문자열)", sqlLines);
            Assert.DoesNotContain(sqlLines, l => l.StartsWith("- Asp/", StringComparison.Ordinal));

            var js = await Service(root).FindAsync("Asp/js/order/history.js", null, "fnHistory", Ct);
            Assert.Equal("설정에 asp 절이 없어 ASP·JS 부분은 건너뜀", js);
        }
        finally { TempRepo.Delete(root); }
    }

    [Fact]
    public async Task IndexTruncated_NotesIt()
    {
        var text = await Service(fx.Root, maxSiteFiles: 1).FindAsync("Asp/js/order/history.js", null, "fnHistory", Ct);

        var lines = Lines(text);
        Assert.Equal("대상: fnHistory (Asp/js/order/history.js:2)", lines[0]);
        Assert.Equal("사이트 색인이 파일 수 상한에서 잘려 빠진 곳이 있을 수 있습니다", lines[1]);
    }
}
