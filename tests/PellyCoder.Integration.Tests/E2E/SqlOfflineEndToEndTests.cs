using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Dapper;
using SqlConnection = Microsoft.Data.SqlClient.SqlConnection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;
using PellyCoder.Core.Sql;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// The whole <c>pelly sql</c> pipeline against the real SQL Server corpus database with canned model responses
/// (<see cref="CannedLlmBackend"/>, think off), on temp copies of eval/corpus/Sql/objects. They run in CI's
/// <c>sql-integration</c> job. Every test checks that no object definition or permission in the database changed.
/// </summary>
public sealed class SqlOfflineEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private const string Status3Line = "    IF @Status = 3";

    /// <summary>sql-001: one block that adds a Status = 2 guard in front of the Status = 3 guard.</summary>
    private const string ValidCancelEdit = Status3Line + "\n=======\n"
        + "    IF @Status = 2\n    BEGIN\n        RAISERROR(N'배송 중인 주문은 취소할 수 없습니다.', 16, 1);\n        RETURN;\n    END\n\n"
        + Status3Line + "\n>>>>>>> REPLACE\n";

    private readonly SqlCorpusCopy _copy = SqlCorpusCopy.Create();

    public void Dispose() => _copy.Dispose();

    [SqlServerFact]
    public Task CancelOrder_ValidEdit_Exit0() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        var file = _copy.File("usp_CancelOrder.sql");
        var before = File.ReadAllBytes(file);
        var backend = new CannedLlmBackend([ValidCancelEdit]);

        var run = await RunAsync(file, backend);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(1, run.Attempts);
        Assert.Single(backend.Requests);
        var after = File.ReadAllBytes(file);
        var (_, target) = SqlE2E.AssertOnlyTargetChanged(before, after, "dbo.usp_CancelOrder");
        Assert.Contains("@Status = 2", target, StringComparison.Ordinal);
        Assert.True(after.AsSpan().StartsWith(Encoding.UTF8.Preamble), "BOM이 사라졌습니다");
        Assert.Equal(0, SqlE2E.CountBareLf(Encoding.UTF8.GetString(after)));
    });

    [SqlServerFact]
    public Task MissingColumn_ThenFix_Exit0_ErrorCodesHas207() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        var file = _copy.File("usp_CancelOrder.sql");
        var originalLf = SqlE2E.ReadLf(file);
        var bad = SqlE2E.InjectStatement(ValidCancelEdit, originalLf, "dbo.usp_CancelOrder", SqlE2E.MissingColumnStatement);
        var injected = "    " + SqlE2E.MissingColumnStatement;
        var fix = injected + "\n" + Status3Line + "\n=======\n" + Status3Line + "\n>>>>>>> REPLACE\n";
        var backend = new CannedLlmBackend([bad, fix]);

        var run = await RunAsync(file, backend);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(2, run.Attempts);
        Assert.Contains("Msg207", run.ErrorCodes);
        var prompt2 = backend.Requests[1].RawPrompt;
        Assert.Contains("SQL CHECK FAILED", prompt2, StringComparison.Ordinal);
        Assert.Contains("NoSuchColumn", prompt2, StringComparison.Ordinal);
        var after = SqlE2E.ReadLf(file);
        Assert.DoesNotContain("NoSuchColumn", after, StringComparison.Ordinal);
        Assert.Contains("@Status = 2", after, StringComparison.Ordinal);
    });

    [SqlServerFact]
    public Task MissingTable_IsCaughtBy208_NotByCompile() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        const string Statement = "IF EXISTS (SELECT 1 FROM dbo.NoSuchTable) RETURN;";
        var file = _copy.File("usp_CancelOrder.sql");
        var originalLf = SqlE2E.ReadLf(file);
        var bad = SqlE2E.InjectStatement(ValidCancelEdit, originalLf, "dbo.usp_CancelOrder", Statement);
        var fix = "    " + Statement + "\n" + Status3Line + "\n=======\n" + Status3Line + "\n>>>>>>> REPLACE\n";

        // S5 ⓒ: deferred name resolution — NOEXEC compiles the bad batch without any error ...
        var badBatch = SqlE2E.TargetBatch(SqlE2E.Patch(originalLf, bad), "dbo.usp_CancelOrder");
        var compileErrors = await new SqlServerExecutor(TestEnv.SqlConnection)
            .CompileWithNoExecAsync(badBatch.Text, SqlSessionSettings.Default, TestContext.Current.CancellationToken);
        Assert.Empty(compileErrors);

        // ... so only the object existence check can catch it.
        var backend = new CannedLlmBackend([bad, fix]);
        var run = await RunAsync(file, backend);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(2, run.Attempts);
        Assert.Equal(["Msg208"], run.ErrorCodes);
        var prompt2 = backend.Requests[1].RawPrompt;
        Assert.Contains("SQL CHECK FAILED", prompt2, StringComparison.Ordinal);
        Assert.Contains("Invalid object name 'dbo.NoSuchTable'.", prompt2, StringComparison.Ordinal);
        Assert.DoesNotContain("NoSuchTable", SqlE2E.ReadLf(file), StringComparison.Ordinal);
    });

    [SqlServerFact]
    public Task SettlementAll_EditsOnlyTargetBatch_GrantsUntouched() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        const string Search = "           SUM(st.PayoutAmount) AS PayoutAmount";
        var edit = Search + "\n=======\n" + Search + ",\n           COUNT(*) AS SettlementRows\n>>>>>>> REPLACE\n";
        var file = _copy.File("Settlement_All.sql");
        var before = File.ReadAllBytes(file);
        var backend = new CannedLlmBackend([edit]);

        var run = await RunAsync(file, backend, objectName: "dbo.usp_SettleMonthly");

        Assert.Equal(ExitCodes.Success, run.Exit);
        var after = File.ReadAllBytes(file);
        var (beforeTarget, afterTarget) = SqlE2E.AssertOnlyTargetChanged(before, after, "dbo.usp_SettleMonthly");
        Assert.DoesNotContain("SettlementRows", beforeTarget, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) AS SettlementRows", afterTarget, StringComparison.Ordinal);
        var text = Encoding.UTF8.GetString(after);
        Assert.Contains("GRANT EXECUTE ON dbo.usp_SettleDaily TO public\r\nGO", text, StringComparison.Ordinal);
        Assert.Contains("GRANT EXECUTE ON dbo.usp_SettleMonthly TO public\r\nGO", text, StringComparison.Ordinal);
    });

    [SqlServerFact]
    public Task ForbiddenEveryAttempt_Exit5_Restored() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        const string Search = "    SET NOCOUNT ON;";
        var edit = Search + "\n=======\n" + Search + "\n    EXEC xp_cmdshell 'dir';\n>>>>>>> REPLACE\n";
        var file = _copy.File("usp_CancelOrder.sql");
        var before = File.ReadAllBytes(file);
        var backend = new CannedLlmBackend([edit, edit, edit]);

        var run = await RunAsync(file, backend, maxRetries: 2);

        Assert.Equal(ExitCodes.SafetyRejected, run.Exit);
        Assert.Equal(3, run.Attempts);
        Assert.Equal(3, backend.Requests.Count);
        Assert.Equal("SqlSafetyRejected", run.FailureKind);
        Assert.Contains("SQL SAFETY REJECTED", backend.Requests[1].RawPrompt, StringComparison.Ordinal);
        Assert.Contains("xp_cmdshell", backend.Requests[1].RawPrompt, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(file));
    });

    [SqlServerFact]
    public Task HostNotAllowed_Exit5_NoGenerateCall() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        var file = _copy.File("usp_CancelOrder.sql");
        var before = File.ReadAllBytes(file);
        var backend = new CannedLlmBackend([ValidCancelEdit]);
        var options = SqlE2E.OfflineOptions();
        options.Sql.AllowedHosts = ["sql.example.invalid"];

        var run = await RunAsync(file, backend, options: options);

        Assert.Equal(ExitCodes.SafetyRejected, run.Exit);
        Assert.Empty(backend.Requests);
        Assert.Contains("sql.allowedHosts에 없습니다", run.Messages, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(file));
    });

    [SqlServerFact]
    public Task QuotedIdentifierOffFile_CompilesWithOff() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        var file = _copy.File("usp_CancelOrder.sql");
        var original = File.ReadAllText(file);
        Assert.Contains("SET QUOTED_IDENTIFIER ON\r\n", original, StringComparison.Ordinal);
        File.WriteAllText(file, original.Replace("SET QUOTED_IDENTIFIER ON\r\n", "SET QUOTED_IDENTIFIER OFF\r\n", StringComparison.Ordinal),
            new UTF8Encoding(true));

        // A double-quoted string literal is valid only under QUOTED_IDENTIFIER OFF.
        var edit = Status3Line + "\n=======\n"
            + "    IF @Status = 2\n    BEGIN\n        RAISERROR(\"Shipped orders cannot be cancelled.\", 16, 1);\n        RETURN;\n    END\n\n"
            + Status3Line + "\n>>>>>>> REPLACE\n";

        // The same batch fails to compile with QUOTED_IDENTIFIER ON, so passing below means OFF was used.
        var batch = SqlE2E.TargetBatch(SqlE2E.Patch(SqlE2E.ReadLf(file), edit), "dbo.usp_CancelOrder");
        var executor = new SqlServerExecutor(TestEnv.SqlConnection);
        var ct = TestContext.Current.CancellationToken;
        Assert.NotEmpty(await executor.CompileWithNoExecAsync(batch.Text, new SqlSessionSettings(true, true), ct));
        Assert.Empty(await executor.CompileWithNoExecAsync(batch.Text, new SqlSessionSettings(true, false), ct));

        var backend = new CannedLlmBackend([edit]);
        var run = await RunAsync(file, backend);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(1, run.Attempts);
        Assert.Contains("RAISERROR(\"Shipped orders cannot be cancelled.\", 16, 1);", SqlE2E.ReadLf(file), StringComparison.Ordinal);
    });

    /// <summary>The injection helper used by the real-model test, on hand-written responses (no environment needed).</summary>
    [Fact]
    public void InjectStatement_SkipsPlacesThatBreakTheBatch()
    {
        var originalLf = SqlE2E.ReadLf(CorpusPaths.Get("Sql/objects/usp_CancelOrder.sql"));

        // The REPLACE ends inside a multi-line statement: the line before it is not a statement boundary.
        const string Response = "    SELECT @Status = Status\n    FROM dbo.Orders\n    WHERE OrderId = @OrderId;\n=======\n"
            + "    SELECT @Status = Status\n    FROM dbo.Orders\n    WHERE OrderId = @OrderId\n      AND Status <> 2;\n>>>>>>> REPLACE\n";
        var injected = SqlE2E.InjectStatement(Response, originalLf, "dbo.usp_CancelOrder", SqlE2E.MissingColumnStatement);

        var patched = SqlE2E.Patch(originalLf, injected);
        Assert.Contains(SqlE2E.MissingColumnStatement, patched, StringComparison.Ordinal);
        Assert.Contains("      AND Status <> 2;", patched, StringComparison.Ordinal);
        Assert.Empty(SqlE2E.TargetBatch(patched, "dbo.usp_CancelOrder").ParseErrors);
    }

    private async Task<SqlRun> RunAsync(string file, ILlmBackend backend, string? objectName = null, int? maxRetries = null,
        PellyOptions? options = null, [CallerMemberName] string test = "")
    {
        var request = new RunRequest(file, null, null, null, SqlE2E.Sql001Request, Yes: true, DryRun: false, MaxRetries: maxRetries,
            ObjectName: objectName, Kind: EditKind.Sql);
        return await SqlE2E.RunAsync(_copy.Root, options ?? SqlE2E.OfflineOptions(), backend, request, output, test);
    }
}

/// <summary>A temp copy of eval/corpus/Sql/objects under <c>&lt;Root&gt;/objects</c>, with an empty .sln marking the workspace root.</summary>
internal sealed class SqlCorpusCopy : IDisposable
{
    private SqlCorpusCopy(string root) => Root = root;

    public string Root { get; }

    public static SqlCorpusCopy Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "pelly-sql-e2e-" + Guid.NewGuid().ToString("N"));
        CsEndToEndTests.CopyDirectory(CorpusPaths.Get("Sql/objects"), Path.Combine(root, "objects"));
        System.IO.File.WriteAllText(Path.Combine(root, "Sql.sln"), "");
        return new SqlCorpusCopy(root);
    }

    public string File(string name) => Path.Combine(Root, "objects", name);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }
}

internal sealed record SqlRun(int Exit, RecordingUserInteraction Ui, string? RunDir, JsonElement? Summary, string Messages)
{
    public int Attempts => Summary?.GetProperty("attempts").GetInt32() ?? 0;

    public string? FailureKind => Summary?.GetProperty("failureKind").GetString();

    public IReadOnlyList<string?> ErrorCodes =>
        Summary is { } s ? [.. s.GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString())] : [];

    public bool HasAttemptFile(int attempt, string name) => RunDir is not null && File.Exists(Path.Combine(RunDir, $"attempt-{attempt}", name));

    public string AttemptFile(int attempt, string name) => File.ReadAllText(Path.Combine(RunDir!, $"attempt-{attempt}", name));
}

/// <summary>Shared plumbing of the offline and real-model SQL runs.</summary>
internal static class SqlE2E
{
    public const string Sql001Request = "배송 중(Status = 2)인 주문도 취소할 수 없도록 오류를 발생시켜줘";
    public const string Sql004Request = "결과에 판매자별 이월 금액(SettlementDeferrals.Amount 합계) 열을 추가해줘";
    public const string MissingColumnStatement = "IF EXISTS (SELECT 1 FROM dbo.Orders o WHERE o.NoSuchColumn = 1) RETURN;";
    public const int CompatLevel = 160;

    /// <summary>Think off (one generate call per attempt) and the local/CI corpus database allowed.</summary>
    public static PellyOptions OfflineOptions()
    {
        var options = new PellyOptions();
        options.Llm.ThinkMode = ThinkMode.Off;
        ConfigureSql(options);
        return options;
    }

    public static void ConfigureSql(PellyOptions options)
    {
        options.Sql.ConnectionString = TestEnv.SqlConnection;
        options.Sql.AllowedHosts = ["localhost"];
        options.Sql.AllowedDatabases = ["PellyCorpus"];
        options.Sql.CompatLevel = CompatLevel;
    }

    public static async Task<SqlRun> RunAsync(string root, PellyOptions options, ILlmBackend backend, RunRequest request,
        ITestOutputHelper output, string test)
    {
        var ui = new RecordingUserInteraction();
        var orchestrator = FsmOrchestratorFactory.CreateSql(new FileSystem(), backend, ui, options, TimeProvider.System,
            new SqlServerExecutor(options.Sql.ConnectionString));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        var exit = await orchestrator.RunAsync(request, cts.Token);

        foreach (var line in ui.Lines) output.WriteLine(line);
        var runs = Path.Combine(root, ".pelly", "runs");
        var runDir = Directory.Exists(runs) ? Directory.GetDirectories(runs).SingleOrDefault() : null;
        JsonElement? summary = null;
        var messages = "";
        if (runDir is not null)
        {
            var summaryPath = Path.Combine(runDir, "summary.json");
            if (File.Exists(summaryPath))
            {
                var summaryText = File.ReadAllText(summaryPath);
                output.WriteLine(summaryText);
                using var doc = JsonDocument.Parse(summaryText);
                summary = doc.RootElement.Clone();
            }
            var messagesPath = Path.Combine(runDir, "messages.txt");
            if (File.Exists(messagesPath)) messages = File.ReadAllText(messagesPath);
            output.WriteLine(messages);

            var keep = Environment.GetEnvironmentVariable("PELLY_TEST_KEEP");
            if (!string.IsNullOrEmpty(keep))
            {
                var dest = Path.Combine(keep, test + "-" + Path.GetFileName(root));
                CsEndToEndTests.CopyDirectory(runs, dest);
                output.WriteLine($"runs kept at {dest}");
            }
        }
        return new SqlRun(exit, ui, runDir, summary, messages);
    }

    // ---- database ----

    /// <summary>Runs <paramref name="body"/> and asserts that no module definition, modify date or permission changed.</summary>
    public static async Task WithUnchangedDatabase(Func<Task> body)
    {
        var before = await SnapshotAsync();
        Assert.Contains(before, row => row.StartsWith("dbo.usp_CancelOrder\t", StringComparison.Ordinal));
        await body();
        Assert.Equal(before, await SnapshotAsync());
    }

    private static async Task<IReadOnlyList<string>> SnapshotAsync()
    {
        await using var conn = new SqlConnection(TestEnv.SqlConnection);
        var modules = await conn.QueryAsync<string>(
            "SELECT SCHEMA_NAME(o.schema_id) + '.' + o.name + CHAR(9) + CONVERT(varchar(30), o.modify_date, 126) + CHAR(9) "
            + "+ OBJECT_DEFINITION(o.object_id) FROM sys.objects o "
            + "WHERE o.is_ms_shipped = 0 AND OBJECT_DEFINITION(o.object_id) IS NOT NULL ORDER BY o.object_id");
        var permissions = await conn.QueryAsync<string>(
            "SELECT CONCAT(p.class_desc, ':', p.major_id, ':', USER_NAME(p.grantee_principal_id), ':', p.permission_name, ':', p.state_desc) "
            + "FROM sys.database_permissions p WHERE p.class = 1 ORDER BY 1");
        return [.. modules, .. permissions.Select(p => "perm\t" + p)];
    }

    // ---- file text ----

    /// <summary>The file as pelly works on it: BOM stripped, LF line ends.</summary>
    public static string ReadLf(string path) => Lf(File.ReadAllText(path));

    public static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static int CountBareLf(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r')) count++;
        return count;
    }

    public static SqlBatch TargetBatch(string textLf, string objectName)
    {
        var wanted = SqlObjectName.Parse(objectName);
        return Assert.Single(SqlBatchSplitter.Split(textLf, CompatLevel).Batches, b => b.ObjectName is { } n && n.SameAs(wanted));
    }

    /// <summary>
    /// Asserts that the bytes before and after the target batch are identical in both versions, and returns the target
    /// batch text (LF) before and after.
    /// </summary>
    public static (string Before, string After) AssertOnlyTargetChanged(byte[] before, byte[] after, string objectName)
    {
        var beforeText = Encoding.UTF8.GetString(before); // keeps a BOM as U+FEFF and CR characters
        var afterText = Encoding.UTF8.GetString(after);
        var beforeTarget = TargetBatch(Lf(beforeText.TrimStart('﻿')), objectName);
        var afterTarget = TargetBatch(Lf(afterText.TrimStart('﻿')), objectName);

        var beforeLines = beforeText.Split('\n');
        var afterLines = afterText.Split('\n');
        var headCount = beforeTarget.StartLine - 1;
        var tailCount = beforeLines.Length - beforeTarget.EndLine;
        Assert.Equal(headCount, afterTarget.StartLine - 1);
        Assert.Equal(tailCount, afterLines.Length - afterTarget.EndLine);

        var head = Encoding.UTF8.GetByteCount(string.Concat(beforeLines[..headCount].Select(l => l + "\n")));
        var tail = Encoding.UTF8.GetByteCount(string.Concat(beforeLines[^tailCount..].Select(l => "\n" + l)));
        Assert.True(after.Length >= head + tail, "수정 후 파일이 대상 밖의 바이트보다 짧습니다");
        Assert.True(before.AsSpan(0, head).SequenceEqual(after.AsSpan(0, head)), "대상 배치 앞의 바이트가 바뀌었습니다");
        Assert.True(before.AsSpan(before.Length - tail).SequenceEqual(after.AsSpan(after.Length - tail)), "대상 배치 뒤의 바이트가 바뀌었습니다");
        Assert.NotEqual(beforeTarget.Text, afterTarget.Text);
        return (beforeTarget.Text, afterTarget.Text);
    }

    // ---- model responses ----

    /// <summary>Applies a code response (text after the prefilled SEARCH marker) with pelly's own parser and patcher.</summary>
    public static string Patch(string originalLf, string response)
    {
        var patched = TryPatch(originalLf, response);
        Assert.True(patched is not null, "응답을 원본에 적용할 수 없습니다:\n" + response);
        return patched!;
    }

    private static string? TryPatch(string originalLf, string response)
    {
        var parsed = SearchReplaceParser.Parse("<<<<<<< SEARCH\n" + response);
        if (parsed.Failure is not null) return null;
        var whole = new EditableRegion(new TextSpanInfo(1, originalLf.Split('\n').Length), "test");
        var outcome = SearchReplacePatcher.Apply(originalLf, parsed.Blocks, [whole]);
        return outcome.Success ? outcome.NewText : null;
    }

    /// <summary>
    /// Inserts <paramref name="statement"/> as a new line of the first REPLACE block, with its neighbour's indentation.
    /// First choice is just before the block's last line (as M2a's R7); when that does not leave a target batch that
    /// pelly's safety guard accepts (one statement, parses, statement inside the object), earlier places in the block are
    /// tried, then just after its last line.
    /// </summary>
    public static string InjectStatement(string response, string originalLf, string objectName, string statement)
    {
        var lines = response.Split('\n').ToList();
        var separator = lines.FindIndex(l => l.TrimEnd('\r') == "=======");
        var end = separator < 0 ? -1 : lines.FindIndex(separator + 1, l => l.TrimEnd('\r').StartsWith(">>>>>>> REPLACE", StringComparison.Ordinal));
        Assert.True(end > separator + 1, "첫 REPLACE 블록이 없거나 비어 있습니다:\n" + response);

        var script = SqlBatchSplitter.Split(originalLf, CompatLevel);
        var wanted = SqlObjectName.Parse(objectName);
        var targetIndex = Assert.Single(script.Batches, b => b.ObjectName is { } n && n.SameAs(wanted)).Index;

        var positions = Enumerable.Range(separator + 1, end - separator - 1).Reverse().Append(end);
        foreach (var at in positions)
        {
            var neighbour = lines[Math.Min(at, end - 1)];
            var indent = neighbour[..(neighbour.Length - neighbour.TrimStart().Length)];
            var cr = neighbour.EndsWith('\r') ? "\r" : "";
            var candidate = new List<string>(lines);
            candidate.Insert(at, indent + statement + cr);
            var text = string.Join('\n', candidate);

            var patched = TryPatch(originalLf, text);
            if (patched is null) continue;
            if (!SqlSafetyGuard.Check(script, targetIndex, patched, CompatLevel).Ok) continue;
            if (!SqlBatchSplitter.Split(patched, CompatLevel).Batches[targetIndex].Text.Contains(statement, StringComparison.Ordinal)) continue;
            return text;
        }
        Assert.Fail("문장을 넣을 수 있는 자리가 없습니다 (모델 응답이 원본에 적용되지 않거나 안전 검사를 통과하지 못함):\n" + response);
        return response;
    }
}
