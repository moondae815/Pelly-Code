using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Models;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>The whole SQL pipeline (CreateSql) with a fake model and a fake SQL executor.</summary>
public sealed class SqlFlowTests : IDisposable
{
    private const string Conn = "Server=sql1;Database=Shop;User Id=u;Password=Secret123;";
    private const string Search = "    SELECT Id FROM dbo.T WHERE Id = @Id;";
    private const string Good = "    SELECT Id FROM dbo.T WHERE Id = @Id AND Id > 0;";
    private const string BadColumn = "    SELECT Id FROM dbo.T WHERE Id = @Id AND Bogus > 0;";

    private static readonly string Script =
        "SET ANSI_NULLS ON\nGO\nCREATE PROCEDURE dbo.usp_P @Id int\nAS\nBEGIN\n" + Search + "\nEND\nGO\nSELECT 1\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-sql-flow-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly FakeSqlExecutor _sql = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly PellyOptions _options = new()
    {
        Sql = new SqlOptions { AllowedHosts = ["sql1"], AllowedDatabases = ["Shop"], ConnectionString = Conn },
    };

    public SqlFlowTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        _sql.Schemas.Add(new SqlTableSchema(SqlObjectName.Parse("dbo.T"), "TABLE", [new("Id", "int", false, true)], []));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string PathA => Path.Combine(_root, "a.sql");

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private string Prepare(string text = "")
    {
        File.WriteAllText(PathA, string.IsNullOrEmpty(text) ? Script : text, new UTF8Encoding(false));
        return File.ReadAllText(PathA);
    }

    private Task<int> Run(int? maxRetries = null, CancellationToken? ct = null)
    {
        var request = new RunRequest(PathA, null, null, null, "add a filter", true, false, maxRetries, null, EditKind.Sql);
        var orchestrator = FsmOrchestratorFactory.CreateSql(_fs, _backend, _ui, _options, TimeProvider.System, _sql);
        return orchestrator.RunAsync(request, ct ?? TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Success_FirstAttempt_Exit0_FileChanged()
    {
        Prepare();
        _backend.Enqueue(Block(Search, Good));

        var exit = await Run();

        Assert.Equal(0, exit);
        Assert.Equal(Script.Replace(Search, Good), File.ReadAllText(PathA));
        Assert.Equal(2, _sql.Calls.Count(c => c == "Compile")); // original once, edited once
    }

    [Fact]
    public async Task MissingColumn_FeedbackHasMsg207_RetrySucceeds()
    {
        Prepare();
        _sql.Compile = batch => batch.Contains("Bogus")
            ? [new SqlCompileError(207, 4, "Invalid column name 'Bogus'.")]
            : [];
        _backend.Enqueue(Block(Search, BadColumn)); // attempt 1 writes a changed target
        _backend.Enqueue("Fix the error.</think>", evalCount: 10);
        _backend.Enqueue(Block(BadColumn, Good));   // attempt 2 patches the already-changed file

        var exit = await Run();

        Assert.Equal(0, exit);
        Assert.Equal(3, _backend.Requests.Count); // attempt 1, then attempt 2 as a think step plus a block
        Assert.Contains("SQL CHECK FAILED", _backend.Requests[1].RawPrompt);
        Assert.Contains("Msg207", _backend.Requests[1].RawPrompt);
        Assert.Equal(Script.Replace(Search, Good), File.ReadAllText(PathA));
    }

    [Fact]
    public async Task NewMissingTable_Is208()
    {
        Prepare();
        _sql.Missing.Add(SqlObjectName.Parse("dbo.NoSuch"));
        var bad = "    SELECT Id FROM dbo.NoSuch WHERE Id = @Id;";
        _backend.Enqueue(Block(Search, bad));
        _backend.Enqueue("Fix the error.</think>", evalCount: 10);
        _backend.Enqueue(Block(bad, Good));

        var exit = await Run();

        Assert.Equal(0, exit);
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("Msg208", prompt);
        Assert.Contains("Invalid object name 'dbo.NoSuch'.", prompt);
        Assert.Contains("a.sql(6", prompt); // line in the file
    }

    [Fact]
    public async Task OriginalAlreadyMissingTable_IsNotCounted()
    {
        Prepare();
        _sql.Missing.Add(SqlObjectName.Parse("dbo.T"));
        _backend.Enqueue(Block(Search, Good));

        var exit = await Run();

        Assert.Equal(0, exit);
        Assert.Single(_ui.Warnings, w => w.StartsWith("원본 객체가 이미 없는 객체(dbo.T)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OriginalCompileError_IsNotCounted()
    {
        Prepare();
        _sql.Compile = _ => [new SqlCompileError(207, 3, "Invalid column name 'Old'.")];
        _backend.Enqueue(Block(Search, Good));

        var exit = await Run();

        Assert.Equal(0, exit);
        Assert.Contains("원본 객체에 이미 컴파일 오류가 1개 있습니다 — 새로 생긴 오류만 검사합니다", _ui.Warnings);
    }

    [Fact]
    public async Task RetriesExhausted_Exit1_Restored()
    {
        var before = Prepare();
        _sql.Compile = batch => batch.Contains("Bogus") ? [new SqlCompileError(207, 4, "Invalid column name 'Bogus'.")] : [];
        _backend.Enqueue(Block(Search, BadColumn));
        _backend.Enqueue("Fix the error.</think>", evalCount: 10);
        _backend.Enqueue(Block(BadColumn, BadColumn + " -- 2"));
        _backend.Enqueue("Fix the error.</think>", evalCount: 10);
        _backend.Enqueue(Block(BadColumn + " -- 2", BadColumn + " -- 3"));

        var exit = await Run(maxRetries: 2);

        Assert.Equal(1, exit);
        Assert.Equal(before, File.ReadAllText(PathA));
    }

    [Fact]
    public async Task ExecutorDiesDuringVerify_Exit4_Restored()
    {
        var before = Prepare();
        var calls = 0;
        _sql.Compile = _ => ++calls == 1 ? [] : throw new SqlUnavailableException("connection lost");
        _backend.Enqueue(Block(Search, Good));

        var exit = await Run();

        Assert.Equal(4, exit);
        Assert.Equal(before, File.ReadAllText(PathA));
    }

    [Fact]
    public async Task DynamicSqlNotice_ShownOnce_AcrossRetries()
    {
        var dynamic = Script.Replace(Search, Search + "\n    EXEC('SELECT 1');");
        Prepare(dynamic);
        _sql.Compile = batch => batch.Contains("Bogus") ? [new SqlCompileError(207, 4, "Invalid column name 'Bogus'.")] : [];
        _backend.Enqueue(Block(Search, BadColumn));
        _backend.Enqueue("Fix the error.</think>", evalCount: 10);
        _backend.Enqueue(Block(BadColumn, Good));

        var exit = await Run();

        Assert.Equal(0, exit);
        Assert.Single(_ui.Warnings, w => w.StartsWith("동적 SQL 안의 문장은 검증하지 않습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_DuringEdit_Exit2_FileUntouched()
    {
        var before = Prepare();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _backend.Enqueue(Block(Search, Good));
        _backend.BeforeRespond = _ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        var exit = await Run(ct: cts.Token);

        Assert.Equal(2, exit);
        Assert.Equal(before, File.ReadAllText(PathA));
    }
}
