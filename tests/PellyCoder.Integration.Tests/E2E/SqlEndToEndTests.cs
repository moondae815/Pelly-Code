using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// Real-model runs of <c>pelly sql</c> against copies of the SQL corpus and the real corpus database (local only: they
/// need Ollama and SQL Server). Think mode stays at its default (<c>retry</c>). The forced-retry test corrupts the first
/// code response with <see cref="CorruptingBackend"/>, so a run that ends on attempt 1 fails the test.
/// </summary>
public sealed class SqlEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly SqlCorpusCopy _copy = SqlCorpusCopy.Create();

    public void Dispose() => _copy.Dispose();

    [OllamaSqlFact]
    public Task Sql001_RealModel_Exit0() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        var file = _copy.File("usp_CancelOrder.sql");
        var before = File.ReadAllBytes(file);

        var run = await RunAsync(file, SqlE2E.Sql001Request);

        Assert.Equal(ExitCodes.Success, run.Exit);
        AssertShippedGuardAdded(before, File.ReadAllBytes(file));
    });

    [OllamaSqlFact]
    public Task FirstAttemptMissingColumn_FeedbackFixes() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        var file = _copy.File("usp_CancelOrder.sql");
        var before = File.ReadAllBytes(file);
        var originalLf = SqlE2E.ReadLf(file);
        CorruptingBackend? corrupting = null;

        var run = await RunAsync(file, SqlE2E.Sql001Request, wrap: inner => corrupting = new CorruptingBackend(inner, (n, req, text) =>
            IsFirstCodeRequest(corrupting!, n, req)
                ? SqlE2E.InjectStatement(text, originalLf, "dbo.usp_CancelOrder", SqlE2E.MissingColumnStatement)
                : text));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(1, corrupting!.RewrittenCount);
        Assert.True(run.Attempts >= 2, "재시도 없이 끝났습니다 — 손상된 첫 응답이 검증을 통과했습니다");
        Assert.Contains("Msg207", run.ErrorCodes);
        var prompt2 = run.AttemptFile(2, "prompt.txt");
        Assert.Contains("SQL CHECK FAILED", prompt2, StringComparison.Ordinal);
        Assert.Contains("NoSuchColumn", prompt2, StringComparison.Ordinal);

        // Default think mode: attempt 1 has no THINK, attempt 2 starts with one.
        Assert.Equal("Retry", run.Summary!.Value.GetProperty("thinkMode").GetString());
        Assert.Equal(["code", "think", "code"], corrupting.Requests.Take(3).Select(Kind));
        Assert.True(run.HasAttemptFile(2, "think.txt"), "2번째 시도에 think.txt가 없습니다");

        var after = File.ReadAllBytes(file);
        Assert.DoesNotContain("NoSuchColumn", Encoding.UTF8.GetString(after), StringComparison.Ordinal);
        AssertShippedGuardAdded(before, after);
    });

    [OllamaSqlFact]
    public Task MultiObjectFile_ObjectOption_Exit0() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        var file = _copy.File("Settlement_All.sql");
        var before = File.ReadAllBytes(file);

        var run = await RunAsync(file, SqlE2E.Sql004Request, objectName: "dbo.usp_SettleMonthly");

        Assert.Equal(ExitCodes.Success, run.Exit);
        var after = File.ReadAllBytes(file);
        var (_, target) = SqlE2E.AssertOnlyTargetChanged(before, after, "dbo.usp_SettleMonthly");
        Assert.Contains("SettlementDeferrals", target, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"SUM\s*\(\s*(\w+\.)?Amount\s*\)", RegexOptions.IgnoreCase), target);
    });

    [OllamaSqlFact]
    public Task CliRun_DryRun_ShowsDiff() => SqlE2E.WithUnchangedDatabase(async () =>
    {
        var file = _copy.File("usp_CancelOrder.sql");
        var before = File.ReadAllBytes(file);
        File.WriteAllText(Path.Combine(_copy.Root, "pelly.json"),
            """{ "sql": { "allowedHosts": ["localhost"], "allowedDatabases": ["PellyCorpus"], "compatLevel": 160 } }""");

        var result = await new SystemProcessRunner(TimeProvider.System).RunAsync(
            new ProcessSpec("dotnet",
                [CliDll(), "sql", file, SqlE2E.Sql001Request, "--dry-run", "--yes",
                 "--model", TestEnv.OllamaModel!, "--endpoint", TestEnv.OllamaEndpoint!],
                _copy.Root, TimeSpan.FromMinutes(10),
                new Dictionary<string, string> { ["PELLY_SQL_CONNECTION"] = TestEnv.SqlConnection! }),
            TestContext.Current.CancellationToken);
        output.WriteLine($"exit {result.ExitCode}");
        output.WriteLine(result.StdOut);
        output.WriteLine(result.StdErr);
        var runs = Path.Combine(_copy.Root, ".pelly", "runs");
        foreach (var summary in Directory.Exists(runs) ? Directory.GetFiles(runs, "summary.json", SearchOption.AllDirectories) : [])
            output.WriteLine(File.ReadAllText(summary));
        var keep = Environment.GetEnvironmentVariable("PELLY_TEST_KEEP");
        if (!string.IsNullOrEmpty(keep))
        {
            var dest = Path.Combine(keep, nameof(CliRun_DryRun_ShowsDiff) + "-" + Path.GetFileName(_copy.Root));
            if (Directory.Exists(runs)) CsEndToEndTests.CopyDirectory(runs, dest);
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(dest, "cli-output.txt"), $"exit {result.ExitCode}\n--- stdout\n{result.StdOut}\n--- stderr\n{result.StdErr}");
        }

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Matches(new Regex(@"@@ -\d+,\d+ \+\d+,\d+ @@"), result.StdOut);
        Assert.Matches(new Regex(@"(?m)^.*\+.*@Status.*\b2\b"), result.StdOut);
        Assert.Equal(before, File.ReadAllBytes(file));
    });

    // ---- judging ----

    private static readonly Regex Status2Raise = new(
        @"@Status\s*(=\s*2\b|IN\s*\([^)]*\b2\b[^)]*\))[\s\S]{0,120}?\b(RAISERROR|THROW)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Status3Raise = new(
        @"@Status\s*(=\s*3\b|IN\s*\([^)]*\b3\b[^)]*\))[\s\S]{0,120}?\b(RAISERROR|THROW)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// sql-001 by meaning: only usp_CancelOrder's batch changed (other bytes identical), it now raises under a
    /// <c>@Status = 2</c> / <c>@Status IN (…2…)</c> condition, and the delivered (3) guard still raises. Compilation is
    /// judged by pelly's own VERIFY (exit 0).
    /// </summary>
    private static void AssertShippedGuardAdded(byte[] before, byte[] after)
    {
        var (beforeTarget, afterTarget) = SqlE2E.AssertOnlyTargetChanged(before, after, "dbo.usp_CancelOrder");
        Assert.DoesNotMatch(Status2Raise, beforeTarget);
        Assert.Matches(Status2Raise, afterTarget);
        Assert.Matches(Status3Raise, afterTarget);
    }

    // ---- request classification (inspect the request, not a fixed index) ----

    private static bool IsCodeRequest(LlmRequest r) => r.RawPrompt.EndsWith("<<<<<<< SEARCH\n", StringComparison.Ordinal);

    private static bool IsThinkRequest(LlmRequest r) => r.Stop.Contains("</think>");

    private static string Kind(LlmRequest r) => IsCodeRequest(r) ? "code" : IsThinkRequest(r) ? "think" : "other";

    private static bool IsFirstCodeRequest(CorruptingBackend backend, int n, LlmRequest request) =>
        IsCodeRequest(request) && !backend.Requests.Take(n - 1).Any(IsCodeRequest);

    // ---- run plumbing ----

    private async Task<SqlRun> RunAsync(string file, string request, string? objectName = null,
        Func<ILlmBackend, ILlmBackend>? wrap = null, [CallerMemberName] string test = "")
    {
        var overrides = new Dictionary<string, string?>
        {
            ["llm:endpoint"] = TestEnv.OllamaEndpoint,
            ["llm:model"] = TestEnv.OllamaModel,
        };
        var loaded = PellyOptionsLoader.Load(new FileSystem(), null, null, new Dictionary<string, string?>(), overrides);
        var options = Assert.IsType<PellyOptions>(loaded.Options);
        SqlE2E.ConfigureSql(options);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        ILlmBackend backend = new OllamaBackend(http, options.Llm, TimeProvider.System);
        if (wrap is not null) backend = wrap(backend);
        var runRequest = new RunRequest(file, null, null, null, request, Yes: true, DryRun: false, MaxRetries: null,
            ObjectName: objectName, Kind: EditKind.Sql);
        return await SqlE2E.RunAsync(_copy.Root, options, backend, runRequest, output, test);
    }

    /// <summary>
    /// The pelly CLI built next to this test assembly's configuration (the project reference with
    /// <c>ReferenceOutputAssembly=false</c> makes the test build build it first, so it is never stale).
    /// </summary>
    private static string CliDll()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tfm = baseDir.Name;
        var configuration = baseDir.Parent!.Name;
        var repo = Directory.GetParent(CorpusPaths.Root)!.Parent!.FullName;
        var dll = Path.Combine(repo, "src", "PellyCoder.Cli", "bin", configuration, tfm, "pelly.dll");
        Assert.True(File.Exists(dll), $"pelly CLI가 빌드되어 있지 않습니다: {dll}");
        return dll;
    }
}
