using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Integration.Tests.Asp;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.E2E;

namespace PellyCoder.Integration.Tests.Web;

/// <summary>
/// The whole <c>pelly js</c> / <c>pelly css</c> pipeline (<see cref="FsmOrchestratorFactory.CreateWeb"/>) on temp copies of
/// eval/corpus/Asp with canned model responses (<see cref="CannedLlmBackend"/>, think off) and the real file system.
/// </summary>
public sealed class WebIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly AspCorpusCopy _copy = AspCorpusCopy.Create();

    public void Dispose() => _copy.Dispose();

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private const string KeywordLine = "    var keyword = $(\"#keyword\").val();";

    // ---- list.js: CP949, CRLF, jQuery 1.8.3 ----

    [Fact]
    public async Task ListJs_AddStatusParam_Approved_OutsideBytesIdentical()
    {
        var file = _copy.File("js/order/list.js");
        var before = File.ReadAllBytes(file);
        var span = WebE2E.FunctionSpan(before, AspE2E.Cp949, "function fnSearch()");
        var backend = new CannedLlmBackend([
            Block(KeywordLine, KeywordLine + "\n    var status = $(\"#status\").val();")
            + "<<<<<<< SEARCH\n" + Block("        keyword: keyword\n    };", "        keyword: keyword,\n        status: status\n    };"),
        ]);

        var run = await RunAsync(file, backend, "상태(status) 값을 검색 조건으로 같이 보내줘", EditKind.Js, function: "fnSearch");

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(1, run.Attempts);
        var after = File.ReadAllBytes(file);
        var changed = AspE2E.Cp949.GetString(AspE2E.AssertOutsideBytesIdentical(before, after, span));
        Assert.Contains("status: status", changed, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", changed.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        var web = run.Web;
        Assert.Equal("function", web.GetProperty("unit").GetString());
        Assert.Equal("CP949", web.GetProperty("encoding").GetString());
        Assert.Equal("passed", web.GetProperty("checks").GetProperty("Syntax").GetString());
        Assert.Equal("1.8.3", web.GetProperty("jquery").GetString());
        Assert.Equal(0, web.GetProperty("levelRejections").GetInt32());
    }

    // ---- language level ----

    [Fact]
    public async Task Es5File_ArrowFunction_ThenFixed_Exit0()
    {
        const string Return = "    return s;";
        const string Arrow = "    var same = function (x) { return x; };\n    var viaArrow = (x) => x;\n    return viaArrow(s);";
        const string Fixed = "    var same = function (x) { return x; };\n    return same(s);";
        var file = _copy.File("js/common.js");
        var backend = new CannedLlmBackend([Block(Return, Arrow), Block(Return, Fixed)]);

        var run = await RunAsync(file, backend, "숫자 문자열을 한 번 더 함수로 거쳐서 돌려줘", EditKind.Js, function: "formatNumber");

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(2, run.Attempts);
        var feedback = AspE2E.Feedback(backend.Requests[1].RawPrompt);
        Assert.Contains("LANGUAGE LEVEL EXCEEDED", feedback, StringComparison.Ordinal);
        Assert.Contains("=>", feedback, StringComparison.Ordinal);
        Assert.Equal(1, run.Web.GetProperty("levelRejections").GetInt32());
        Assert.DoesNotContain("=>", AspE2E.Cp949.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModernFile_ArrowAllowed_Exit0()
    {
        const string Line = "    total += n;";
        var file = _copy.File("js/order/modern.js");
        var before = File.ReadAllBytes(file);
        var span = WebE2E.FunctionSpan(before, Encoding.ASCII, "function addTotal(");
        var backend = new CannedLlmBackend([Block(Line, "    var add = (a, b) => a + b;\n    total = add(total, n);")]);

        var run = await RunAsync(file, backend, "합계를 더하는 부분을 화살표 함수로 바꿔줘", EditKind.Js, function: "addTotal");

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(1, run.Attempts);
        var changed = Encoding.ASCII.GetString(AspE2E.AssertOutsideBytesIdentical(before, File.ReadAllBytes(file), span));
        Assert.Contains("=>", changed, StringComparison.Ordinal);
        Assert.Equal("ES2015", run.Web.GetProperty("originalLevel").GetString());
    }

    // ---- edit.js: ASCII, mixed line ends, no final line end ----

    [Fact]
    public async Task MixedEndingsAsciiJs_KoreanAdded_Cp949_NoticeShown()
    {
        const string Alert = "        alert(\"Enter the name.\");";
        const string Korean = "        alert(\"이름을 입력하십시오.\");";
        var file = _copy.File("js/member/edit.js");
        var before = File.ReadAllBytes(file);
        Assert.All(before, b => Assert.True(b < 0x80, "edit.js는 순수 ASCII여야 합니다"));
        Assert.Contains("CRLF", AspE2E.LineEnds(before));
        Assert.Contains("LF", AspE2E.LineEnds(before));
        Assert.Equal((byte)'}', before[^1]);
        var backend = new CannedLlmBackend([Block(Alert, Korean)]);

        var run = await RunAsync(file, backend, "이름 입력 안내를 한글로 바꿔줘", EditKind.Js, function: "fnSave");

        Assert.Equal(ExitCodes.Success, run.Exit);
        var after = File.ReadAllBytes(file);
        Assert.Equal((byte)'}', after[^1]); // still no final line end
        Assert.Equal(AspE2E.LineEnds(before), AspE2E.LineEnds(after));
        // Only the alert line differs; it is CP949 now, every other line is byte for byte the same.
        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);
        Assert.Equal(oldLines.Count, newLines.Count);
        for (var i = 0; i < oldLines.Count; i++)
        {
            if (AspE2E.Cp949.GetString(newLines[i]).Contains("이름을", StringComparison.Ordinal))
            {
                Assert.Equal(AspE2E.Cp949.GetBytes(Korean + "\n"), newLines[i]);
                Assert.Contains("Enter the name.", Encoding.ASCII.GetString(oldLines[i]), StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(oldLines[i], newLines[i]);
            }
        }
        Assert.Throws<DecoderFallbackException>(() => AspE2E.StrictUtf8.GetString(after));
        Assert.Contains(run.Ui.Lines, l => l.Contains("이 파일은 ASCII였고 CP949로 저장했습니다", StringComparison.Ordinal));
    }

    // ---- safety ----

    [Fact]
    public async Task Eval_Exit5_Restored()
    {
        var file = _copy.File("js/order/list.js");
        var before = File.ReadAllBytes(file);
        var evil = Block(KeywordLine, KeywordLine + "\n    eval(\"var injected = 1;\");");
        var backend = new CannedLlmBackend([evil, evil, evil, evil]);

        var run = await RunAsync(file, backend, "검색어를 eval로 처리해줘", EditKind.Js, function: "fnSearch");

        Assert.Equal(ExitCodes.SafetyRejected, run.Exit);
        Assert.Equal(4, backend.Requests.Count);
        Assert.Equal("JsSafetyRejected", run.FailureKind);
        Assert.Contains("eval()", AspE2E.Feedback(backend.Requests[1].RawPrompt), StringComparison.Ordinal);
        Assert.Equal("failed", run.Web.GetProperty("checks").GetProperty("Safety").GetString());
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    // ---- names: warned, not blocked ----

    [Fact]
    public async Task InventedFunction_WarnedNotBlocked()
    {
        var added = "    if (keyword == \"help\") {\n        gfnOpenPopup(\"help.asp\", 400, 300);\n"
            + "        checkDate(keyword);\n        fnUnknown(keyword);\n    }";
        var file = _copy.File("js/order/list.js");
        var backend = new CannedLlmBackend([Block(KeywordLine, KeywordLine + "\n" + added)]);

        var run = await RunAsync(file, backend, "검색어가 help이면 도움말 창을 열어줘", EditKind.Js, function: "fnSearch");

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(1, run.Attempts);
        var line = Assert.Single(run.Ui.Lines, l => l.Contains("이름 경고", StringComparison.Ordinal));
        Assert.Contains("fnUnknown", line, StringComparison.Ordinal);
        Assert.DoesNotContain("gfnOpenPopup", line, StringComparison.Ordinal);
        Assert.DoesNotContain("checkDate", line, StringComparison.Ordinal);
        Assert.Equal("warned", run.Web.GetProperty("checks").GetProperty("Names").GetString());
        Assert.Equal(1, run.Web.GetProperty("nameWarnings").GetInt32());
        Assert.Contains("fnUnknown(keyword)", AspE2E.Cp949.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
    }

    // ---- refused and broken files ----

    [Fact]
    public async Task VendorFile_Refused_Exit3()
    {
        var file = _copy.File("lib/jquery-1.8.3.min.js");
        var before = File.ReadAllBytes(file);
        var backend = new CannedLlmBackend([]);

        var run = await RunAsync(file, backend, "주석을 지워줘", EditKind.Js, lines: new TextSpanInfo(1, 1));

        Assert.Equal(ExitCodes.LocateFailed, run.Exit);
        Assert.Empty(backend.Requests);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task BrokenJs_FallbackChecks_SkippedShown()
    {
        const string Open = "    var w = window.open(url, \"popup\", \"width=400,height=300\");";
        var file = _copy.File("js/legacy/broken.js");
        var before = File.ReadAllBytes(file);
        var span = WebE2E.FunctionSpan(before, Encoding.ASCII, "function oldPopup(");
        var backend = new CannedLlmBackend([Block(Open, "    var w = window.open(url, \"popup\", \"width=500,height=400\");")]);

        var run = await RunAsync(file, backend, "팝업 크기를 500x400으로 키워줘", EditKind.Js, function: "oldPopup");

        Assert.Equal(ExitCodes.Success, run.Exit);
        var changed = Encoding.ASCII.GetString(AspE2E.AssertOutsideBytesIdentical(before, File.ReadAllBytes(file), span));
        Assert.Contains("width=500,height=400", changed, StringComparison.Ordinal);
        Assert.Equal("skipped", run.Web.GetProperty("checks").GetProperty("Syntax").GetString());
        Assert.Contains(run.Ui.Lines, l => l.Contains("문법 건너뜀", StringComparison.Ordinal));
    }

    // ---- admin.css ----

    [Fact]
    public async Task Css_SelectorInMediaAndTop_NonInteractive_Exit3()
    {
        var file = _copy.File("css/admin.css");
        var before = File.ReadAllBytes(file);
        var backend = new CannedLlmBackend([]);

        var run = await RunAsync(file, backend, "표 제목 높이를 키워줘", EditKind.Css, selector: ".list_tbl th");

        Assert.Equal(ExitCodes.LocateFailed, run.Exit);
        Assert.Empty(backend.Requests);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Css_IeHacksOriginal_EditPasses()
    {
        const string Th = ".list_tbl th { height: 28px; _height: 28px; background: #eeeeee; color: #333333; text-align: center; }";
        const string Taller = ".list_tbl th { height: 32px; _height: 32px; background: #eeeeee; color: #333333; text-align: center; }";
        var file = _copy.File("css/admin.css");
        var before = File.ReadAllBytes(file);
        var line = WebE2E.FunctionSpan(before, AspE2E.Cp949, ".list_tbl th {").StartLine;
        var backend = new CannedLlmBackend([Block(Th, Taller)]);

        var run = await RunAsync(file, backend, "표 제목 높이를 32px로 키워줘", EditKind.Css, line: line);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(1, run.Attempts);
        var after = File.ReadAllBytes(file);
        var changed = AspE2E.Cp949.GetString(AspE2E.AssertOutsideBytesIdentical(before, after, new TextSpanInfo(line, line)));
        Assert.Equal(Taller + "\r\n", changed);
        Assert.Equal("passed", run.Web.GetProperty("checks").GetProperty("Syntax").GetString());
        Assert.Equal("rule", run.Web.GetProperty("unit").GetString());
        Assert.Equal(0, run.Web.GetProperty("propertyWarnings").GetInt32());
    }

    // ---- the corpus itself ----

    [Theory]
    [InlineData("w3-001", null)]
    [InlineData("w3-002", "작업 T1의 영향 대상이 명세에 없습니다: admin/order/history.asp:12 (fnHistory 호출, 인수 1개, onclick)")]
    [InlineData("w3-003", null)]
    public async Task Benchmark_W3Specs_CheckAsDesigned(string id, string? warning)
    {
        var bench = Path.Combine(CorpusPaths.Root, "..", "benchmark", "web-w3", id + ".md");
        Directory.CreateDirectory(_copy.File("changes"));
        File.Copy(bench, _copy.File("changes/" + id + ".md"));
        var r = await PellyCoder.Core.Specs.SpecChecker.CheckAsync(new System.IO.Abstractions.FileSystem(), _copy.File("changes/" + id + ".md"), 160,
            null, true, TestContext.Current.CancellationToken, null, new PellyCoder.Core.Config.AspOptions { SiteRoot = "." });
        Assert.False(r.HasErrors, string.Join("; ", r.Issues.Select(i => i.Message)));
        var js = r.Issues.Where(i => i.Code == "JsImpactNotInSpec").Select(i => i.Message).ToList();
        if (warning is null) Assert.Empty(js);
        else Assert.Equal([warning], js);
    }

    [Fact]
    public void Corpus_WebFiles_AsDesigned()
    {
        foreach (var name in new[] { "js/common.js", "js/order/list.js", "css/admin.css" })
        {
            var bytes = File.ReadAllBytes(CorpusPaths.Get("Asp/" + name));
            Assert.Throws<DecoderFallbackException>(() => AspE2E.StrictUtf8.GetString(bytes));
            Assert.Matches("[가-힣]", AspE2E.Cp949.GetString(bytes));
        }
        var edit = File.ReadAllBytes(CorpusPaths.Get("Asp/js/member/edit.js"));
        Assert.All(edit, b => Assert.True(b < 0x80));
        Assert.Contains("CRLF", AspE2E.LineEnds(edit));
        Assert.Contains("LF", AspE2E.LineEnds(edit));
        Assert.NotEqual((byte)'\n', edit[^1]);
        var jquery = File.ReadAllText(CorpusPaths.Get("Asp/lib/jquery-1.8.3.min.js"));
        Assert.StartsWith("/*! jQuery v1.8.3 | (fake header for tests) */", jquery, StringComparison.Ordinal);
        Assert.True(jquery.Length < 200, "코퍼스에는 실제 jQuery를 넣지 않습니다");
    }

    [Fact]
    public void Corpus_pages_have_expected_client_blocks()
    {
        // page -> (script/style blocks, of which with src). Client VBScript and a commented-out <script> are not blocks.
        var expected = new Dictionary<string, (int Blocks, int Src)>
        {
            ["admin/order/list.asp"] = (1, 0),
            ["admin/order/edit.asp"] = (2, 0),
            ["inc/header.inc"] = (2, 2),
            ["admin/member/style.asp"] = (1, 0),
            ["admin/legacy/old.asp"] = (1, 0),
            ["help/guide.htm"] = (1, 0),
            ["admin/order/history.asp"] = (2, 1), // W3 corpus: a function called from a .js, a block and an onclick
        };
        var pages = Directory.GetFiles(CorpusPaths.Get("Asp"), "*.*", SearchOption.AllDirectories)
            .Where(f => new[] { ".asp", ".inc", ".htm", ".html" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetRelativePath(CorpusPaths.Get("Asp"), f).Replace('\\', '/'));
        foreach (var (name, (blocks, src)) in expected)
        {
            Assert.True(pages.ContainsKey(name), name + " is missing from the corpus");
            var page = AspPage.Parse(ParseText(File.ReadAllBytes(pages[name])));
            Assert.True(blocks == page.ClientBlocks.Count, $"{name}: {page.ClientBlocks.Count} blocks, expected {blocks}");
            Assert.Equal(src, page.ClientBlocks.Count(b => b.Src is not null));
        }
        foreach (var (name, path) in pages.Where(p => !expected.ContainsKey(p.Key)))
            Assert.Empty(AspPage.Parse(ParseText(File.ReadAllBytes(path))).ClientBlocks);

        var old = AspPage.Parse(ParseText(File.ReadAllBytes(pages["admin/legacy/old.asp"])));
        Assert.Equal("JavaScript", Assert.Single(old.ClientBlocks).Language);
        var style = AspPage.Parse(ParseText(File.ReadAllBytes(pages["admin/member/style.asp"])));
        Assert.Equal(AspClientBlockKind.Style, Assert.Single(style.ClientBlocks).Kind);

        // Bytes: CP949 pages are not UTF-8, the .htm is UTF-8 with a <meta charset>, every new page is CRLF only.
        foreach (var name in new[] { "admin/order/list.asp", "admin/order/edit.asp", "admin/member/style.asp", "admin/legacy/old.asp" })
            Assert.Throws<DecoderFallbackException>(() => AspE2E.StrictUtf8.GetString(File.ReadAllBytes(pages[name])));
        var guide = AspE2E.StrictUtf8.GetString(File.ReadAllBytes(pages["help/guide.htm"]));
        Assert.Contains("<meta charset=\"utf-8\">", guide, StringComparison.Ordinal);
        Assert.Matches("[가-힣]", guide);
        foreach (var name in expected.Keys)
            Assert.All(AspE2E.LineEnds(File.ReadAllBytes(pages[name])), e => Assert.Equal("CRLF", e));
    }

    private static string ParseText(byte[] bytes)
    {
        try { return AspE2E.StrictUtf8.GetString(bytes).ReplaceLineEndings("\n"); }
        catch (DecoderFallbackException) { return AspE2E.DecodeLf(bytes); }
    }

    private static List<byte[]> SplitLines(byte[] bytes)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            lines.Add(bytes[start..(i + 1)]);
            start = i + 1;
        }
        if (start < bytes.Length) lines.Add(bytes[start..]);
        return lines;
    }

    private Task<WebRun> RunAsync(string file, ILlmBackend backend, string request, EditKind kind, string? function = null,
        int? line = null, TextSpanInfo? lines = null, string? selector = null, [CallerMemberName] string test = "")
    {
        var options = AspE2E.LoadOptions(_copy.Root);
        options.Llm.ThinkMode = ThinkMode.Off;
        var runRequest = new RunRequest(file, null, function, line, request, Yes: true, DryRun: false, MaxRetries: null,
            Kind: kind, Lines: lines, Selector: selector);
        return WebE2E.RunAsync(_copy.Root, options, backend, runRequest, output, test);
    }
}

internal sealed record WebRun(int Exit, RecordingUserInteraction Ui, string? RunDir, JsonElement? Summary, string Messages)
{
    public int Attempts => Summary?.GetProperty("attempts").GetInt32() ?? 0;

    public string? FailureKind => Summary?.GetProperty("failureKind").GetString();

    public JsonElement Web => Summary!.Value.GetProperty("web");
}

/// <summary>Shared plumbing of the canned and real-model JS/CSS runs.</summary>
internal static class WebE2E
{
    public static async Task<WebRun> RunAsync(string root, PellyOptions options, ILlmBackend backend, RunRequest request,
        ITestOutputHelper output, string test)
    {
        var ui = new RecordingUserInteraction();
        var orchestrator = FsmOrchestratorFactory.CreateWeb(new FileSystem(), backend, ui, options, TimeProvider.System, request.Kind);
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
        return new WebRun(exit, ui, runDir, summary, messages);
    }

    /// <summary>
    /// 1-based lines from the first line containing <paramref name="start"/> to the first later line that is exactly the closing
    /// <c>}</c> (a function) or, when the start line already ends with <c>}</c>, that line alone.
    /// </summary>
    public static TextSpanInfo FunctionSpan(byte[] bytes, Encoding encoding, string start)
    {
        var lines = encoding.GetString(bytes).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var first = Array.FindIndex(lines, l => l.Contains(start, StringComparison.Ordinal));
        Assert.True(first >= 0, $"'{start}' 줄이 없습니다");
        if (lines[first].TrimEnd().EndsWith('}')) return new TextSpanInfo(first + 1, first + 1);
        var last = Array.FindIndex(lines, first, l => l == "}");
        Assert.True(last >= 0, "닫는 } 줄이 없습니다");
        return new TextSpanInfo(first + 1, last + 1);
    }
}
