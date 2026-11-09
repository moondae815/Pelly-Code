using System.IO.Abstractions.TestingHelpers;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>The whole JS/CSS pipeline (CreateWeb) on a small mock site with a fake model.</summary>
public sealed class WebOrchestratorTests
{
    // A regression must fail the test, not hang the run.
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(60);

    private const string Root = "/w";
    private const string JsPath = Root + "/js/order.js";
    private const string CommonPath = Root + "/js/common.js";
    private const string CssPath = Root + "/css/site.css";
    private const string Banner =
        "JS·CSS는 브라우저에서 실행해 확인하지 않았습니다. 화면에서 직접 확인하십시오. 브라우저 캐시 때문에 바뀐 파일이 바로 반영되지 않을 수 있습니다.";

    private const string OrderJs =
        "function saveForm(id) {\n" +       // 1
        "  var v = fmtPrice(id);\n" +       // 2
        "  return v;\n" +                   // 3
        "}\n";                              // 4

    private const string CommonJs =
        "function fmtPrice(n) {\n" +
        "  return n + \" won\";\n" +
        "}\n";

    private const string SiteCss =
        ".box {\n" +          // 1
        "  color: red;\n" +   // 2
        "}\n" +               // 3
        ".card {\n" +         // 4
        "  margin: 0;\n" +    // 5
        "}\n";                // 6

    private const string Body = "  return v;";
    private const string Good = "  return v + \"!\";";

    private readonly MockFileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly PellyOptions _options = new();

    static WebOrchestratorTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public WebOrchestratorTests()
    {
        _fs.AddDirectory(Root + "/.git"); // pins the workspace root
        _fs.AddFile(CommonPath, new MockFileData(Encoding.ASCII.GetBytes(CommonJs)));
    }

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private byte[] Prepare(string path, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        _fs.AddFile(path, new MockFileData(bytes));
        return bytes;
    }

    private Task<int> RunJs(string? function = "saveForm", bool yes = true, int? maxRetries = null) =>
        FsmOrchestratorFactory.CreateWeb(_fs, _backend, _ui, _options, TimeProvider.System, EditKind.Js)
            .RunAsync(new RunRequest(JsPath, null, function, null, "add a mark", yes, false, maxRetries, Kind: EditKind.Js),
                TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    private Task<int> RunCss(string selector = ".card") =>
        FsmOrchestratorFactory.CreateWeb(_fs, _backend, _ui, _options, TimeProvider.System, EditKind.Css)
            .RunAsync(new RunRequest(CssPath, null, null, null, "add padding", true, false, null, Kind: EditKind.Css, Selector: selector),
                TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    private JsonElement Summary()
    {
        var file = Assert.Single(_fs.Directory.GetFiles(Root + "/.pelly/runs", "summary.json", SearchOption.AllDirectories));
        return JsonDocument.Parse(_fs.File.ReadAllText(file)).RootElement;
    }

    [Fact]
    public async Task Js_HappyPath_Exit0_SummaryHasWebBlock()
    {
        Prepare(JsPath, OrderJs);
        _backend.Enqueue(Block(Body, Good));

        var exit = await RunJs();

        Assert.Equal(0, exit);
        Assert.Equal(OrderJs.Replace(Body, Good), _fs.File.ReadAllText(JsPath));
        var summary = Summary();
        Assert.Equal("Js", summary.GetProperty("kind").GetString());
        Assert.Equal("Web", summary.GetProperty("projectStyle").GetString());
        Assert.Equal("js-v1", summary.GetProperty("promptVersion").GetString());
        var web = summary.GetProperty("web");
        Assert.Equal("function", web.GetProperty("unit").GetString());
        Assert.Equal("CP949", web.GetProperty("encoding").GetString());
        Assert.Equal("ES5", web.GetProperty("originalLevel").GetString());
        Assert.Equal("ES5", web.GetProperty("targetLevel").GetString());
        Assert.Equal(JsonValueKind.Null, web.GetProperty("jquery").ValueKind);
        var checks = web.GetProperty("checks");
        foreach (var name in new[] { "Encoding", "Syntax", "Level", "Shape", "Safety", "Names", "Api" })
            Assert.Equal("passed", checks.GetProperty(name).GetString());
        Assert.Equal(0, web.GetProperty("levelRejections").GetInt32());
        Assert.Equal(0, web.GetProperty("nameWarnings").GetInt32());
        Assert.Equal(0, web.GetProperty("apiWarnings").GetInt32());
        Assert.Equal(0, web.GetProperty("propertyWarnings").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("asp").ValueKind);
    }

    [Fact]
    public async Task Js_LevelThenFixed_Exit0_LevelRejections1()
    {
        Prepare(JsPath, OrderJs);
        _backend.Enqueue(Block(Body, "  var f = () => v;\n  return f();")); // arrow function in an ES5 file
        _backend.Enqueue("Use a function expression.</think>", evalCount: 10);
        _backend.Enqueue(Block(Body, Good));

        var exit = await RunJs();

        Assert.Equal(0, exit);
        Assert.Equal(OrderJs.Replace(Body, Good), _fs.File.ReadAllText(JsPath));
        var web = Summary().GetProperty("web");
        Assert.Equal(1, web.GetProperty("levelRejections").GetInt32());
        Assert.Equal("passed", web.GetProperty("checks").GetProperty("Level").GetString());
    }

    [Fact]
    public async Task Js_RetriesExhausted_Exit1_RestoredBytes()
    {
        var original = Prepare(JsPath, OrderJs);
        const string broken = "  return v +;";
        _backend.Enqueue(Block(Body, broken));
        _backend.Enqueue("Fix the syntax.</think>", evalCount: 10);
        _backend.Enqueue(Block(Body, broken));

        var exit = await RunJs(maxRetries: 1);

        Assert.Equal(1, exit);
        Assert.Equal(original, _fs.File.ReadAllBytes(JsPath));
        var summary = Summary();
        Assert.Equal(FailureKinds.JsSyntaxErrors, summary.GetProperty("failureKind").GetString());
        Assert.Equal("failed", summary.GetProperty("web").GetProperty("checks").GetProperty("Syntax").GetString());
    }

    [Fact]
    public async Task Js_EvalExhausted_Exit5_Restored()
    {
        var original = Prepare(JsPath, OrderJs);
        const string unsafeLine = "  return eval(\"v\");";
        _backend.Enqueue(Block(Body, unsafeLine));
        _backend.Enqueue("Do not run code.</think>", evalCount: 10);
        _backend.Enqueue(Block(Body, unsafeLine));

        var exit = await RunJs(maxRetries: 1);

        Assert.Equal(5, exit);
        Assert.Equal(original, _fs.File.ReadAllBytes(JsPath));
        var summary = Summary();
        Assert.Equal(FailureKinds.JsSafetyRejected, summary.GetProperty("failureKind").GetString());
        Assert.Equal("failed", summary.GetProperty("web").GetProperty("checks").GetProperty("Safety").GetString());
    }

    [Fact]
    public async Task Js_Rejected_Exit2_Restored()
    {
        var original = Prepare(JsPath, OrderJs);
        _ui.IsInteractive = true;
        _ui.ConfirmAnswers.Enqueue(false);
        _backend.Enqueue(Block(Body, Good));

        var exit = await RunJs(yes: false);

        Assert.Equal(2, exit);
        Assert.Equal(original, _fs.File.ReadAllBytes(JsPath));
        Assert.Contains(Banner, _ui.Warnings); // shown before the question
    }

    [Fact]
    public async Task Css_HappyPath_Exit0()
    {
        Prepare(CssPath, SiteCss);
        _backend.Enqueue(Block("  margin: 0;", "  margin: 0;\n  padding: 4px;"));

        var exit = await RunCss();

        Assert.Equal(0, exit);
        Assert.Equal(SiteCss.Replace("  margin: 0;", "  margin: 0;\n  padding: 4px;"), _fs.File.ReadAllText(CssPath));
        var summary = Summary();
        Assert.Equal("Css", summary.GetProperty("kind").GetString());
        Assert.Equal("css-v1", summary.GetProperty("promptVersion").GetString());
        var web = summary.GetProperty("web");
        Assert.Equal("rule", web.GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Null, web.GetProperty("originalLevel").ValueKind);
        var checks = web.GetProperty("checks");
        foreach (var name in new[] { "Encoding", "Syntax", "Shape", "Safety", "Properties" })
            Assert.Equal("passed", checks.GetProperty(name).GetString());
        var line = Assert.Single(_ui.Warnings, w => w.StartsWith("검사: ", StringComparison.Ordinal));
        Assert.Equal("검사: 인코딩 통과, 문법 통과, 대상 모양 통과, 안전 통과, 속성 통과", line);
        Assert.Contains(Banner, _ui.Warnings);
    }

    [Fact]
    public async Task Js_SecondOfTwoSameNamedFunctions_BodyEdit_Exit0()
    {
        const string twoInits =
            "function OrderA() {\n" +              // 1
            "  this.init = function (x) {\n" +     // 2
            "    return x;\n" +                    // 3
            "  };\n" +                             // 4
            "}\n" +                                // 5
            "function OrderB() {\n" +              // 6
            "  this.init = function (y) {\n" +     // 7
            "    return y;\n" +                    // 8
            "  };\n" +                             // 9
            "}\n";                                 // 10
        Prepare(JsPath, twoInits);
        _ui.IsInteractive = true;
        _ui.SelectIndex = labels => labels.ToList().FindIndex(l => l.Contains("줄 7-9", StringComparison.Ordinal));
        _backend.Enqueue(Block("    return y;", "    return y + 1;"));

        var exit = await RunJs(function: "init");

        Assert.Equal(0, exit);
        Assert.Equal(twoInits.Replace("    return y;", "    return y + 1;"), _fs.File.ReadAllText(JsPath));
        Assert.Equal(2, Assert.Single(_ui.SelectLabels).Count);
        Assert.Equal("passed", Summary().GetProperty("web").GetProperty("checks").GetProperty("Shape").GetString());
    }

    [Fact]
    public async Task Css_SecondOfRepeatedSelector_ValueEdit_Exit0()
    {
        const string repeated = ".card {\n  margin: 0;\n}\n.box {\n  color: red;\n}\n.card {\n  padding: 0;\n}\n";
        Prepare(CssPath, repeated);
        _ui.IsInteractive = true;
        _ui.SelectIndex = labels => labels.ToList().FindIndex(l => l.Contains("줄 7-9", StringComparison.Ordinal));
        _backend.Enqueue(Block("  padding: 0;", "  padding: 4px;"));

        var exit = await RunCss();

        Assert.Equal(0, exit);
        Assert.Equal(repeated.Replace("  padding: 0;", "  padding: 4px;"), _fs.File.ReadAllText(CssPath));
    }

    [Fact]
    public async Task Review_ShowsChecksAndBanner_EvenWithYes()
    {
        Prepare(JsPath, OrderJs);
        _backend.Enqueue(Block(Body, "  newHelper(v);\n" + Good));

        var exit = await RunJs(yes: true);

        Assert.Equal(0, exit); // the unknown name is a warning, never a rejection
        var line = Assert.Single(_ui.Warnings, w => w.StartsWith("검사: ", StringComparison.Ordinal));
        Assert.Equal("검사: 인코딩 통과, 문법 통과, 문법 수준 통과, 대상 모양 통과, 안전 통과, 이름 경고(newHelper(줄 3)), API 통과, 요소 건너뜀(페이지가 아님)", line);
        Assert.Contains(Banner, _ui.Warnings);
        Assert.True(_ui.Warnings.IndexOf(line) < _ui.Warnings.IndexOf(Banner));
        Assert.Equal(1, Summary().GetProperty("web").GetProperty("nameWarnings").GetInt32());
        var messages = _fs.File.ReadAllText(Assert.Single(_fs.Directory.GetFiles(Root + "/.pelly/runs", "messages.txt", SearchOption.AllDirectories)));
        Assert.Contains(Banner, messages);
        Assert.Contains(line, messages);
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith("이 파일은 ASCII였고", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Review_AsciiNotice()
    {
        Prepare(JsPath, OrderJs);
        const string korean = "  return v; // 합계";
        _backend.Enqueue(Block(Body, korean));

        var exit = await RunJs();

        Assert.Equal(0, exit);
        Assert.Equal(Encoding.GetEncoding(949).GetBytes(OrderJs.Replace(Body, korean)), _fs.File.ReadAllBytes(JsPath));
        Assert.Contains("이 파일은 ASCII였고 CP949로 저장했습니다", _ui.Warnings);
    }

    [Fact]
    public void CreateWeb_OtherKind_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FsmOrchestratorFactory.CreateWeb(_fs, _backend, _ui, _options, TimeProvider.System, EditKind.Asp));
    }

    private const string AspPagePath = Root + "/order.asp";

    private const string AspPage =
        "<%@ Language=\"VBScript\" %>\n" +
        "<%\n" +
        "Option Explicit\n" +
        "Dim total\n" +
        "Function GetTotal(qty, price)\n" +
        "  GetTotal = qty * price\n" +
        "End Function\n" +
        "total = GetTotal(2, 3)\n" +
        "Response.Write total\n" +
        "%>\n";

    [Fact]
    public async Task Asp_ReviewUnchanged()
    {
        _fs.AddFile(AspPagePath, new MockFileData(Encoding.ASCII.GetBytes(AspPage)));
        _backend.Enqueue(Block("  GetTotal = qty * price", "  GetTotal = qty * price + 1"));

        var exit = await FsmOrchestratorFactory.CreateAsp(_fs, _backend, _ui, _options, TimeProvider.System, new ScriptedProcessRunner(), null, () => false)
            .RunAsync(new RunRequest(AspPagePath, null, "GetTotal", null, "add one", true, false, null, Kind: EditKind.Asp),
                TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

        Assert.Equal(0, exit);
        var line = Assert.Single(_ui.Warnings, w => w.StartsWith("검사: ", StringComparison.Ordinal));
        Assert.Equal(
            "검사: 구조 통과, 안전 통과, 인코딩 통과, 이름 통과, 컴파일 건너뜀(Windows가 아님), "
            + "SP 건너뜀(개발 DB 연결(sql.connectionString)이 설정되지 않았습니다), 논리 통과", line);
        Assert.Contains("ASP는 실행해서 확인하지 않았습니다. 화면에서 직접 확인하십시오.", _ui.Warnings);
        Assert.DoesNotContain(Banner, _ui.Warnings);
    }

    private const string PagePath = Root + "/p/a.asp";
    private const string ServerHint = "서버 값이 필요하면 pelly asp --lines를 쓰십시오";
    private const string ServerPage = "<html>\n<script>\nfunction fnA() {\n  var x = <%= a %>;\n  return x;\n}\n</script>\n";

    private Task<int> RunPage() =>
        FsmOrchestratorFactory.CreateWeb(_fs, _backend, _ui, _options, TimeProvider.System, EditKind.Js)
            .RunAsync(new RunRequest(PagePath, null, "fnA", null, "change", true, false, 0, Kind: EditKind.Js),
                TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    [Fact]
    public async Task Page_ending_with_WebServerCodeChanged_shows_the_asp_lines_hint()
    {
        Prepare(PagePath, ServerPage);
        _backend.Enqueue(Block("  var x = <%= a %>;", "  var x = <%= b %>;"));

        var exit = await RunPage();

        Assert.Equal(1, exit);
        Assert.Equal(FailureKinds.WebServerCodeChanged, Summary().GetProperty("failureKind").GetString());
        Assert.Contains(ServerHint, _ui.Warnings);
    }

    [Fact]
    public async Task Page_ending_with_another_failure_does_not_show_the_asp_lines_hint()
    {
        Prepare(PagePath, ServerPage);
        _backend.Enqueue(Block("  return x;", "  return x +;"));

        var exit = await RunPage();

        Assert.Equal(1, exit);
        Assert.Equal(FailureKinds.JsSyntaxErrors, Summary().GetProperty("failureKind").GetString());
        Assert.DoesNotContain(ServerHint, _ui.Warnings);
    }

    // ---- page success paths (end to end, container "page") ----

    private Task<int> RunPageEdit(EditKind kind, string path, string? function = null, string? selector = null) =>
        FsmOrchestratorFactory.CreateWeb(_fs, _backend, _ui, _options, TimeProvider.System, kind)
            .RunAsync(new RunRequest(path, null, function, null, "change", true, false, 2, Kind: kind, Selector: selector),
                TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    private const string JsCp949Page =
        "<%@ Language=\"VBScript\" CodePage=\"949\" %>\r\n" +
        "<html>\r\n<p>한글 머리</p>\r\n" +
        "<script type=\"text/javascript\">\r\n" +
        "function helper(a) {\r\n  return a;\r\n}\r\n" +
        "</script>\r\n" +
        "<input type=\"text\" name=\"custId\" id=\"custId\">\r\n" +
        "<script>\r\n" +
        "function fnSave() {\r\n" +
        "  var s = '<%= Replace(v, \"'\", \"\\'\") %>';\r\n" +
        "  var n = <%= cnt %>;\r\n" +
        "  return helper(s + n);\r\n" +
        "}\r\n" +
        "</script>\r\n" +
        "<p>끝 한글</p>\n"; // mixed line end at the very end

    [Fact]
    public async Task Page_Js_Function_RetryAfterScriptCloseRejection_Exit0_BytesOutsideBlockUnchanged()
    {
        var path = Root + "/p/list.asp";
        var cp949 = Encoding.GetEncoding(949);
        _fs.AddFile(path, new MockFileData(cp949.GetBytes(JsCp949Page)));
        _backend.Enqueue(Block("  return helper(s + n);", "  return \"</script>\" + helper(s + n);"));
        _backend.Enqueue("Escape it.</think>", evalCount: 10);
        _backend.Enqueue(Block("  return helper(s + n);", "  if (!s) { alert(\"빈 값\"); }\n  return helper(s + n);"));

        var exit = await RunPageEdit(EditKind.Js, path, function: "fnSave");

        Assert.Equal(0, exit);
        var expected = JsCp949Page.Replace("  return helper(s + n);", "  if (!s) { alert(\"빈 값\"); }\r\n  return helper(s + n);");
        Assert.Equal(cp949.GetBytes(expected), _fs.File.ReadAllBytes(path));
        var summary = Summary();
        Assert.Equal("js-page-v1", summary.GetProperty("promptVersion").GetString());
        var web = summary.GetProperty("web");
        Assert.Equal("page", web.GetProperty("container").GetString());
        Assert.Equal("passed", web.GetProperty("checks").GetProperty("Names").GetString());
    }

    private const string NameOnlyPage =
        "<%@ Language=\"VBScript\" CodePage=\"949\" %>\n" +
        "<form>\n<input type=\"text\" name=\"custId\">\n</form>\n" +
        "<script>\n" +
        "function fnSearchOrder() {\n" +
        "  var f = document.forms[0];\n" +
        "  return true;\n" +
        "}\n" +
        "</script>\n";

    [Fact]
    public async Task Page_Js_JqueryIdWithOnlyName_WarnsElements()
    {
        var path = Root + "/admin/order/list.asp";
        Prepare(path, NameOnlyPage);
        _backend.Enqueue(Block("  return true;", "  $(\"#custId\").focus();\n  return true;"));

        var exit = await RunPageEdit(EditKind.Js, path, function: "fnSearchOrder");

        Assert.Equal(0, exit);
        Assert.Equal("warned", Summary().GetProperty("web").GetProperty("checks").GetProperty("Elements").GetString());
    }

    private const string CssHtmPage =
        "<html>\r\n<head>\r\n<meta charset=\"utf-8\">\r\n<style>\r\n.box {\r\n  color: red;\r\n}\r\n.card {\r\n  margin: 0;\r\n}\r\n</style>\r\n</head>\r\n<p>한글</p>\r\n";

    [Fact]
    public async Task Page_Css_InHtmWithMetaCharset_Exit0_BytesOutsideBlockUnchanged()
    {
        var path = Root + "/help/guide.htm";
        _fs.AddFile(path, new MockFileData(Encoding.UTF8.GetBytes(CssHtmPage)));
        _backend.Enqueue(Block("  margin: 0;", "  margin: 0;\n  padding: 4px;"));

        var exit = await RunPageEdit(EditKind.Css, path, selector: ".card");

        Assert.Equal(0, exit);
        Assert.Equal(Encoding.UTF8.GetBytes(CssHtmPage.Replace("  margin: 0;", "  margin: 0;\r\n  padding: 4px;")), _fs.File.ReadAllBytes(path));
        var summary = Summary();
        Assert.Equal("css-page-v1", summary.GetProperty("promptVersion").GetString());
        Assert.Equal("page", summary.GetProperty("web").GetProperty("container").GetString());
    }

    [Fact]
    public async Task Page_Css_InAspWithServerOutput_Exit0_BytesOutsideBlockUnchanged()
    {
        var path = Root + "/p/style.asp";
        var page = CssHtmPage.Replace("color: red;", "color: <%= c %>;");
        _fs.AddFile(path, new MockFileData(Encoding.UTF8.GetBytes(page)));
        _backend.Enqueue(Block("  color: <%= c %>;", "  color: <%= c %>;\n  font-weight: bold;"));

        var exit = await RunPageEdit(EditKind.Css, path, selector: ".box");

        Assert.Equal(0, exit);
        Assert.Equal(Encoding.UTF8.GetBytes(page.Replace("  color: <%= c %>;", "  color: <%= c %>;\r\n  font-weight: bold;")), _fs.File.ReadAllBytes(path));
        var summary = Summary();
        Assert.Equal("css-page-v1", summary.GetProperty("promptVersion").GetString());
        Assert.Equal("page", summary.GetProperty("web").GetProperty("container").GetString());
    }

    [Fact]
    public async Task Page_Js_InSharedInc_TakesNamesAndJqueryFromIncluder()
    {
        Prepare(Root + "/a.asp", "<script src=\"/js/jquery-1.8.3.js\"></script>\n<script>\nfunction fromParent() {}\n</script>\n<!--#include file=\"inc/x.inc\"-->\n");
        Prepare(Root + "/js/jquery-1.8.3.js", "x");
        var path = Root + "/inc/x.inc";
        var inc = "<script>\nfunction own() {\n  return 1;\n}\n</script>\n";
        Prepare(path, inc);
        _backend.Enqueue(Block("  return 1;", "  fromParent();\n  return $(\"#a\").length;"));

        var exit = await RunPageEdit(EditKind.Js, path, function: "own");

        Assert.Equal(0, exit);
        Assert.Equal(inc.Replace("  return 1;", "  fromParent();\n  return $(\"#a\").length;"), _fs.File.ReadAllText(path));
        var summary = Summary();
        Assert.Equal("js-page-v1", summary.GetProperty("promptVersion").GetString());
        var web = summary.GetProperty("web");
        Assert.Equal("page", web.GetProperty("container").GetString());
        Assert.Equal("passed", web.GetProperty("checks").GetProperty("Names").GetString());
        Assert.Equal("page", web.GetProperty("jquerySource").GetString());
    }
}
