using PellyCode.Core.Abstractions;
using PellyCode.Core.Processes;
using static PellyCode.McpServer.Tests.VerifyHarness;

namespace PellyCode.McpServer.Tests;

/// <summary>
/// pelly_verify on changed ASP pages, <c>.js</c> and <c>.css</c> files of a copy of eval/corpus/Asp (CP949, CRLF): the pure Core
/// checks run on the HEAD text and the current text; unchanged files are read from the live tree on both sides.
/// </summary>
public sealed class VerifyWebTests : IDisposable
{
    private const string List = "Asp/admin/order/list.asp";
    private const string HistoryJs = "Asp/js/order/history.js";
    private const string HistoryPage = "Asp/admin/order/history.asp";
    private const string Css = "Asp/css/admin.css";

    private readonly VerifyHarness _repo = new("Asp");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _repo.Dispose();

    private Task<string> VerifyAsync(Func<bool>? isWindows = null, IProcessRunner? processes = null) =>
        _repo.Service(isWindows: isWindows, processes: processes).VerifyAsync(null, Ct);

    [Fact]
    public async Task Asp_NewUnknownName_IsError()
    {
        _repo.Edit(List, t => t.Replace("pageSize = 20", "pageSize = GetPageSizeX()", StringComparison.Ordinal));

        var text = await VerifyAsync();

        var lines = Lines(text);
        Assert.Equal("결과: 새 오류 1건, 새 경고 0건", lines[0]);
        Assert.Contains($"[ASP] {List} — 새 오류 1건", lines);
        Assert.Contains($"- {List}:22 정의되지 않은 이름: GetPageSizeX", lines);
    }

    [Fact]
    public async Task Verify_Cp949Asp_UnchangedNames_NoWarning()
    {
        _repo.Edit(List, t => t.Replace("' 검색 조건 끝", "' 검색 조건 끝\r\n' 한글 주석을 더함", StringComparison.Ordinal));

        var lines = Lines(await VerifyAsync());

        Assert.Equal("결과: 새 문제 없음", lines[0]);
        Assert.Contains($"[ASP] {List} — 통과", lines);
    }

    [Fact]
    public async Task NoAspWebSettings_SaysNothingChecked()
    {
        // The web check is skipped as a whole: the first line must not claim 「새 문제 없음」.
        _repo.Edit(List, t => t.Replace("pageSize = 20", "pageSize = GetPageSizeX()", StringComparison.Ordinal));
        var options = _repo.Options(o =>
        {
            o.Asp.Configured = false;
            o.Web.Configured = false;
        });

        var lines = Lines(await _repo.Service(options).VerifyAsync(null, Ct));

        Assert.Equal("결과: 검사한 파일이 없습니다", lines[0]);
        Assert.Contains("[건너뜀] ASP·JS·CSS 검사 — 설정에 asp·web 절이 없습니다", lines);
    }

    [Fact]
    public async Task OnlyMinifiedJs_SaysNothingChecked()
    {
        _repo.Write("Asp/js/vendor.min.js", "var a=1;" + string.Concat(Enumerable.Repeat("function f(){return 1;}", 400)) + "\n");

        var lines = Lines(await VerifyAsync());

        Assert.Equal("결과: 검사한 파일이 없습니다", lines[0]);
    }

    [Fact]
    public async Task NonWindows_AspSyntaxSkipped()
    {
        _repo.Edit(List, t => t.Replace("pageSize = 20", "pageSize = 30", StringComparison.Ordinal));
        var processes = new RecordingRunner();

        var lines = Lines(await VerifyAsync(isWindows: () => false, processes: processes));

        Assert.Equal("결과: 새 문제 없음", lines[0]);
        Assert.Contains("[건너뜀] ASP 구문 검사 — Windows가 아님", lines);
        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task Windows_AspSyntax_NewErrorOnly()
    {
        // cscript stands in: the current page (compiled first) reports an Expected 'End' error, the HEAD page compiles.
        _repo.Edit(List, t => t.Replace("pageSize = 20", "pageSize = 21", StringComparison.Ordinal));
        var processes = new RecordingRunner { Output = n => n == 1 ? "{0}(22, 1) Microsoft VBScript compilation error: Expected 'End'\r\n" : "" };

        var lines = Lines(await VerifyAsync(isWindows: () => true, processes: processes));

        Assert.Equal(2, processes.Started.Count);
        Assert.All(processes.Started, s => Assert.Equal("cscript", s.FileName));
        Assert.Equal("결과: 새 오류 1건, 새 경고 0건", lines[0]);
        Assert.Contains($"[ASP] {List} — 새 오류 1건", lines);
        Assert.Contains(lines, l => l.StartsWith($"- {List}:", StringComparison.Ordinal) && l.Contains("Expected 'End'", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_repo.Root, ".pelly", "mcp-verify")) && Directory.EnumerateFiles(Path.Combine(_repo.Root, ".pelly", "mcp-verify")).Any());
    }

    [Fact]
    public async Task Js_NewUnknownName_IsWarning()
    {
        _repo.Edit(HistoryJs, t => t.Replace("    fnHistory(gNo);", "    fnHistory(gNo);\r\n    fnUndefinedThing();", StringComparison.Ordinal));

        var lines = Lines(await VerifyAsync());

        Assert.Equal("결과: 새 오류 0건, 새 경고 1건", lines[0]);
        Assert.Contains($"[JS] {HistoryJs} — 새 경고 1건", lines);
        Assert.Contains($"- {HistoryJs}:15 정의되지 않은 JS 이름: fnUndefinedThing", lines);
    }

    [Fact]
    public async Task JsSignatureChanged_CallerArgMismatch_Warning()
    {
        _repo.Edit(HistoryJs, t => t.Replace("function fnHistory(no) {", "function fnHistory(no, mode) {", StringComparison.Ordinal));

        var lines = Lines(await VerifyAsync());

        Assert.StartsWith("결과: 새 오류 0건, 새 경고 ", lines[0]);
        Assert.Contains($"- {HistoryPage}:17 fnHistory 호출의 인수가 1개 — 서명이 바뀜: fnHistory(no) → fnHistory(no, mode)", lines);
        Assert.Contains($"- {HistoryPage}:12 fnHistory 호출의 인수가 1개 — 서명이 바뀜: fnHistory(no) → fnHistory(no, mode)", lines);
    }

    [Fact]
    public async Task Css_NewUnknownProperty_Warning()
    {
        _repo.Edit(Css, t => t + ".memo_box { colr: red; }\r\n");

        var lines = Lines(await VerifyAsync());

        Assert.Equal("결과: 새 오류 0건, 새 경고 1건", lines[0]);
        Assert.Contains($"[CSS] {Css} — 새 경고 1건", lines);
        Assert.Contains(lines, l => l.StartsWith("- 알 수 없는 CSS 속성: colr", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncChanged_IncluderPagesChecked()
    {
        _repo.Write("Asp/inc/memo_form.inc", "<input type=\"text\" id=\"memoText\" name=\"memoText\">\r\n");
        _repo.Write("Asp/admin/order/memo2.asp", """
            <%@ Language="VBScript" CodePage=949 %>
            <!--#include virtual="/inc/memo_form.inc"-->
            <script type="text/javascript">
            function fnMemo() {
                return document.getElementById("memoText").value;
            }
            </script>

            """.Replace("\n", "\r\n", StringComparison.Ordinal));
        _repo.Commit();
        _repo.Edit("Asp/inc/memo_form.inc", t => t.Replace("id=\"memoText\"", "id=\"memoBody\"", StringComparison.Ordinal));

        var lines = Lines(await VerifyAsync());

        Assert.Equal("결과: 새 오류 0건, 새 경고 1건", lines[0]);
        Assert.Contains("[ASP] Asp/admin/order/memo2.asp — 새 경고 1건", lines);
        // Core's own warning, hints included: the include still has the name.
        Assert.Contains("- #memoText(줄 5) — id 없음, name=\"memoText\"는 있음(inc/memo_form.inc:1)", lines);
    }

    [Fact]
    public async Task IncChanged_RemovedSub_IncluderNameError()
    {
        _repo.Write("Asp/inc/memo_util.inc", "<%\r\nSub WriteMemo(text)\r\n    Response.Write text\r\nEnd Sub\r\n%>\r\n");
        _repo.Write("Asp/admin/order/memo3.asp",
            "<%@ Language=\"VBScript\" CodePage=949 %>\r\n<!--#include virtual=\"/inc/memo_util.inc\"-->\r\n<%\r\nCall WriteMemo(\"x\")\r\n%>\r\n");
        _repo.Commit();
        _repo.Edit("Asp/inc/memo_util.inc", t => t.Replace("WriteMemo", "WriteNote", StringComparison.Ordinal));

        var lines = Lines(await VerifyAsync());

        Assert.Equal("결과: 새 오류 1건, 새 경고 0건", lines[0]);
        Assert.Contains("[ASP] Asp/admin/order/memo3.asp — 새 오류 1건", lines);
        Assert.Contains("- Asp/admin/order/memo3.asp:4 정의되지 않은 이름: WriteMemo", lines);
    }

    [Fact]
    public async Task IncChanged_Over50Pages_Noted()
    {
        _repo.Write("Asp/inc/many.inc", "<input type=\"hidden\" id=\"manyId\">\r\n");
        for (var i = 1; i <= 55; i++)
            _repo.Write($"Asp/many/p{i:00}.asp", "<!--#include virtual=\"/inc/many.inc\"-->\r\n<p>page</p>\r\n");
        _repo.Commit();
        _repo.Edit("Asp/inc/many.inc", t => t.Replace("manyId", "manyKey", StringComparison.Ordinal));

        var lines = Lines(await VerifyAsync());

        Assert.Equal("결과: 새 문제 없음", lines[0]);
        Assert.Contains("Asp/inc/many.inc: include하는 페이지가 55개라 50개만 검사함 — 외 5개 페이지는 검사하지 않음", lines);
    }

    [Fact]
    public async Task Js_CheckCannotRun_SkippedNotPassed()
    {
        // Nesting this deep runs the parsers out of stack: the checks cannot run, so the file must not read 「통과」.
        // One bracket per line: long lines would make it a minified file, which is skipped for another reason.
        const string Deep = "Asp/js/deep.js";
        _repo.Write(Deep, "function fnDeep(a) {\n    var x = " + string.Concat(Enumerable.Repeat("[\n", 50000)) + ";\n}\n");
        _repo.Commit();
        _repo.Edit(Deep, t => t.Replace("fnDeep(a)", "fnDeep(a, b)", StringComparison.Ordinal));

        var lines = Lines(await VerifyAsync());

        Assert.DoesNotContain($"[JS] {Deep} — 통과", lines);
        Assert.Contains(lines, l => l.StartsWith($"[JS] {Deep} 건너뜀 — 검사할 수 없습니다: ", StringComparison.Ordinal));
    }

    /// <summary>Records the processes it was asked to start; <see cref="Output"/> gives the n-th run's stdout (<c>{0}</c> = script path).</summary>
    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessSpec> Started { get; } = [];
        public Func<int, string> Output { get; init; } = _ => "";

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Started.Add(spec);
            var output = string.Format(System.Globalization.CultureInfo.InvariantCulture, Output(Started.Count), spec.Arguments[^1]);
            return Task.FromResult(new ProcessResult(output.Length == 0 ? 0 : 1, output, "", System.Text.Encoding.UTF8.GetBytes(output), TimeSpan.Zero, false));
        }
    }
}
