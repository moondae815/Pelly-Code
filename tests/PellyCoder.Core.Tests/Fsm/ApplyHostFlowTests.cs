using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>Runs inside a <c>pelly apply</c> host (caller-held lock and workspace) and the detailed run result.</summary>
public sealed class ApplyHostFlowTests : IDisposable
{
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(60);

    private const string CalcSource =
        "namespace Demo;\n" +
        "\n" +
        "public class Calc\n" +
        "{\n" +
        "    public int Divide(int a, int b)\n" +
        "    {\n" +
        "        return a / b;\n" +
        "    }\n" +
        "}\n";

    private const string Original = "        return a / b;";
    private const string Guarded = "        if (b == 0) throw new DivideByZeroException();\n        return a / b;";
    private const string Broken = "        return a / b";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-host-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly PellyOptions _options = new();
    private readonly ScriptedProcessRunner _processes = new();

    public ApplyHostFlowTests()
    {
        _processes.WhenEmpty(new ProcessResult(0, "", "", [], TimeSpan.Zero, false));
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        File.WriteAllText(Path.Combine(_root, "Demo", "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(CalcPath, CalcSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");

    private Workspace Ws => new(_root, Path.Combine(_root, ".pelly"));

    private static RunRequest Req(string path, ApplyHost? host = null, bool yes = true) =>
        new(path, null, "Divide", null, "Throw DivideByZeroException when b is zero.", yes, false, null, Host: host);

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private Task<RunResult> Run(RunRequest request) =>
        FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes)
            .RunDetailedAsync(request, TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    [Fact]
    public async Task Run_with_host_does_not_take_the_lock()
    {
        using var held = WorkspaceLock.TryAcquire(_fs, Ws);
        Assert.NotNull(held);
        _backend.Enqueue(Block(Original, Guarded));

        var result = await Run(Req(CalcPath, new ApplyHost(Ws)));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("다른 pelly 실행이 진행 중"));
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        // The caller's lock is still held: the run did not release it.
        Assert.Null(WorkspaceLock.TryAcquire(_fs, Ws));
    }

    [Fact]
    public async Task Run_without_host_still_refuses_when_locked()
    {
        using var held = WorkspaceLock.TryAcquire(_fs, Ws);
        _backend.Enqueue(Block(Original, Guarded));

        var result = await Run(Req(CalcPath));

        Assert.Equal(4, result.ExitCode);
        Assert.Contains(_ui.Warnings, w => w.Contains("다른 pelly 실행이 진행 중"));
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task Run_with_host_rejects_file_outside_root()
    {
        var other = Path.Combine(_root, "Other");
        Directory.CreateDirectory(other);
        _backend.Enqueue(Block(Original, Guarded));

        var result = await Run(Req(CalcPath, new ApplyHost(new Workspace(other, Path.Combine(other, ".pelly")))));

        Assert.Equal(4, result.ExitCode);
        Assert.Contains(_ui.Warnings, w => w.Contains("대상 파일이 apply 작업 공간 밖입니다"));
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
        Assert.Empty(_backend.Requests);
        // Failed before the file was read: nothing to track.
        Assert.Null(result.Original);
        Assert.Null(result.Final);
        Assert.Equal("EnvError", result.FailureKind);
    }

    [Fact]
    public async Task Run_with_host_rejects_sibling_folder_sharing_the_root_prefix()
    {
        var sibling = _root + "-x";
        Directory.CreateDirectory(sibling);
        try
        {
            var path = Path.Combine(sibling, "Calc.cs");
            File.WriteAllText(path, CalcSource);

            var result = await Run(Req(path, new ApplyHost(Ws)));

            Assert.Equal(4, result.ExitCode);
            Assert.Contains(_ui.Warnings, w => w.Contains("대상 파일이 apply 작업 공간 밖입니다"));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public async Task Detailed_result_carries_run_id_and_final_snapshot()
    {
        _backend.Enqueue(Block(Original, Guarded));

        var result = await Run(Req(CalcPath, new ApplyHost(Ws)));

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrEmpty(result.RunId));
        Assert.True(Directory.Exists(Path.Combine(_root, ".pelly", "runs", result.RunId)));
        Assert.Equal(Path.GetFullPath(CalcPath), result.FilePath);
        Assert.NotNull(result.Original);
        Assert.Equal(CalcSource, result.Original.Text);
        Assert.NotNull(result.Final);
        Assert.Contains("DivideByZeroException", result.Final.Text);
        Assert.Null(result.FailureKind);
    }

    [Fact]
    public async Task Detailed_result_after_verify_failure_is_the_restored_original()
    {
        _backend.Enqueue(Block(Original, Broken));
        for (var i = 0; i < 3; i++)
        {
            _backend.Enqueue("Retry.</think>", evalCount: 5);
            _backend.Enqueue(Block(Broken, Broken));
        }

        var result = await Run(Req(CalcPath, new ApplyHost(Ws)));

        Assert.Equal(1, result.ExitCode);
        Assert.NotNull(result.Original);
        Assert.NotNull(result.Final);
        Assert.Equal(result.Original.Sha256, result.Final.Sha256);
        Assert.Equal("SyntaxErrors", result.FailureKind);
    }

    [Fact]
    public async Task Detailed_result_after_rejection_is_the_restored_original()
    {
        _ui.IsInteractive = true;
        _ui.ConfirmAnswers.Enqueue(false);
        _backend.Enqueue(Block(Original, Guarded));

        var result = await Run(Req(CalcPath, new ApplyHost(Ws), yes: false));

        Assert.Equal(2, result.ExitCode);
        Assert.NotNull(result.Original);
        Assert.NotNull(result.Final);
        Assert.Equal(result.Original.Sha256, result.Final.Sha256);
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
        Assert.Equal("Rejected", result.FailureKind);
    }

    [Fact]
    public async Task Detailed_result_is_null_when_rollback_meets_an_external_change()
    {
        // Attempt 1 writes a syntax error; before attempt 2 the user edits the file, so the rollback must not overwrite it.
        var external = CalcSource.Replace("return a / b;", "return a / b; // user edit");
        _backend.Enqueue(Block(Original, Broken));
        _ui.OnStatus = (phase, attempt) =>
        {
            if (phase == RunPhase.Slice && attempt == 1) File.WriteAllText(CalcPath, external);
        };

        var result = await Run(Req(CalcPath, new ApplyHost(Ws)));

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(external, File.ReadAllText(CalcPath));
        Assert.NotNull(result.Original);
        Assert.Null(result.Final);
    }

    [Fact]
    public async Task Run_async_returns_the_detailed_exit_code()
    {
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes)
            .RunAsync(Req(CalcPath), TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Sql_run_with_host_does_not_take_the_lock()
    {
        const string search = "    SELECT Id FROM dbo.T WHERE Id = @Id;";
        const string good = "    SELECT Id FROM dbo.T WHERE Id = @Id AND Id > 0;";
        var script = "SET ANSI_NULLS ON\nGO\nCREATE PROCEDURE dbo.usp_P @Id int\nAS\nBEGIN\n" + search + "\nEND\nGO\nSELECT 1\n";
        var sqlPath = Path.Combine(_root, "a.sql");
        File.WriteAllText(sqlPath, script, new UTF8Encoding(false));
        var sql = new FakeSqlExecutor();
        sql.Schemas.Add(new SqlTableSchema(SqlObjectName.Parse("dbo.T"), "TABLE", [new("Id", "int", false, true)], []));
        _options.Sql = new SqlOptions
        {
            AllowedHosts = ["sql1"], AllowedDatabases = ["Shop"], ConnectionString = "Server=sql1;Database=Shop;User Id=u;Password=Secret123;",
        };
        using var held = WorkspaceLock.TryAcquire(_fs, Ws);
        _backend.Enqueue(Block(search, good));

        var request = new RunRequest(sqlPath, null, null, null, "add a filter", true, false, null, null, EditKind.Sql, Host: new ApplyHost(Ws));
        var result = await FsmOrchestratorFactory.CreateSql(_fs, _backend, _ui, _options, TimeProvider.System, sql)
            .RunDetailedAsync(request, TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(script.Replace(search, good), File.ReadAllText(sqlPath));
        Assert.NotNull(result.Final);
        Assert.Contains("Id > 0", result.Final.Text);
    }
}
