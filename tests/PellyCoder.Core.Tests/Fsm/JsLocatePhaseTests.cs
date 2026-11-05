using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Acornima;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Web;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

public sealed class JsLocatePhaseTests
{
    private const string Root = "/w";
    private static readonly WebTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly PellyOptions _options = new();

    private const string Order =
        "var Order = {\n  search: function (a) {\n    return a;\n  }\n};\nOrder.prototype.save = function () {\n  return 1;\n};\n";

    private async Task<(PhaseOutcome Outcome, RunContext Ctx)> Locate(
        string text, string file = "js/list.js", string? function = null, int? line = null, TextSpanInfo? lines = null,
        byte[]? bytes = null)
    {
        var path = Root + "/" + file;
        _fs.AddFile(path, new MockFileData(bytes ?? Encoding.UTF8.GetBytes(text)));

        var request = new RunRequest(path, null, function, line, "do it", true, false, null, Kind: EditKind.Js, Lines: lines);
        var ctx = new RunContext("r1", request, _options)
        {
            FilePath = path,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = Codec.Decode(path, _fs.File.ReadAllBytes(path));
        ctx.Current = ctx.Original;
        var outcome = await new JsLocatePhase(_fs, _ui, _options, Codec).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        return (outcome, ctx);
    }

    [Fact]
    public async Task Function_ByName()
    {
        const string text = "// search\nfunction fnSearch(a) {\n  return a;\n}\n\nfunction fnSave() {\n}\n";

        var (outcome, ctx) = await Locate(text, function: "fnSearch");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(new EditTarget(EditKind.Js, Root + "/js/list.js", "function fnSearch", new TextSpanInfo(1, 4), null), ctx.Target);
        Assert.Equal("Web", ctx.ProjectStyle);
        var state = Assert.IsType<WebRunState>(ctx.Web);
        Assert.Equal(EditKind.Js, state.Kind);
        Assert.Equal(WebTargetUnit.Function, state.Unit);
        Assert.Equal("fnSearch", state.Function!.Name);
        Assert.Equal(new TextSpanInfo(1, 4), state.OriginalSpan);
        Assert.Equal(state.OriginalSpan, state.CurrentSpan);
        Assert.Equal(text, state.OriginalText);
        Assert.Equal("CP949", state.EncodingName);
        Assert.Equal(Root, state.SiteRoot);
        Assert.NotNull(state.OriginalJs);
        Assert.False(state.Fallback);
        Assert.False(state.SiteIndex.IsValueCreated);
        Assert.Equal([new EditableRegion(new TextSpanInfo(1, 4), "target")], state.EditableRegions());
    }

    [Fact]
    public async Task Function_ByQualifiedName()
    {
        var (outcome, ctx) = await Locate(Order, function: "Order.search");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("function Order.search", ctx.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(2, 4), ctx.Target.MemberSpan);

        var (outcome2, ctx2) = await Locate(Order, function: "Order.prototype.save");

        Assert.Equal(PhaseOutcome.Ok, outcome2);
        Assert.Equal("function Order.prototype.save", ctx2.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(6, 8), ctx2.Target.MemberSpan);
    }

    [Fact]
    public async Task Function_CaseSensitive_NotFound()
    {
        var (outcome, ctx) = await Locate("function fnSearch() {\n}\n", function: "fnsearch");

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.TargetNotFound, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("fnsearch"));
        Assert.Null(ctx.Target);
    }

    [Fact]
    public async Task Function_Several_NonInteractive_Ambiguous()
    {
        const string text = "var A = { init: function () {\n} };\nvar B = { init: function () {\n} };\n";

        var (outcome, ctx) = await Locate(text, function: "init");

        Assert.Equal(PhaseOutcome.Ambiguous, outcome);
        Assert.Equal(FailureKinds.TargetAmbiguous, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("A.init") && m.Contains("B.init"));
        Assert.Null(ctx.Target);
    }

    [Fact]
    public async Task Line_NamedInsideReady_PicksNamed()
    {
        const string text = "$(function () {\n  function fnCheck(v) {\n    $(\"#b\").click(function () {\n      alert(v);\n    });\n  }\n  fnCheck(1);\n});\n";

        var (outcome, ctx) = await Locate(text, line: 4);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("function fnCheck", ctx.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(2, 6), ctx.Target.MemberSpan);
    }

    [Fact]
    public async Task Line_InsideAnonymousOnly_AsksForLines()
    {
        const string text = "$(function () {\n  $(\"#btn\").click(function () {\n    alert(1);\n  });\n});\n";

        var (outcome, ctx) = await Locate(text, line: 3);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Contains("3번 줄을 감싸는 이름 있는 함수가 없습니다. --lines A-B로 범위를 주십시오", ctx.Messages);
        Assert.Equal(FailureKinds.TargetNotFound, ctx.FailureKind);
    }

    [Fact]
    public async Task Lines_CutsStatement_NotFound()
    {
        const string text = "var a = 1;\nif (a) {\n  a = 2;\n}\nvar b = 3;\n";

        var (outcome, ctx) = await Locate(text, lines: new TextSpanInfo(1, 3));

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Contains("줄 1-3 범위가 문장 중간을 자릅니다. 문장 경계로 범위를 주십시오", ctx.Messages);
        Assert.Null(ctx.Target);
    }

    [Fact]
    public async Task Lines_Valid_Display()
    {
        const string text = "var a = 1;\nif (a) {\n  a = 2;\n}\nvar b = 3;\n";

        var (outcome, ctx) = await Locate(text, lines: new TextSpanInfo(2, 4));

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("줄 2-4", ctx.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(2, 4), ctx.Target.MemberSpan);
        Assert.Equal(WebTargetUnit.Lines, ctx.Web!.Unit);
        Assert.Null(ctx.Web.Function);
    }

    [Fact]
    public async Task NoOption_OneFunction_Auto()
    {
        var (outcome, ctx) = await Locate("var x = 1;\nfunction only() {\n  return x;\n}\n");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("function only", ctx.Target!.SymbolDisplay);
        Assert.Empty(_ui.SelectLabels);
    }

    [Fact]
    public async Task NotJs_Refused()
    {
        var (outcome, ctx) = await Locate("function a() {}\n", file: "js/list.ts");

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebUnsupportedFile, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("pelly js는 .js 파일만 고칩니다"));
    }

    [Fact]
    public async Task MinFile_Refused()
    {
        var (outcome, ctx) = await Locate("function a() {}\n", file: "js/list.min.js");

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebMinifiedFile, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("압축된 파일은 고치지 않습니다"));
    }

    [Fact]
    public async Task VendorPath_Refused()
    {
        var (outcome, ctx) = await Locate("function a() {}\n", file: "lib/util.js");

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebVendorFile, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("라이브러리 파일은 고치지 않습니다"));
    }

    [Fact]
    public async Task Level_AutoUsesOriginal()
    {
        var (outcome, ctx) = await Locate("function a() {\n  let x = 1;\n  return x;\n}\n");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(EcmaVersion.ES2015, ctx.Web!.OriginalLevel);
        Assert.Equal(EcmaVersion.ES2015, ctx.Web.TargetLevel);
        Assert.False(ctx.Web.Module);
    }

    [Fact]
    public async Task Level_ConfiguredAboveOriginal_UsesConfigured()
    {
        _options.Web.JsTarget = "ES2017";

        var (outcome, ctx) = await Locate("function a() {\n  var x = 1;\n  return x;\n}\n");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(EcmaVersion.ES5, ctx.Web!.OriginalLevel);
        Assert.Equal(EcmaVersion.ES2017, ctx.Web.TargetLevel);
        Assert.DoesNotContain(ctx.Messages, m => m.Contains("web.jsTarget"));
    }

    [Fact]
    public async Task Level_ConfiguredBelowOriginal_UsesOriginalWithNotice()
    {
        _options.Web.JsTarget = "es5";

        var (outcome, ctx) = await Locate("function a() {\n  var x = 1;\n  return x;\n}\nlet y = 2;\n");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(EcmaVersion.ES2015, ctx.Web!.OriginalLevel);
        Assert.Equal(EcmaVersion.ES2015, ctx.Web.TargetLevel);
        Assert.Contains("원본이 ES2015 문법을 써서 web.jsTarget(ES5) 대신 원본 수준(ES2015)을 기준으로 봅니다", ctx.Messages);
    }

    [Fact]
    public async Task Broken_FallbackWithMessage()
    {
        const string text = "function a() {\n  var x = ;\n}\n";

        var (outcome, ctx) = await Locate(text, lines: new TextSpanInfo(1, 2));

        Assert.Equal(PhaseOutcome.Ok, outcome);
        var state = ctx.Web!;
        Assert.True(state.Fallback);
        Assert.Null(state.OriginalJs);
        Assert.Null(state.OriginalLevel);
        Assert.Equal("원본이 해석되지 않음: 줄 2", state.FallbackReason);
        Assert.Contains("JS 문법 검사 건너뜀(원본이 해석되지 않음: 줄 2)", ctx.Messages);
        Assert.Equal("줄 1-2", ctx.Target!.SymbolDisplay); // no statement-cut check in fallback mode
    }

    [Fact]
    public async Task Broken_FunctionStillFoundByScan()
    {
        const string text = "function fnOk(a) {\n  return a;\n}\nfunction fnBad() {\n  var x = ;\n}\n";

        var (outcome, ctx) = await Locate(text, function: "fnOk");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.True(ctx.Web!.Fallback);
        Assert.Equal("function fnOk", ctx.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(1, 3), ctx.Target.MemberSpan);
        Assert.Equal(WebTargetUnit.Function, ctx.Web.Unit);
    }

    [Fact]
    public async Task Jquery_SeveralVersions_Message()
    {
        _fs.AddFile(Root + "/js/jquery-1.8.3.min.js", new MockFileData("x"));
        _fs.AddFile(Root + "/lib/jquery-1.4.2.js", new MockFileData("x"));

        var (outcome, ctx) = await Locate("function a() {\n}\n");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(new Version(1, 4, 2), ctx.Web!.Jquery);
        Assert.Equal(2, ctx.Web.JqueryFound.Count);
        Assert.Contains("jQuery 버전이 여럿입니다: 1.4.2(lib/jquery-1.4.2.js), 1.8.3(js/jquery-1.8.3.min.js) — 가장 낮은 1.4.2를 기준으로 봅니다",
            ctx.Messages);
    }

    [Fact]
    public async Task OriginalAscii_Set()
    {
        var (_, ascii) = await Locate("function a() {\n}\n");
        Assert.True(ascii.Web!.OriginalAscii);

        var (_, korean) = await Locate("// 검색\nfunction b() {\n}\n", file: "js/k.js");
        Assert.False(korean.Web!.OriginalAscii);
        Assert.Equal("UTF-8", korean.Web.EncodingName);
    }
}
