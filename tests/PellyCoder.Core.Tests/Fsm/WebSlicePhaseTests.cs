using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Web;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

public sealed class WebSlicePhaseTests
{
    // A full path on each OS: the site index enumerates full paths, as the real site root always is.
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\w" : "/w";
    private static readonly string JsPath = Root + "/js/order.js";
    private static readonly string CssPath = Root + "/css/site.css";
    private static readonly WebTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly PellyOptions _options = new();
    private readonly FileGuard _guard;

    public WebSlicePhaseTests() => _guard = new FileGuard(_fs, TimeProvider.System, Codec);

    private const string OrderJs =
        "// formats a price\n" +          // 1
        "function fmtPrice(n) {\n" +      // 2
        "  return n + \" won\";\n" +      // 3
        "}\n" +                           // 4
        "\n" +                            // 5
        "// saves the form\n" +           // 6
        "function saveForm(id) {\n" +     // 7
        "  var v = fmtPrice(id);\n" +     // 8
        "  if (!isValid(v)) {\n" +        // 9
        "    return false;\n" +           // 10
        "  }\n" +                         // 11
        "  return true;\n" +              // 12
        "}\n" +                           // 13
        "\n" +                            // 14
        "function other() {\n" +          // 15
        "  return 1;\n" +                 // 16
        "}\n";                            // 17

    private const string CommonJs = "// checks a value\nfunction isValid(v) {\n  return !!v;\n}\n";

    private const string SiteCss =
        ".box, .card {\n" +   // 1
        "  color: red;\n" +   // 2
        "}\n" +               // 3
        ".card {\n" +         // 4
        "  margin: 0;\n" +    // 5
        "}\n" +               // 6
        ".other {\n" +        // 7
        "  padding: 0;\n" +   // 8
        "}\n";                // 9

    private WebSlicePhase Phase(BudgetOptions? budget = null) => new(_guard, QwenTokenCounter.Shared, budget ?? new BudgetOptions());

    private RunContext NewContext(string path, RunRequest request)
    {
        var ctx = new RunContext("r1", request, _options)
        {
            FilePath = path,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = _guard.Read(path);
        ctx.Current = ctx.Original;
        return ctx;
    }

    private async Task<RunContext> LocatedJs(string text, string? function = null, TextSpanInfo? lines = null)
    {
        _fs.AddFile(JsPath, new MockFileData(Encoding.UTF8.GetBytes(text)));
        _fs.AddFile(Root + "/js/common.js", new MockFileData(Encoding.UTF8.GetBytes(CommonJs)));
        var request = new RunRequest(JsPath, null, function, null, "do it", true, false, null, Kind: EditKind.Js, Lines: lines);
        var ctx = NewContext(JsPath, request);
        var outcome = await new JsLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec)
            .ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PhaseOutcome.Ok, outcome);
        return ctx;
    }

    // ".card" alone would match both rules, so the target rule is picked by a line inside it.
    private async Task<RunContext> LocatedCss(string text, int line)
    {
        _fs.AddFile(CssPath, new MockFileData(Encoding.UTF8.GetBytes(text)));
        var request = new RunRequest(CssPath, null, null, line, "do it", true, false, null, Kind: EditKind.Css);
        var ctx = NewContext(CssPath, request);
        var outcome = await new CssLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec)
            .ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PhaseOutcome.Ok, outcome);
        return ctx;
    }

    private static string PartText(RunContext ctx, string kind) => ctx.SliceResult.Slice.Included.Single(p => p.Kind == kind).Text;

    [Fact]
    public async Task Js_PartsInOrder()
    {
        _fs.AddFile(Root + "/js/lib/jquery-1.8.3.min.js", new MockFileData("/*! jQuery v1.8.3 */"));
        var ctx = await LocatedJs(OrderJs, function: "saveForm");

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(["target", "env", "callees", "file", "around"], ctx.SliceResult.Slice.Included.Select(p => p.Kind));
        Assert.Equal(string.Join("\n", OrderJs.Split('\n')[5..13]), PartText(ctx, "target"));
        var env = PartText(ctx, "env");
        Assert.Contains("jQuery 1.8.3", env);
        Assert.Contains("File encoding: CP949", env);
        var callees = PartText(ctx, "callees");
        Assert.Contains("// formats a price\nfunction fmtPrice(n) {", callees);
        Assert.DoesNotContain("won", callees); // no bodies
        Assert.Equal("function fmtPrice(n) {\nfunction other() {", PartText(ctx, "file"));
        var around = PartText(ctx, "around");
        Assert.StartsWith("// formats a price\n", around);
        Assert.Contains("\n... (target lines) ...\n\nfunction other() {", around);
        Assert.Equal("JavaScript ES5 (script); jQuery 1.8.3; file encoding CP949", ctx.SliceResult.Slice.LanguageHint);
        Assert.Equal([new EditableRegion(new TextSpanInfo(6, 13), "target")], ctx.SliceResult.Regions);
        Assert.False(ctx.SliceResult.TargetTooLarge);
    }

    [Fact]
    public async Task Js_CalleeFromSiteIndex_WithPathHeader()
    {
        var ctx = await LocatedJs(OrderJs, function: "saveForm");

        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));

        var callees = PartText(ctx, "callees");
        Assert.Contains("// js/common.js\nfunction isValid(v) {", callees);
        Assert.DoesNotContain("!!v", callees);
        Assert.DoesNotContain("js/order.js", callees);
    }

    [Fact]
    public async Task Js_EnvListsForbiddenFeaturesForEs5()
    {
        var ctx = await LocatedJs(OrderJs, function: "saveForm");

        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));

        var env = PartText(ctx, "env");
        Assert.Contains("Language level: ES5 — do not use let/const, arrow functions, template literals, classes", env);
        Assert.Contains("optional chaining (?.)", env);
        Assert.Contains("jQuery: not found", env);
        Assert.Contains("File encoding: CP949", env);
        Assert.Equal("JavaScript ES5 (script); jQuery none; file encoding CP949", ctx.SliceResult.Slice.LanguageHint);
    }

    [Fact]
    public async Task Js_EnvForEs2015_DoesNotForbidLetConst()
    {
        var ctx = await LocatedJs(OrderJs.Replace("var v", "let v"), function: "saveForm");

        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));

        var env = PartText(ctx, "env");
        Assert.Contains("Language level: ES2015 — do not use exponentiation (**), async/await", env);
        Assert.DoesNotContain("let/const", env);
        Assert.DoesNotContain("arrow functions", env);
    }

    [Fact]
    public async Task Js_Fallback_LevelUnknown()
    {
        var broken = OrderJs + "function broken( {\n";
        var ctx = await LocatedJs(broken, lines: new TextSpanInfo(8, 9));

        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));

        Assert.Contains("Language level: unknown (file did not parse)", PartText(ctx, "env"));
        Assert.Contains("// formats a price\nfunction fmtPrice(n) {", PartText(ctx, "callees"));
        Assert.StartsWith("JavaScript unknown (script);", ctx.SliceResult.Slice.LanguageHint);
    }

    [Fact]
    public async Task Js_Budget_DropsAroundFirst()
    {
        var ctx = await LocatedJs(OrderJs, function: "saveForm");
        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));
        Assert.Equal(["target", "env", "callees", "file", "around"], ctx.SliceResult.Slice.Included.Select(p => p.Kind));
        var tokens = ctx.SliceResult.Slice.Included.ToDictionary(p => p.Kind, p => p.Tokens);

        string[] order = ["around", "file", "callees", "env"];
        for (var dropped = 1; dropped <= order.Length; dropped++)
        {
            var kept = tokens.Where(t => !order.Take(dropped).Contains(t.Key)).Sum(t => t.Value);
            var outcome = await Phase(new BudgetOptions { Context = kept }).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

            Assert.Equal(PhaseOutcome.Ok, outcome);
            Assert.Equal(order.Take(dropped), ctx.SliceResult.Slice.Omitted.Select(p => p.Kind));
            Assert.Equal(5 - dropped, ctx.SliceResult.Slice.Included.Count);
        }
    }

    [Fact]
    public async Task Js_TargetTooLarge_BudgetExceeded()
    {
        var ctx = await LocatedJs(OrderJs, function: "saveForm");

        var outcome = await Phase(new BudgetOptions { Context = 5 }).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.BudgetExceeded, outcome);
        Assert.True(ctx.SliceResult.TargetTooLarge);
        Assert.Equal(["target"], ctx.SliceResult.Slice.Included.Select(p => p.Kind));
        Assert.Contains(ctx.Messages, m => m.Contains("budget.context 5"));
    }

    [Fact]
    public async Task Js_FunctionMovedAfterWrite_Refound()
    {
        var ctx = await LocatedJs(OrderJs, function: "saveForm");
        // An earlier attempt added two lines above the target.
        var moved = OrderJs.Replace("  return n + \" won\";\n", "  var s = n;\n  s = s + \"\";\n  return n + \" won\";\n");
        _fs.File.WriteAllBytes(JsPath, Encoding.UTF8.GetBytes(moved));
        ctx.Current = _guard.Read(JsPath);

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(new TextSpanInfo(8, 15), ctx.Web!.CurrentSpan);
        Assert.Equal(new TextSpanInfo(8, 15), ctx.Target!.MemberSpan);
        Assert.Equal([new EditableRegion(new TextSpanInfo(8, 15), "target")], ctx.SliceResult.Regions);
        Assert.StartsWith("// saves the form\nfunction saveForm(id) {", PartText(ctx, "target"));
    }

    [Fact]
    public async Task Js_SecondOfTwoSameNamed_Renamed_RefoundNotTheFirst()
    {
        const string text = "function fnA(a) {\n  return 1;\n}\nfunction fnA(a) {\n  return 2;\n}\n";
        _fs.AddFile(JsPath, new MockFileData(Encoding.UTF8.GetBytes(text)));
        var request = new RunRequest(JsPath, null, null, 5, "do it", true, false, null, Kind: EditKind.Js, AllowSignatureChange: true);
        var ctx = NewContext(JsPath, request);
        Assert.Equal(PhaseOutcome.Ok, await new JsLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec)
            .ExecuteAsync(ctx, TestContext.Current.CancellationToken));
        Assert.Equal(4, ctx.Web!.Function!.DeclLine);
        // An earlier attempt renamed the second fnA.
        _fs.File.WriteAllBytes(JsPath, Encoding.UTF8.GetBytes(text.Replace("function fnA(a) {\n  return 2;", "function fnB(a) {\n  return 2;")));
        ctx.Current = _guard.Read(JsPath);

        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));
        Assert.Equal(new TextSpanInfo(4, 6), ctx.Web!.CurrentSpan);
    }

    [Fact]
    public async Task Js_FunctionRemoved_TargetNotFound()
    {
        var ctx = await LocatedJs(OrderJs, function: "saveForm");
        _fs.File.WriteAllBytes(JsPath, Encoding.UTF8.GetBytes(OrderJs.Replace("function saveForm(id)", "function keepForm(id)")));
        ctx.Current = _guard.Read(JsPath);

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal("TargetNotFound", ctx.FailureKind);
    }

    [Fact]
    public async Task Js_ExternalChange_EnvError()
    {
        var ctx = await LocatedJs(OrderJs, function: "saveForm");
        _fs.File.AppendAllText(JsPath, "// someone else\n");

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Equal("FileChangedExternally", ctx.FailureKind);
    }

    [Fact]
    public async Task Css_PartsInOrder()
    {
        var ctx = await LocatedCss(SiteCss, 5);

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(["target", "env", "related", "around"], ctx.SliceResult.Slice.Included.Select(p => p.Kind));
        Assert.Equal(".card {\n  margin: 0;\n}", PartText(ctx, "target"));
        Assert.Equal("File encoding: CP949\nIE hacks used in this file: no\nVendor prefixes used in this file: no", PartText(ctx, "env"));
        Assert.Equal("CSS; file encoding CP949; IE hacks: no", ctx.SliceResult.Slice.LanguageHint);
        Assert.Equal([new EditableRegion(new TextSpanInfo(4, 6), "target")], ctx.SliceResult.Regions);
        Assert.Equal(WebTargetUnit.Rule, ctx.Web!.Unit);
    }

    [Fact]
    public async Task Css_RelatedSharesSelector()
    {
        var ctx = await LocatedCss(SiteCss, 5);

        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));

        Assert.Equal(".box, .card {\n  color: red;\n}", PartText(ctx, "related"));
    }

    [Fact]
    public async Task Css_Budget_DropsAroundThenRelatedThenEnv()
    {
        var ctx = await LocatedCss(SiteCss, 5);
        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));
        var tokens = ctx.SliceResult.Slice.Included.ToDictionary(p => p.Kind, p => p.Tokens);

        string[] order = ["around", "related", "env"];
        for (var dropped = 1; dropped <= order.Length; dropped++)
        {
            var kept = tokens.Where(t => !order.Take(dropped).Contains(t.Key)).Sum(t => t.Value);
            var outcome = await Phase(new BudgetOptions { Context = kept }).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

            Assert.Equal(PhaseOutcome.Ok, outcome);
            Assert.Equal(order.Take(dropped), ctx.SliceResult.Slice.Omitted.Select(p => p.Kind));
        }
    }

    [Fact]
    public async Task Css_RuleMovedAfterWrite_Refound()
    {
        var ctx = await LocatedCss(SiteCss, 5);
        _fs.File.WriteAllBytes(CssPath, Encoding.UTF8.GetBytes(SiteCss.Replace("  color: red;\n", "  color: red;\n  border: 0;\n")));
        ctx.Current = _guard.Read(CssPath);

        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));

        Assert.Equal(new TextSpanInfo(5, 7), ctx.Web!.CurrentSpan);
        Assert.Equal(".card {\n  margin: 0;\n}", PartText(ctx, "target"));
    }
}
