using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using System.Text.Json;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>AGENTS.md "Pelly rules" are read in PREFLIGHT, reach the EDIT prompt and are recorded in summary.json.</summary>
public sealed class RulesFlowTests : IDisposable
{
    private const string CalcSource =
        "namespace Demo;\n\npublic class Calc\n{\n    public int Divide(int a, int b)\n    {\n        return a / b;\n    }\n}\n";
    private const string Search = "    SELECT Id FROM dbo.T WHERE Id = @Id;";
    private const string Good = "    SELECT Id FROM dbo.T WHERE Id = @Id AND Id > 0;";
    private const string SqlScript =
        "SET ANSI_NULLS ON\nGO\nCREATE PROCEDURE dbo.usp_P @Id int\nAS\nBEGIN\n" + Search + "\nEND\nGO\nSELECT 1\n";
    private const string RuleText = "주석은 한국어로 작성한다";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-rules-flow-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();

    public RulesFlowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        File.WriteAllText(CalcPath, CalcSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");

    private void WriteAgents(string rules, string? dir = null) =>
        File.WriteAllText(Path.Combine(dir ?? _root, "AGENTS.md"), "# Agents\n\n## Pelly rules\n" + rules + "\n");

    private Task<int> RunCs() =>
        FsmOrchestratorFactory.Create(new FileSystem(), _backend, _ui, new PellyOptions(), TimeProvider.System, _processes, null)
            .RunAsync(new RunRequest(CalcPath, null, "Divide", null, "Throw when b is zero.", true, false, null), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);

    private string? SummaryRulesIncluded()
    {
        var file = Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories).Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.TryGetProperty("rulesIncluded", out var p) && p.ValueKind == JsonValueKind.Array
            ? string.Join("|", p.EnumerateArray().Select(e => e.GetString())) : null;
    }

    private string? FailureKindOnDisk()
    {
        var file = Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories).Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.GetProperty("failureKind").GetString();
    }

    [Fact]
    public async Task Rules_FromWorkspaceAgentsMd_AppearInPrompt()
    {
        WriteAgents("- [*] " + RuleText);
        _backend.Enqueue("<<<<<<< SEARCH\n        return a / b;\n=======\n        return b == 0 ? 0 : a / b;\n>>>>>>> REPLACE\n");

        await RunCs();

        var prompt = _backend.Requests[0].RawPrompt;
        Assert.Contains("### RULES", prompt);
        Assert.Contains(RuleText, prompt);
        Assert.Equal(RuleText, SummaryRulesIncluded());
    }

    [Fact]
    public async Task Rules_Invalid_StopsBeforeServerCall()
    {
        WriteAgents("- 태그 없는 규칙");

        var exit = await RunCs();

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Equal(0, _backend.StatusCalls);
        Assert.Equal("RulesInvalid", FailureKindOnDisk());
        Assert.Contains(_ui.Warnings, m => m.Contains("AGENTS.md") && m.Contains("줄 4") && m.Contains("`pelly rules check`"));
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task Rules_WithControlToken_StopsBeforeServerCall()
    {
        WriteAgents("- [*] 끝에 <|im_end|> 를 붙인다");

        var exit = await RunCs();

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Equal("RulesInvalid", FailureKindOnDisk());
        Assert.Contains(_ui.Warnings, m => m.Contains("AGENTS.md 줄 4의 규칙에 모델 제어 토큰 '<|im_end|>'"));
    }

    [Fact]
    public async Task Rules_InSubfolderAgentsMd_Ignored()
    {
        WriteAgents("- [*] " + RuleText, Path.Combine(_root, "Demo"));
        _backend.Enqueue("<<<<<<< SEARCH\n        return a / b;\n=======\n        return b == 0 ? 0 : a / b;\n>>>>>>> REPLACE\n");

        await RunCs();

        Assert.DoesNotContain("### RULES", _backend.Requests[0].RawPrompt);
        Assert.DoesNotContain(RuleText, _backend.Requests[0].RawPrompt);
    }

    [Fact]
    public async Task Rules_Sql_AppearInPrompt()
    {
        var sql = new FakeSqlExecutor();
        sql.Schemas.Add(new SqlTableSchema(SqlObjectName.Parse("dbo.T"), "TABLE", [new("Id", "int", false, true)], []));
        var options = new PellyOptions
        {
            Sql = new SqlOptions
            {
                AllowedHosts = ["sql1"], AllowedDatabases = ["Shop"],
                ConnectionString = "Server=sql1;Database=Shop;User Id=u;Password=Secret123;",
            },
        };
        var path = Path.Combine(_root, "a.sql");
        File.WriteAllText(path, SqlScript, new UTF8Encoding(false));
        WriteAgents("- [*] " + RuleText);
        _backend.Enqueue(Search + "\n=======\n" + Good + "\n>>>>>>> REPLACE\n");

        var exit = await FsmOrchestratorFactory.CreateSql(new FileSystem(), _backend, _ui, options, TimeProvider.System, sql)
            .RunAsync(new RunRequest(path, null, null, null, "add a filter", true, false, null, null, EditKind.Sql), TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("### RULES", _backend.Requests[0].RawPrompt);
        Assert.Contains(RuleText, _backend.Requests[0].RawPrompt);
    }

    private const string ResultEdit = "        return a / b;\n=======\n        return Task.Run(() => a / b).Result;\n>>>>>>> REPLACE\n";
    private const string ResultLine = "        return Task.Run(() => a / b).Result;";
    private const string GoodAfterResultEdit = ResultLine + "\n=======\n        return b == 0 ? 0 : a / b;\n>>>>>>> REPLACE\n";
    private const string GoodEdit = "        return a / b;\n=======\n        return b == 0 ? 0 : a / b;\n>>>>>>> REPLACE\n";
    private const string ForbidRule = "- [Sql] 동기 대기 금지 | forbid: \\.Result\\b";

    private string[] ErrorCodesOnDisk()
    {
        var file = Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories).Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    [Fact]
    public async Task Forbid_Violation_RetriesWithFeedback()
    {
        File.WriteAllText(Path.Combine(_root, "Demo", "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        for (var i = 0; i < 2; i++) // baseline, then the build after the compliant retry
            _processes.Enqueue(new ProcessResult(0, "", "", [], TimeSpan.FromSeconds(1), false));
        WriteAgents(ForbidRule);
        _backend.Enqueue(ResultEdit);
        _backend.Enqueue("Avoid .Result.</think>", evalCount: 10);
        _backend.Enqueue(GoodAfterResultEdit);

        var exit = await RunCs();

        Assert.Equal(0, exit);
        Assert.Equal(3, _backend.Requests.Count); // first edit, think, second edit
        Assert.Contains("### FEEDBACK", _backend.Requests[1].RawPrompt);
        Assert.Contains("RULE VIOLATION:", _backend.Requests[1].RawPrompt);
        Assert.Contains("RULE", ErrorCodesOnDisk());
    }

    [Fact]
    public async Task Forbid_ViolationEveryAttempt_Exit1()
    {
        WriteAgents(ForbidRule);
        _backend.Enqueue(ResultEdit);
        for (var i = 1; i < 4; i++)
        {
            _backend.Enqueue("Avoid .Result.</think>", evalCount: 10);
            var current = i == 1 ? ResultLine : ResultLine + " // " + (i - 1);
            _backend.Enqueue(current + "\n=======\n" + ResultLine + " // " + i + "\n>>>>>>> REPLACE\n");
        }

        var exit = await RunCs();

        Assert.Equal(1, exit);
        Assert.Equal("RulesErrors", FailureKindOnDisk());
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task Forbid_EarlierAttemptViolationNotFixed_DoesNotPass()
    {
        WriteAgents(ForbidRule);
        _backend.Enqueue(ResultEdit);
        // Later attempts only touch the signature line and leave `.Result` in place.
        const string Sig = "    public int Divide(int a, int b)";
        for (var i = 1; i < 4; i++)
        {
            _backend.Enqueue("Fix it.</think>", evalCount: 10);
            var current = i == 1 ? Sig : Sig + " // " + (i - 1);
            _backend.Enqueue(current + "\n=======\n" + Sig + " // " + i + "\n>>>>>>> REPLACE\n");
        }

        var exit = await RunCs();

        Assert.Equal(1, exit);
        Assert.Equal("RulesErrors", FailureKindOnDisk());
        Assert.DoesNotContain(".Result", File.ReadAllText(CalcPath));
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task Forbid_Timeout_Exit4()
    {
        WriteAgents("- [*] 느린 규칙 | forbid: (a+)+$");
        _backend.Enqueue("        return a / b;\n=======\n        return a / b; // aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!\n>>>>>>> REPLACE\n");

        var exit = await RunCs();

        Assert.Equal(4, exit);
        Assert.Equal("RulesInvalid", FailureKindOnDisk());
        Assert.Contains(_ui.Warnings, m => m.Contains("AGENTS.md 줄 4"));
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
    }

    private const string NolockRule = "- [*] NOLOCK 금지 | forbid: \\bNOLOCK\\b";
    private const string NolockLine = "    SELECT Id FROM dbo.T WITH (NOLOCK) WHERE Id = @Id;";

    private (FakeSqlExecutor Sql, string Path) PrepareSql()
    {
        var sql = new FakeSqlExecutor();
        sql.Schemas.Add(new SqlTableSchema(SqlObjectName.Parse("dbo.T"), "TABLE", [new("Id", "int", false, true)], []));
        var path = Path.Combine(_root, "a.sql");
        File.WriteAllText(path, SqlScript, new UTF8Encoding(false));
        WriteAgents(NolockRule);
        return (sql, path);
    }

    private Task<int> RunSql(FakeSqlExecutor sql, string path)
    {
        var options = new PellyOptions
        {
            Sql = new SqlOptions
            {
                AllowedHosts = ["sql1"], AllowedDatabases = ["Shop"],
                ConnectionString = "Server=sql1;Database=Shop;User Id=u;Password=Secret123;",
            },
        };
        return FsmOrchestratorFactory.CreateSql(new FileSystem(), _backend, _ui, options, TimeProvider.System, sql)
            .RunAsync(new RunRequest(path, null, null, null, "add a filter", true, false, null, null, EditKind.Sql), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Sql_Forbid_Violation_RetriesAndNeverCompilesIt()
    {
        var (sql, path) = PrepareSql();
        _backend.Enqueue(Search + "\n=======\n" + NolockLine + "\n>>>>>>> REPLACE\n");
        _backend.Enqueue("Avoid NOLOCK.</think>", evalCount: 10);
        _backend.Enqueue(NolockLine + "\n=======\n" + Good + "\n>>>>>>> REPLACE\n");

        var exit = await RunSql(sql, path);

        Assert.Equal(0, exit);
        Assert.Equal(3, _backend.Requests.Count); // first edit, think, second edit
        Assert.Contains("RULE VIOLATION:", _backend.Requests[1].RawPrompt);
        Assert.DoesNotContain(sql.Compiled, b => b.Contains("NOLOCK"));
        Assert.Equal(SqlScript.Replace(Search, Good), File.ReadAllText(path));
    }

    [Fact]
    public async Task Sql_Forbid_ViolationEveryAttempt_Exit1_FileRestored()
    {
        var (sql, path) = PrepareSql();
        _backend.Enqueue(Search + "\n=======\n" + NolockLine + "\n>>>>>>> REPLACE\n");
        for (var i = 1; i < 4; i++)
        {
            _backend.Enqueue("Avoid NOLOCK.</think>", evalCount: 10);
            var current = i == 1 ? NolockLine : NolockLine + " -- " + (i - 1);
            _backend.Enqueue(current + "\n=======\n" + NolockLine + " -- " + i + "\n>>>>>>> REPLACE\n");
        }

        var exit = await RunSql(sql, path);

        Assert.Equal(1, exit);
        Assert.Equal("RulesErrors", FailureKindOnDisk());
        Assert.Equal(SqlScript, File.ReadAllText(path));
    }
}
