using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// Real-model runs that force the M2a paths M1's E2E never reached: a retry with THINK after a SEARCH mismatch, build
/// feedback after a compile error, and target lookup by line, by an identifier in the request and by a model pick.
/// The retry tests corrupt the first code response with <see cref="CorruptingBackend"/>, so a run that succeeds on
/// attempt 1 means the decorator did not fire and fails the test.
/// </summary>
public sealed class BuildEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private const string Request = "배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘";
    private const string NoSuchLine = "            // pelly-e2e: this line does not exist in the file";
    private const string UndefinedStatement = "undefinedVar++;";

    private static readonly System.Text.RegularExpressions.Regex DeliveredThrow =
        new(@"OrderStatus\.Delivered[^{;]*\)\s*\{?\s*throw\s+new\s+InvalidOperationException", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var dir in _temps)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* temp folder, best effort */ }
        }
    }

    [OllamaFact]
    public async Task FirstAttemptMismatch_RetryUsesThink_Succeeds()
    {
        var copy = await ModernCopyAsync();
        CorruptingBackend? corrupting = null;
        var run = await RunAsync(copy, new RunRequest(copy.File, null, "CancelAsync", null, Request, Yes: true, DryRun: false, MaxRetries: null),
            inner => corrupting = new CorruptingBackend(inner, (n, req, text) =>
                IsFirstCodeRequest(corrupting!, n, req) ? ReplaceFirstSearchLine(text) : text));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(1, corrupting!.RewrittenCount);
        Assert.Equal(2, run.Summary.GetProperty("attempts").GetInt32());
        Assert.Equal("Retry", run.Summary.GetProperty("thinkMode").GetString());
        Assert.Contains("SEARCH NOT FOUND", run.AttemptFile(2, "prompt.txt"), StringComparison.Ordinal);

        // Attempt 1 has no THINK stage (retry mode); attempt 2 sends exactly one, before its code request.
        var kinds = corrupting.Requests.Select(Kind).ToList();
        Assert.Equal(["code", "think", "code"], kinds);
        Assert.True(run.HasAttemptFile(2, "think.txt"), "2번째 시도에 think.txt가 없습니다");
        Assert.False(run.HasAttemptFile(1, "think.txt"), "1번째 시도에 think.txt가 있습니다 (retry 모드는 2번째부터)");
        Assert.True(run.Summary.GetProperty("thinkTokens").GetInt32() > 0, "summary의 thinkTokens가 0입니다");

        await AssertShippedGuardAdded(copy);
    }

    [OllamaFact]
    public async Task FirstAttemptBuildError_FeedbackFixes_Succeeds()
    {
        var copy = await ModernCopyAsync();
        CorruptingBackend? corrupting = null;
        var run = await RunAsync(copy, new RunRequest(copy.File, null, "CancelAsync", null, Request, Yes: true, DryRun: false, MaxRetries: null),
            inner => corrupting = new CorruptingBackend(inner, (n, req, text) =>
                IsFirstCodeRequest(corrupting!, n, req) ? InjectUndefinedVar(text, CsEndToEndTests.Lf(copy.Before)) : text));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(1, corrupting!.RewrittenCount);
        Assert.True(run.Summary.GetProperty("attempts").GetInt32() >= 2, "재시도 없이 끝났습니다 — 손상된 첫 응답이 빌드를 통과했습니다");
        var codes = run.Summary.GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("CS0103", codes);
        Assert.Contains("BUILD FAILED", run.AttemptFile(2, "prompt.txt"), StringComparison.Ordinal);
        Assert.Contains("undefinedVar", run.AttemptFile(2, "prompt.txt"), StringComparison.Ordinal);

        var after = File.ReadAllText(copy.File);
        Assert.DoesNotContain("undefinedVar", after, StringComparison.Ordinal);
        await AssertShippedGuardAdded(copy);
    }

    [OllamaFact]
    public async Task LineTarget_Works()
    {
        var copy = await ModernCopyAsync();
        var line = LineOf(copy.Before, "if (order.Status == OrderStatus.Cancelled)"); // inside CancelAsync
        var run = await RunAsync(copy, new RunRequest(copy.File, null, null, line, Request, Yes: true, DryRun: false, MaxRetries: null));

        Assert.Equal(ExitCodes.Success, run.Exit);
        await AssertShippedGuardAdded(copy);
    }

    [OllamaFact]
    public async Task NoMethodNoLine_RequestIdentifier_Works()
    {
        var copy = await ModernCopyAsync();
        CorruptingBackend? recording = null;
        var run = await RunAsync(copy,
            new RunRequest(copy.File, null, null, null, "`CancelAsync`에서 " + Request, Yes: true, DryRun: false, MaxRetries: null),
            inner => recording = new CorruptingBackend(inner, (_, text) => text));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.DoesNotContain(recording!.Requests, IsPickRequest); // the identifier decides; the model is not asked
        Assert.DoesNotContain(run.Ui.Lines, l => l.Contains("모델이 선택", StringComparison.Ordinal));
        await AssertShippedGuardAdded(copy);
    }

    [OllamaFact]
    public async Task NoMethodNoLine_ModelPick_Works()
    {
        var copy = await ModernCopyAsync();
        CorruptingBackend? recording = null;
        var run = await RunAsync(copy,
            new RunRequest(copy.File, null, null, null, "주문을 취소할 때 배송 중인 주문이면 예외를 던져서 취소하지 못하게 해줘", Yes: true, DryRun: false, MaxRetries: null),
            inner => recording = new CorruptingBackend(inner, (_, text) => text));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Single(recording!.Requests, IsPickRequest);
        Assert.Contains("warn: 대상: OrderService.CancelAsync(int, string) (모델이 선택)", run.Ui.Lines);
        Assert.Contains("대상: OrderService.CancelAsync(int, string) (모델이 선택)", run.Messages, StringComparison.Ordinal);
        await AssertShippedGuardAdded(copy);
    }

    /// <summary>
    /// The request's meaning: CancelAsync changed and nothing else did, it still parses, the existing Delivered guard still
    /// throws (alone or or-ed with Shipped, in either order), and — by behaviour, not by a regex (M3a T14 ruling) — the corpus
    /// test pinning the old behaviour now fails with InvalidOperationException while the other corpus tests pass
    /// (<see cref="CsEndToEndTests.AssertShippedOrdersNoLongerCancelledAsync"/>). The earlier Shipped regex accepted only a
    /// condition ending in <c>OrderStatus.Shipped)</c> and rejected a correct <c>Shipped || Delivered</c> guard (2026-10-18);
    /// the Delivered check stays because no corpus test covers it (it caught the Shipped guard replacing the Delivered guard).
    /// </summary>
    private async Task AssertShippedGuardAdded(ModernCopy copy)
    {
        var after = File.ReadAllText(copy.File);
        var (beforeTarget, beforeRest) = CsEndToEndTests.Split(CsEndToEndTests.Lf(copy.Before), LanguageVersion.Latest, "CancelAsync");
        var (afterTarget, afterRest) = CsEndToEndTests.Split(CsEndToEndTests.Lf(after), LanguageVersion.Latest, "CancelAsync");
        Assert.NotEqual(beforeTarget, afterTarget);
        Assert.Equal(beforeRest, afterRest);
        Assert.Matches(DeliveredThrow, afterTarget);
        var outcome = SyntaxVerifier.Verify(CsEndToEndTests.Lf(after), LanguageVersion.Latest, "OrderService.cs");
        Assert.True(outcome.Passed, string.Join("\n", outcome.NewErrors.Select(e => $"{e.Line}:{e.Column} {e.Code} {e.Message}")));
        await CsEndToEndTests.AssertShippedOrdersNoLongerCancelledAsync(copy.Root, output);
    }

    // ---- request classification (R6: inspect the request, not a fixed index) ----

    private static bool IsCodeRequest(LlmRequest r) => r.RawPrompt.EndsWith("<<<<<<< SEARCH\n", StringComparison.Ordinal);

    private static bool IsThinkRequest(LlmRequest r) => r.Stop.Contains("</think>");

    private static bool IsPickRequest(LlmRequest r) => r.RawPrompt.Contains("### MEMBERS\n", StringComparison.Ordinal);

    private static string Kind(LlmRequest r) => IsCodeRequest(r) ? "code" : IsThinkRequest(r) ? "think" : IsPickRequest(r) ? "pick" : "other";

    /// <summary>True for the first code-generation request of the run (the requests before request n are already recorded).</summary>
    private static bool IsFirstCodeRequest(CorruptingBackend backend, int n, LlmRequest request) =>
        IsCodeRequest(request) && !backend.Requests.Take(n - 1).Any(IsCodeRequest);

    // ---- response corruption ----

    /// <summary>The code response starts right after the prefilled SEARCH marker, so its first line is the first SEARCH line.</summary>
    internal static string ReplaceFirstSearchLine(string text)
    {
        var nl = text.IndexOf('\n');
        Assert.True(nl > 0, "코드 응답에 SEARCH 줄이 없습니다:\n" + text);
        Assert.False(text[..nl].TrimEnd('\r').StartsWith("=======", StringComparison.Ordinal), "SEARCH가 비어 있습니다:\n" + text);
        return NoSuchLine + text[nl..];
    }

    /// <summary>
    /// R7: inserts <c>undefinedVar++;</c> as a new line just before the last line of the first REPLACE block, with that
    /// line's indentation. When that does not leave it as a statement inside CancelAsync's body (the REPLACE ends in an
    /// expression, in an added helper, or past the method), the fallback inserts it before the method's last <c>}</c>
    /// instead: one block replacing the whole method with the model's edited version plus the statement.
    /// </summary>
    internal static string InjectUndefinedVar(string response, string originalLf)
    {
        var primary = InsertBeforeLastReplaceLine(response);
        if (TryPatch(originalLf, primary) is null || InjectedInsideCancelAsyncBody(originalLf, primary)) return primary;

        var patched = TryPatch(originalLf, response);
        Assert.True(patched is not null, "모델 응답이 원본에 적용되지 않아 대체 삽입을 할 수 없습니다:\n" + response);
        var span = Assert.Single(MemberLocator.FindByName(originalLf, LanguageVersion.Latest, null, "CancelAsync")).Span;
        var orig = originalLf.Split('\n');
        var edited = patched!.Split('\n');
        var head = span.StartLine - 1;              // lines before the method
        var tail = orig.Length - span.EndLine;      // lines after the method
        Assert.True(orig[..head].SequenceEqual(edited[..head]) && orig[^tail..].SequenceEqual(edited[^tail..]),
            "모델 응답이 CancelAsync 밖의 줄을 바꿔 대체 삽입을 할 수 없습니다:\n" + response);

        var method = CSharpSyntaxTree.ParseText(patched).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "CancelAsync");
        var close = method.Body!.CloseBraceToken.GetLocation().GetLineSpan().StartLinePosition.Line; // 0-based
        var region = edited[head..(edited.Length - tail)].ToList();
        var braceLine = region[close - head];
        region.Insert(close - head, braceLine[..(braceLine.Length - braceLine.TrimStart().Length)] + "    " + UndefinedStatement);
        return string.Join('\n', orig[head..(orig.Length - tail)]) + "\n=======\n" + string.Join('\n', region) + "\n>>>>>>> REPLACE";
    }

    private static string InsertBeforeLastReplaceLine(string text)
    {
        var lines = text.Split('\n').ToList();
        var separator = lines.FindIndex(l => l.TrimEnd('\r') == "=======");
        var end = separator < 0 ? -1 : lines.FindIndex(separator + 1, l => l.TrimEnd('\r').StartsWith(">>>>>>> REPLACE", StringComparison.Ordinal));
        Assert.True(end > separator + 1, "첫 REPLACE 블록이 없거나 비어 있습니다:\n" + text);

        var last = lines[end - 1];
        var indent = last[..(last.Length - last.TrimStart().Length)];
        var cr = last.EndsWith('\r') ? "\r" : "";
        lines.Insert(end - 1, indent + UndefinedStatement + cr);
        return string.Join('\n', lines);
    }

    /// <summary>True when the patched file parses and <c>undefinedVar++;</c> is a statement inside CancelAsync's body.</summary>
    internal static bool InjectedInsideCancelAsyncBody(string originalLf, string response)
    {
        var tree = CSharpSyntaxTree.ParseText(Patch(originalLf, response));
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return false;
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "CancelAsync");
        return method.Body!.DescendantNodes().OfType<ExpressionStatementSyntax>().Any(st => st.ToString() == UndefinedStatement);
    }

    /// <summary>Applies a code response (text after the prefilled SEARCH marker) with pelly's own parser and patcher.</summary>
    internal static string Patch(string originalLf, string response)
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

    // ---- run plumbing ----

    private sealed record ModernCopy(string Root, string File, string Before);

    private sealed record E2ERun(int Exit, RecordingUserInteraction Ui, string RunDir, JsonElement Summary, string Messages)
    {
        public bool HasAttemptFile(int attempt, string name) => System.IO.File.Exists(Path.Combine(RunDir, $"attempt-{attempt}", name));

        public string AttemptFile(int attempt, string name) => System.IO.File.ReadAllText(Path.Combine(RunDir, $"attempt-{attempt}", name));
    }

    private async Task<ModernCopy> ModernCopyAsync()
    {
        // Link-free root: the behaviour oracle builds and runs the corpus test project (TestRunnerSdkTests explains the /var link).
        var temp = Path.Combine(Path.GetTempPath(), "pelly-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var root = CsEndToEndTests.RealPath(temp);
        _temps.Add(root);
        CsEndToEndTests.CopyDirectory(CorpusPaths.Get("Modern"), root);
        await CsEndToEndTests.RestoreAsync(Path.Combine(root, "Modern.sln")); // pelly builds with --no-restore
        var file = Path.Combine(root, "Shop.Api", "Services", "OrderService.cs");
        return new ModernCopy(root, file, File.ReadAllText(file));
    }

    private async Task<E2ERun> RunAsync(ModernCopy copy, RunRequest request, Func<ILlmBackend, ILlmBackend>? wrap = null,
        [CallerMemberName] string test = "")
    {
        var fs = new FileSystem();
        var overrides = new Dictionary<string, string?>
        {
            ["llm:endpoint"] = TestEnv.OllamaEndpoint,
            ["llm:model"] = TestEnv.OllamaModel,
        };
        var loaded = PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), overrides);
        var options = Assert.IsType<PellyOptions>(loaded.Options);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        ILlmBackend backend = new OllamaBackend(http, options.Llm, TimeProvider.System);
        if (wrap is not null) backend = wrap(backend);
        var ui = new RecordingUserInteraction();
        var orchestrator = FsmOrchestratorFactory.Create(fs, backend, ui, options, TimeProvider.System, new SystemProcessRunner(TimeProvider.System));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var exit = await orchestrator.RunAsync(request, cts.Token);

        foreach (var line in ui.Lines) output.WriteLine(line);
        var runs = Path.Combine(copy.Root, ".pelly", "runs");
        var runDir = Assert.Single(Directory.GetDirectories(runs));
        var summaryText = File.ReadAllText(Path.Combine(runDir, "summary.json"));
        output.WriteLine(summaryText);
        var messagesPath = Path.Combine(runDir, "messages.txt");
        var messages = File.Exists(messagesPath) ? File.ReadAllText(messagesPath) : "";

        var keep = Environment.GetEnvironmentVariable("PELLY_TEST_KEEP");
        if (!string.IsNullOrEmpty(keep))
        {
            var dest = Path.Combine(keep, test + "-" + Path.GetFileName(copy.Root));
            CsEndToEndTests.CopyDirectory(runs, dest);
            output.WriteLine($"runs kept at {dest}");
        }

        using var doc = JsonDocument.Parse(summaryText);
        return new E2ERun(exit, ui, runDir, doc.RootElement.Clone(), messages);
    }

    private static int LineOf(string text, string needle)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains(needle, StringComparison.Ordinal));
        Assert.True(index >= 0, $"'{needle}' 줄이 없습니다");
        return index + 1;
    }
}
