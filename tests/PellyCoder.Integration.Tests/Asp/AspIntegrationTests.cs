using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Verification;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.E2E;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Asp;

/// <summary>
/// The whole <c>pelly asp</c> pipeline (<see cref="FsmOrchestratorFactory.CreateAsp"/>) on temp copies of eval/corpus/Asp
/// with canned model responses (<see cref="CannedLlmBackend"/>, think off) and the real file system. cscript runs only
/// on Windows; SQL is not configured, so the stored procedure check is skipped.
/// </summary>
public sealed class AspIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly AspCorpusCopy _copy = AspCorpusCopy.Create();

    static AspIntegrationTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public void Dispose() => _copy.Dispose();

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    // ---- list.asp: search conditions built by string concatenation ----

    private const string SearchEnd = "' 검색 조건 끝";

    [Fact]
    public async Task ListPage_AddStatusFilter_Approved_OutsideBytesIdentical()
    {
        var file = _copy.File("admin/order/list.asp");
        var before = File.ReadAllBytes(file);
        var lines = AspE2E.SpanBetween(before, AspE2E.Cp949, "' 검색 조건", SearchEnd);
        var filter = "If status <> \"\" Then\n    sql = sql & \", @Status = '\" & SafeSQL(status) & \"'\"\nEnd If";
        var backend = new CannedLlmBackend([Block(SearchEnd, filter + "\n" + SearchEnd)]);

        var run = await RunAsync(file, backend, "상태(status) 검색 조건을 추가해줘", lines: lines);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(1, run.Attempts);
        var after = File.ReadAllBytes(file);
        var changed = AspE2E.AssertOutsideBytesIdentical(before, after, lines);
        Assert.Contains("SafeSQL(status)", AspE2E.Cp949.GetString(changed), StringComparison.Ordinal);
        var asp = run.Asp;
        Assert.Equal("Lines", asp.GetProperty("unit").GetString());
        Assert.Equal("CP949", asp.GetProperty("encoding").GetString());
        Assert.Equal(4, asp.GetProperty("includesFound").GetInt32());
        Assert.Equal(0, asp.GetProperty("includesMissing").GetInt32());
        var checks = asp.GetProperty("checks");
        Assert.Equal("passed", checks.GetProperty("Structure").GetString());
        Assert.Equal("passed", checks.GetProperty("Safety").GetString());
        Assert.Equal("passed", checks.GetProperty("AspNames").GetString());
    }

    [Fact]
    public async Task RequestConcat_Exit5_Restored()
    {
        var file = _copy.File("admin/order/list.asp");
        var before = File.ReadAllBytes(file);
        var lines = AspE2E.SpanBetween(before, AspE2E.Cp949, "' 검색 조건", SearchEnd);
        var concat = Block(SearchEnd, "If Request(\"q\") <> \"\" Then sql = sql & \", @Status = \" & Request(\"q\")\n" + SearchEnd);
        var backend = new CannedLlmBackend([concat, concat, concat, concat]);

        var run = await RunAsync(file, backend, "q 값으로 상태 검색 조건을 추가해줘", lines: lines);

        Assert.Equal(ExitCodes.SafetyRejected, run.Exit);
        Assert.Equal(4, backend.Requests.Count);
        Assert.Equal(FailureKinds.AspSafetyRejected, run.FailureKind);
        Assert.Contains("SqlConcatRequest", backend.Requests[1].RawPrompt, StringComparison.Ordinal);
        Assert.Contains("SafeSQL", backend.Requests[1].RawPrompt, StringComparison.Ordinal);
        Assert.Equal("failed", run.Asp.GetProperty("checks").GetProperty("Safety").GetString());
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task InventedFunction_ThenFixed_Exit0()
    {
        const string Amount = "  <td align=\"right\"><%= FormatNumber(rowAmount, 0) %>원</td>";
        const string Invented = "  <td align=\"right\"><%= FormatWon(rowAmount) %></td>";
        const string Fixed = "  <td align=\"right\"><%= FormatNumber(rowAmount, 0) %> 원</td>";
        var file = _copy.File("admin/order/list.asp");
        var before = File.ReadAllBytes(file);
        var lines = AspE2E.SpanBetween(before, AspE2E.Cp949, Amount, Amount);
        var backend = new CannedLlmBackend([Block(Amount, Invented), Block(Invented, Fixed)]);

        var run = await RunAsync(file, backend, "주문 금액과 '원' 사이를 한 칸 띄워줘", lines: lines);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(2, run.Attempts);
        var feedback = AspE2E.Feedback(backend.Requests[1].RawPrompt);
        Assert.Contains("ASP UNKNOWN NAMES", feedback, StringComparison.Ordinal);
        Assert.Contains("FormatWon", feedback, StringComparison.Ordinal);
        var after = File.ReadAllBytes(file);
        var changed = AspE2E.AssertOutsideBytesIdentical(before, after, lines);
        Assert.Equal(Fixed + "\r\n", AspE2E.Cp949.GetString(changed));
    }

    // ---- save.asp: pure ASCII, mixed line ends, no final line feed ----

    [Fact]
    public async Task AsciiMixedEndingsFile_Approved_OutsideBytesIdentical()
    {
        const string Alert = "Response.Write \"<script type=\"\"text/javascript\"\">alert('Order cancelled.');location.href='list.asp';</script>\"";
        var korean = Alert.Replace("Order cancelled.", "주문을 취소했습니다.", StringComparison.Ordinal);
        var file = _copy.File("admin/order/save.asp");
        var before = File.ReadAllBytes(file);
        Assert.All(before, b => Assert.True(b < 0x80, "save.asp는 순수 ASCII여야 합니다"));
        var lines = AspE2E.SpanBetween(before, AspE2E.Cp949, Alert, Alert);
        var backend = new CannedLlmBackend([Block(Alert, korean)]);

        var run = await RunAsync(file, backend, "취소 완료 메시지를 한글로 바꿔줘", lines: lines);

        Assert.Equal(ExitCodes.Success, run.Exit);
        var after = File.ReadAllBytes(file);
        var changed = AspE2E.AssertOutsideBytesIdentical(before, after, lines);
        Assert.Equal(AspE2E.Cp949.GetBytes(korean + "\r\n"), changed);
        Assert.Equal("CP949", run.Asp.GetProperty("encoding").GetString());
        Assert.Equal((byte)'>', after[^1]); // still no final line feed
        Assert.Equal(AspE2E.LineEnds(before), AspE2E.LineEnds(after));
    }

    // ---- func_order.inc: procedure target ----

    private const string AmountReturn = "    GetOrderAmount = amount";

    [Fact]
    public async Task ProcTarget_GetOrderAmount_Approved()
    {
        var file = _copy.File("inc/func_order.inc");
        var before = File.ReadAllBytes(file);
        var lines = AspE2E.SpanBetween(before, AspE2E.Cp949, "Function GetOrderAmount(", "End Function");
        var backend = new CannedLlmBackend([Block(AmountReturn, "    GetOrderAmount = Fix(amount)")]);

        var run = await RunAsync(file, backend, "결제 금액의 원 미만을 버려줘", proc: "GetOrderAmount");

        Assert.Equal(ExitCodes.Success, run.Exit);
        var after = File.ReadAllBytes(file);
        var changed = AspE2E.Cp949.GetString(AspE2E.AssertOutsideBytesIdentical(before, after, lines));
        Assert.Contains("GetOrderAmount = Fix(amount)\r\n", changed, StringComparison.Ordinal);
        Assert.Contains("' 결제 금액", AspE2E.Cp949.GetString(after), StringComparison.Ordinal);
        Assert.Equal("Procedure", run.Asp.GetProperty("unit").GetString());
        Assert.Equal("passed", run.Asp.GetProperty("checks").GetProperty("Structure").GetString());
    }

    // ---- list_excel.asp: CodePage=65001 ----

    [Fact]
    public async Task Utf8CodePageFile_KoreanKeptUtf8()
    {
        const string Joined = "  <th>가입일</th>";
        var file = _copy.File("admin/member/list_excel.asp");
        var before = File.ReadAllBytes(file);
        var lines = AspE2E.SpanBetween(before, Encoding.UTF8, Joined, Joined);
        var backend = new CannedLlmBackend([Block(Joined, "  <th>가입일(년-월-일)</th>")]);

        var run = await RunAsync(file, backend, "가입일 제목에 날짜 형식을 적어줘", lines: lines);

        Assert.Equal(ExitCodes.Success, run.Exit);
        var after = File.ReadAllBytes(file);
        var changed = AspE2E.AssertOutsideBytesIdentical(before, after, lines);
        Assert.Equal(Encoding.UTF8.GetBytes("  <th>가입일(년-월-일)</th>\r\n"), changed);
        Assert.False(after.AsSpan().StartsWith(Encoding.UTF8.Preamble), "BOM이 생겼습니다");
        Assert.Contains("회원 번호", AspE2E.StrictUtf8.GetString(after), StringComparison.Ordinal);
        Assert.Equal("UTF-8", run.Asp.GetProperty("encoding").GetString());
    }

    // ---- Windows: the real cscript ----

    [WindowsFact]
    public async Task CompileError_ThenFixed_Exit0()
    {
        const string Unclosed = "    GetOrderAmount = (amount + 0";
        var file = _copy.File("inc/func_order.inc");
        var backend = new CannedLlmBackend([
            Block(AmountReturn, Unclosed),
            Block(Unclosed, "    GetOrderAmount = Fix(amount)"),
        ]);

        var run = await RunAsync(file, backend, "결제 금액의 원 미만을 버려줘", proc: "GetOrderAmount");

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(2, run.Attempts);
        Assert.Contains("VBS", run.ErrorCodes);
        var feedback = AspE2E.Feedback(backend.Requests[1].RawPrompt);
        Assert.Contains("VBSCRIPT COMPILE FAILED", feedback, StringComparison.Ordinal);
        Assert.Contains("')'", feedback, StringComparison.Ordinal);
        Assert.Equal("passed", run.Asp.GetProperty("checks").GetProperty("AspCompile").GetString());
        Assert.Contains("GetOrderAmount = Fix(amount)", AspE2E.Cp949.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task Corpus_AllFiles_CompileUnderCscript()
    {
        var processes = new SystemProcessRunner(TimeProvider.System);
        var files = AspCorpusCopy.SourceFiles();
        Assert.Equal(18, files.Count); // W3a added admin/order/history.asp, W3b memo.asp and memo_save.asp
        var failures = new List<string>();
        foreach (var path in files)
        {
            var textLf = AspE2E.DecodeLf(File.ReadAllBytes(path));
            var compileScript = VbsCompileScript.Build(AspPage.Parse(textLf), textLf);
            var script = Path.Combine(_copy.Root, Path.GetFileNameWithoutExtension(path) + "-compile.vbs");
            File.WriteAllBytes(script, [.. Encoding.Unicode.GetPreamble(),
                .. Encoding.Unicode.GetBytes(compileScript.Text.ReplaceLineEndings("\r\n"))]);
            var result = await processes.RunAsync(
                new ProcessSpec("cscript", ["//Nologo", "//T:10", "//E:vbscript", script], _copy.Root, TimeSpan.FromSeconds(30)),
                TestContext.Current.CancellationToken);
            var text = TextDecoding.Decode(result.StdOutBytes) + "\n" + TextDecoding.Decode(result.StdErrBytes ?? []);
            var errors = CscriptOutput.Parse(text, script, compileScript);
            output.WriteLine($"{Path.GetRelativePath(CorpusPaths.Get("Asp"), path)}: exit {result.ExitCode}, {errors.Count} errors");
            if (errors.Count > 0 || result.ExitCode != 0)
                failures.Add($"{path} (exit {result.ExitCode}):\n{string.Join("\n", errors)}\n{text}");
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    // ---- the corpus itself (bytes on disk, baselines the tests above rely on) ----

    [Fact]
    public void Corpus_EncodingsAndLineEnds_AsDesigned()
    {
        foreach (var name in new[] { "inc/func_common.inc", "inc/func_order.inc", "admin/order/list.asp", "admin/order/edit.asp" })
        {
            var bytes = File.ReadAllBytes(CorpusPaths.Get("Asp/" + name));
            Assert.Throws<DecoderFallbackException>(() => AspE2E.StrictUtf8.GetString(bytes));
            Assert.Matches("[가-힣]", AspE2E.Cp949.GetString(bytes));
        }

        var save = File.ReadAllBytes(CorpusPaths.Get("Asp/admin/order/save.asp"));
        Assert.All(save, b => Assert.True(b < 0x80));
        var ends = AspE2E.LineEnds(save);
        Assert.Contains("CRLF", ends);
        Assert.Contains("LF", ends);
        Assert.NotEqual((byte)'\n', save[^1]);

        var excel = File.ReadAllBytes(CorpusPaths.Get("Asp/admin/member/list_excel.asp"));
        Assert.False(excel.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Contains("CodePage=65001", AspE2E.StrictUtf8.GetString(excel), StringComparison.Ordinal);
        Assert.Contains("application/vnd.ms-excel", AspE2E.StrictUtf8.GetString(excel), StringComparison.Ordinal);
    }

    [Fact]
    public void Corpus_Baselines_AsDesigned()
    {
        string[] sanitizers = ["SafeSQL"];
        foreach (var path in AspCorpusCopy.SourceFiles())
        {
            var page = AspPage.Parse(AspE2E.DecodeLf(File.ReadAllBytes(path)));
            Assert.DoesNotContain(page.Problems, p => p.Code != "ASP006");
            Assert.False(page.UsesDynamicCode, path);
            var findings = AspSafetyGuard.Findings(page, sanitizers);
            if (path.EndsWith("list.asp", StringComparison.Ordinal))
            {
                // list.asp already has one Request value concatenated into SQL and one unclosed <div>.
                Assert.Equal("SqlConcatRequest", Assert.Single(findings).Rule);
                Assert.Equal("`<div>` 닫는 태그가 모자랍니다", Assert.Single(page.Problems).Message);
            }
            else
            {
                Assert.True(findings.Count == 0, path + ": " + string.Join(", ", findings));
                Assert.True(page.Problems.Count == 0, path + ": " + string.Join(", ", page.Problems));
            }
        }
    }

    private Task<AspRun> RunAsync(string file, ILlmBackend backend, string request, string? proc = null, TextSpanInfo? lines = null,
        [CallerMemberName] string test = "")
    {
        var options = AspE2E.LoadOptions(_copy.Root);
        options.Llm.ThinkMode = ThinkMode.Off;
        var runRequest = new RunRequest(file, null, proc, null, request, Yes: true, DryRun: false, MaxRetries: null,
            Kind: EditKind.Asp, Lines: lines);
        return AspE2E.RunAsync(_copy.Root, options, backend, runRequest, output, test);
    }
}

/// <summary>A temp copy of eval/corpus/Asp (the site root, with its pelly.json) and an empty .git folder marking the workspace root.</summary>
internal sealed class AspCorpusCopy : IDisposable
{
    private AspCorpusCopy(string root) => Root = root;

    public string Root { get; }

    public static AspCorpusCopy Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "pelly-asp-e2e-" + Guid.NewGuid().ToString("N"));
        CsEndToEndTests.CopyDirectory(CorpusPaths.Get("Asp"), root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return new AspCorpusCopy(root);
    }

    /// <summary>Every .asp and .inc file of the committed corpus, sorted.</summary>
    public static IReadOnlyList<string> SourceFiles() =>
        [.. Directory.GetFiles(CorpusPaths.Get("Asp"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".asp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".inc", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];

    public string File(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }
}

internal sealed record AspRun(int Exit, RecordingUserInteraction Ui, string? RunDir, JsonElement? Summary, string Messages)
{
    public int Attempts => Summary?.GetProperty("attempts").GetInt32() ?? 0;

    public string? FailureKind => Summary?.GetProperty("failureKind").GetString();

    public IReadOnlyList<string?> ErrorCodes =>
        Summary is { } s ? [.. s.GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString())] : [];

    public JsonElement Asp => Summary!.Value.GetProperty("asp");
}

/// <summary>Shared plumbing of the canned and real-model ASP runs.</summary>
internal static class AspE2E
{
    /// <summary>Strict CP949 (registers the code pages provider first: property initializers run before a static constructor body).</summary>
    public static Encoding Cp949 { get; } = CreateCp949();

    public static Encoding StrictUtf8 { get; } = new UTF8Encoding(false, throwOnInvalidBytes: true);

    private static Encoding CreateCp949()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    /// <summary>The corpus site's own pelly.json (asp.siteRoot, asp.sqlSanitizers), as the CLI loads it.</summary>
    public static PellyOptions LoadOptions(string root, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var loaded = PellyOptionsLoader.Load(new FileSystem(), null, Path.Combine(root, "pelly.json"),
            new Dictionary<string, string?>(), overrides ?? new Dictionary<string, string?>());
        var options = Assert.IsType<PellyOptions>(loaded.Options);
        Assert.Equal(".", options.Asp.SiteRoot);
        Assert.Equal(["SafeSQL"], options.Asp.SqlSanitizers);
        return options;
    }

    public static async Task<AspRun> RunAsync(string root, PellyOptions options, ILlmBackend backend, RunRequest request,
        ITestOutputHelper output, string test)
    {
        var ui = new RecordingUserInteraction();
        var orchestrator = FsmOrchestratorFactory.CreateAsp(new FileSystem(), backend, ui, options, TimeProvider.System,
            new SystemProcessRunner(TimeProvider.System), sql: null);
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
        return new AspRun(exit, ui, runDir, summary, messages);
    }

    /// <summary>The page text as pelly reads it (CodePage=65001 → UTF-8, else CP949; ASCII reads the same), LF line ends.</summary>
    public static string DecodeLf(byte[] bytes)
    {
        var ascii = Encoding.ASCII.GetString(bytes);
        var encoding = ascii.Contains("CodePage=65001", StringComparison.OrdinalIgnoreCase) ? StrictUtf8 : Cp949;
        return encoding.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>1-based lines from the first line containing <paramref name="first"/> to the next line (from there) containing <paramref name="last"/>.</summary>
    public static TextSpanInfo SpanBetween(byte[] bytes, Encoding encoding, string first, string last)
    {
        var lines = encoding.GetString(bytes).Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains(first, StringComparison.Ordinal));
        Assert.True(start >= 0, $"'{first}' 줄이 없습니다");
        var end = Array.FindIndex(lines, start, l => l.Contains(last, StringComparison.Ordinal));
        Assert.True(end >= 0, $"'{last}' 줄이 없습니다");
        return new TextSpanInfo(start + 1, end + 1);
    }

    /// <summary>
    /// Asserts that every byte before line <c>span.StartLine</c> and after line <c>span.EndLine</c> (with its line end) is
    /// the same in both versions, and returns the bytes in between of <paramref name="after"/>.
    /// </summary>
    public static byte[] AssertOutsideBytesIdentical(byte[] before, byte[] after, TextSpanInfo span)
    {
        var head = LineOffset(before, span.StartLine);
        var tail = before.Length - LineOffset(before, span.EndLine + 1);
        Assert.True(after.Length >= head + tail, "수정 후 파일이 범위 밖의 바이트보다 짧습니다");
        Assert.True(before.AsSpan(0, head).SequenceEqual(after.AsSpan(0, head)), "범위 앞의 바이트가 바뀌었습니다");
        Assert.True(before.AsSpan(before.Length - tail).SequenceEqual(after.AsSpan(after.Length - tail)), "범위 뒤의 바이트가 바뀌었습니다");
        Assert.False(before.SequenceEqual(after), "파일이 바뀌지 않았습니다");
        return after[head..(after.Length - tail)];
    }

    /// <summary>Byte offset where 1-based <paramref name="line"/> starts (the file length past the last line).</summary>
    private static int LineOffset(byte[] bytes, int line)
    {
        var offset = 0;
        for (var n = 1; n < line; n++)
        {
            var lf = Array.IndexOf(bytes, (byte)'\n', offset);
            if (lf < 0) return bytes.Length;
            offset = lf + 1;
        }
        return offset;
    }

    /// <summary>The line end of every line: "CRLF", "LF", or "" for a last line without one.</summary>
    public static IReadOnlyList<string> LineEnds(byte[] bytes)
    {
        var ends = new List<string>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var lf = Array.IndexOf(bytes, (byte)'\n', offset);
            if (lf < 0)
            {
                ends.Add("");
                break;
            }
            ends.Add(lf > 0 && bytes[lf - 1] == '\r' ? "CRLF" : "LF");
            offset = lf + 1;
        }
        return ends;
    }

    /// <summary>The FEEDBACK part of a retry prompt (from its marker to the end).</summary>
    public static string Feedback(string prompt)
    {
        var at = prompt.LastIndexOf("### FEEDBACK", StringComparison.Ordinal);
        Assert.True(at >= 0, "프롬프트에 FEEDBACK이 없습니다:\n" + prompt);
        return prompt[at..];
    }
}
