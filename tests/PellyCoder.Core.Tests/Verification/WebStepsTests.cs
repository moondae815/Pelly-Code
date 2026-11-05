using System.Text;
using Acornima;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Web;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Verification;

/// <summary>The three warning-only JS/CSS VERIFY steps: names, APIs, CSS properties and overrides.</summary>
public sealed class WebStepsTests
{
    private const string Root = "/w";
    private const string JsPath = Root + "/js/order.js";
    private const string CssPath = Root + "/css/site.css";

    private const string OriginalJs =
        "function saveForm(id) {\n" + // 1
        "  var v = fmtPrice(id);\n" +  // 2
        "  return v;\n" +              // 3
        "}\n";                         // 4

    private static FileSnapshot Snapshot(string path, string text) => new(path, Encoding.UTF8, false, "\n", true, "sha", text);

    private static RunContext Ctx(string path, EditKind kind, string original, string current, WebRunState state)
    {
        var request = new RunRequest(path, null, null, null, "x", true, false, null, Kind: kind);
        var ctx = new RunContext("r1", request, new PellyOptions())
        {
            FilePath = path,
            Workspace = new Workspace(Root, Root + "/.pelly"),
            Original = Snapshot(path, original),
            Current = Snapshot(path, current),
        };
        ctx.Web = state;
        return ctx;
    }

    private static Lazy<WebSiteIndexResult> Index(params string[] names) =>
        new(() => new WebSiteIndexResult(names.Select(n => new JsIndexEntry(n, "js/common.js", 1, "function " + n + "()")).ToList(), 1, false));

    private static WebRunState JsState(string original, EcmaVersion level = EcmaVersion.ES5, Version? jquery = null, params string[] siteNames) =>
        new(EditKind.Js, WebTargetUnit.Function, new TextSpanInfo(1, 4), original, "UTF-8", true, Root, Index(siteNames))
        {
            OriginalJs = JsDocument.Parse(original, level, false),
            OriginalLevel = level,
            TargetLevel = level,
            Jquery = jquery,
        };

    private static WebRunState FallbackState(string original) =>
        new(EditKind.Js, WebTargetUnit.Lines, new TextSpanInfo(1, 2), original, "UTF-8", true, Root,
            new Lazy<WebSiteIndexResult>(() => throw new InvalidOperationException("site index not used")))
        {
            Fallback = true,
            FallbackReason = "원본이 해석되지 않음: 줄 1",
        };

    private static WebRunState CssState(string original, TextSpanInfo span) =>
        new(EditKind.Css, WebTargetUnit.Rule, span, original, "UTF-8", true, Root,
            new Lazy<WebSiteIndexResult>(() => throw new InvalidOperationException("site index not used")))
        {
            OriginalCss = CssSheet.Parse(original),
        };

    private static StepResult Run(IVerifierStep step, RunContext ctx) =>
        step.VerifyAsync(ctx, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    [Fact]
    public void Names_Unknown_WarnedAndCounted()
    {
        var patched = OriginalJs.Replace("  return v;\n", "  fnX(v);\n  y = siteHelper(v) + extraGlobal;\n  return v;\n");
        var state = JsState(OriginalJs, siteNames: ["fmtPrice", "siteHelper"]);
        var ctx = Ctx(JsPath, EditKind.Js, OriginalJs, patched, state);

        var result = Run(new JsNamesStep(["extraGlobal"]), ctx);

        Assert.True(result.Passed);
        var check = state.Checks["Names"];
        Assert.Equal(AspCheckStatus.Warned, check.Status);
        Assert.Equal("fnX(줄 3), y(줄 4)", check.Detail);
        Assert.Equal(2, state.NameWarnings);
    }

    [Fact]
    public void Names_NoneNew_Passed()
    {
        var patched = OriginalJs.Replace("return v;", "return v + 1;");
        var state = JsState(OriginalJs, siteNames: ["fmtPrice"]);

        var result = Run(new JsNamesStep([]), Ctx(JsPath, EditKind.Js, OriginalJs, patched, state));

        Assert.True(result.Passed);
        Assert.Equal(AspCheckStatus.Passed, state.Checks["Names"].Status);
        Assert.Equal(0, state.NameWarnings);
    }

    [Fact]
    public void Names_Fallback_Skipped()
    {
        const string original = "var a = @@;\nfnX();\n";
        var state = FallbackState(original);

        var result = Run(new JsNamesStep([]), Ctx(JsPath, EditKind.Js, original, original + "fnY();\n", state));

        Assert.True(result.Passed);
        Assert.Equal(new AspCheck("Names", AspCheckStatus.Skipped, "원본이 해석되지 않음: 줄 1"), state.Checks["Names"]);
        Assert.Equal(0, state.NameWarnings);
    }

    [Fact]
    public void Api_Warned()
    {
        var patched = OriginalJs.Replace("  return v;\n", "  $(\"#a\").on(\"click\", fmtPrice);\n  return v.includes(\"x\");\n");
        var state = JsState(OriginalJs, jquery: new Version(1, 4, 2), siteNames: ["fmtPrice"]);

        var result = Run(new JsApiStep(), Ctx(JsPath, EditKind.Js, OriginalJs, patched, state));

        Assert.True(result.Passed);
        var check = state.Checks["Api"];
        Assert.Equal(AspCheckStatus.Warned, check.Status);
        Assert.Contains("`.on`은 jQuery 1.7부터 있습니다(사이트 기준 1.4.2)(줄 3)", check.Detail);
        Assert.Contains("`.includes`는 ES2016 기능입니다(이 파일 기준 ES5)(줄 4)", check.Detail);
        Assert.Equal(2, state.ApiWarnings);
    }

    [Fact]
    public void Api_Fallback_Skipped()
    {
        const string original = "var a = @@;\nfnX();\n";
        var state = FallbackState(original);

        var result = Run(new JsApiStep(), Ctx(JsPath, EditKind.Js, original, original + "s.includes(1);\n", state));

        Assert.True(result.Passed);
        Assert.Equal(new AspCheck("Api", AspCheckStatus.Skipped, "원본이 해석되지 않음: 줄 1"), state.Checks["Api"]);
    }

    [Fact]
    public void Properties_UnknownWarned()
    {
        const string original = ".box {\n  color: red;\n}\n";
        var patched = original.Replace("  color: red;\n", "  color: red;\n  colr: blue;\n");
        var state = CssState(original, new TextSpanInfo(1, 3));

        var result = Run(new CssPropertyStep(), Ctx(CssPath, EditKind.Css, original, patched, state));

        Assert.True(result.Passed);
        Assert.Equal(new AspCheck("Properties", AspCheckStatus.Warned, "알 수 없는 CSS 속성: colr(줄 3)"), state.Checks["Properties"]);
        Assert.Equal(1, state.PropertyWarnings);
        Assert.False(state.Checks.ContainsKey("Override"));
    }

    [Fact]
    public void Override_Recorded()
    {
        const string original = ".box {\n  color: red;\n}\n.box {\n  margin: 0;\n}\n";
        var patched = original.Replace("  margin: 0;\n", "  margin: 0;\n  color: blue;\n");
        var state = CssState(original, new TextSpanInfo(1, 3));
        state.CurrentSpan = new TextSpanInfo(1, 3);

        var result = Run(new CssPropertyStep(), Ctx(CssPath, EditKind.Css, original, patched, state));

        Assert.True(result.Passed);
        Assert.Equal(AspCheckStatus.Passed, state.Checks["Properties"].Status);
        Assert.Equal(new AspCheck("Override", AspCheckStatus.Warned, "줄 2의 `color`은 줄 4의 `.box` 규칙이 덮어씁니다"), state.Checks["Override"]);
    }

    [Fact]
    public void Steps_NeverFail()
    {
        var js = OriginalJs.Replace("  return v;\n", "  undefinedA(); undefinedB();\n  return v.includes(\"x\");\n");
        var jsState = JsState(OriginalJs);
        var jsCtx = Ctx(JsPath, EditKind.Js, OriginalJs, js, jsState);
        const string css = ".box {\n  color: red;\n}\n";
        var cssState = CssState(css, new TextSpanInfo(1, 3));
        var cssCtx = Ctx(CssPath, EditKind.Css, css, css.Replace("color: red;", "colr: red; zzz: 1;"), cssState);

        IVerifierStep[] jsSteps = [new JsNamesStep([]), new JsApiStep()];
        foreach (var step in jsSteps) Assert.Same(StepResult.Pass, Run(step, jsCtx));
        Assert.Same(StepResult.Pass, Run(new CssPropertyStep(), cssCtx));
        Assert.Equal("Names", jsSteps[0].Name);
        Assert.Equal("Api", jsSteps[1].Name);
        Assert.Equal("Properties", new CssPropertyStep().Name);
        Assert.Equal(AspCheckStatus.Warned, jsState.Checks["Names"].Status);
        Assert.Equal(AspCheckStatus.Warned, jsState.Checks["Api"].Status);
        Assert.Equal(AspCheckStatus.Warned, cssState.Checks["Properties"].Status);
    }
}
