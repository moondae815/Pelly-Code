using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Web;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm.Phases;

/// <summary>JS/CSS SLICE on a page: env lines, [READ-ONLY: page] and [READ-ONLY: html] (design §4.7).</summary>
public sealed class WebSlicePagePhaseTests
{
    private const string Root = "/w";
    private const string PagePath = Root + "/p/list.asp";
    private static readonly WebTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly PellyOptions _options = new();
    private readonly FileGuard _guard;

    public WebSlicePagePhaseTests() => _guard = new FileGuard(_fs, TimeProvider.System, Codec);

    private async Task<RunContext> Sliced(string text, EditKind kind = EditKind.Js, string? function = "fnA", string? selector = null,
        string file = PagePath)
    {
        _fs.AddFile(file, new MockFileData(Encoding.UTF8.GetBytes(text)));
        var request = new RunRequest(file, null, function, null, "do it", true, false, null, Kind: kind, Selector: selector);
        var ctx = new RunContext("r1", request, _options)
        {
            FilePath = file,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = _guard.Read(file);
        ctx.Current = ctx.Original;
        var ct = TestContext.Current.CancellationToken;
        IPhase locate = kind == EditKind.Css
            ? new CssLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec)
            : new JsLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec);
        Assert.Equal(PhaseOutcome.Ok, await locate.ExecuteAsync(ctx, ct));
        Assert.Equal(PhaseOutcome.Ok, await new WebSlicePhase(_guard, QwenTokenCounter.Shared, new BudgetOptions()).ExecuteAsync(ctx, ct));
        return ctx;
    }

    private static string Part(RunContext ctx, string kind) =>
        ctx.SliceResult!.Slice.Included.FirstOrDefault(p => p.Kind == kind)?.Text ?? "";

    private static PromptBuilder Builder() =>
        new(QwenChatTemplate.LoadDefault(), PromptResources.LoadDefault(), QwenTokenCounter.Shared, new BudgetOptions());

    private const string SearchPage =
        "<html>\n" +
        "<form name=\"frm\">\n" +
        "<input type=\"button\" id=\"btnSearch\" value=\"go\">\n" +
        "<input type=\"text\" name=\"userName\">\n" +
        "<input type=\"text\" id=\"unused\">\n" +
        "</form>\n" +
        "<script src=\"/js/common.js\"></script>\n" +
        "<script>\n" +
        "function fnA() {\n" +
        "  $(\"#btnSearch\").click(function () {\n" +
        "    return document.frm.userName.value;\n" +
        "  });\n" +
        "}\n" +
        "function fnB(a, b) {\n" +
        "  return a;\n" +
        "}\n" +
        "</script>\n" +
        "<script>\n" +
        "function fnC(x) {\n" +
        "  return x;\n" +
        "}\n" +
        "</script>\n";

    [Fact]
    public async Task Page_prompt_uses_js_page_v1()
    {
        var ctx = await Sliced(SearchPage);
        var resources = PromptResources.LoadDefault();

        var result = Builder().BuildWeb(ctx.Target!, ctx.SliceResult!.Slice, "do it", null, page: true);

        Assert.Equal("js-page-v1", resources.VersionFor(EditKind.Js, page: true));
        Assert.Equal("css-page-v1", resources.VersionFor(EditKind.Css, page: true));
        Assert.Equal("js-v1", resources.VersionFor(EditKind.Js));
        Assert.Equal("js-page-v1", RunLogger.Summarize(ctx).PromptVersion);
        Assert.Contains("<\\/script>", result.BasePrompt);
        Assert.Contains("[READ-ONLY: html]", result.BasePrompt);
        Assert.Contains("[READ-ONLY: page]", result.BasePrompt);
        Assert.DoesNotContain("[READ-ONLY: file]", result.BasePrompt);
    }

    [Fact]
    public async Task Env_has_server_code_rules_and_jquery_source()
    {
        _options.Web.JqueryVersion = "1.8.3";
        var ctx = await Sliced(SearchPage);

        var env = Part(ctx, "env");

        Assert.Contains("ASP page <script> block", env);
        Assert.Contains("Language level:", env);
        Assert.Contains("jQuery 1.8.3 (source: setting)", env);
        Assert.Contains("<% … %> and <%= … %> are server code. Do not change or write them.", env);
        Assert.Contains("Never write </script> inside a string; write <\\/script>.", env);
    }

    [Fact]
    public async Task Html_section_lists_tags_for_ids_used()
    {
        var ctx = await Sliced(SearchPage);

        var html = Part(ctx, "html");

        Assert.Equal("<input type=\"button\" id=\"btnSearch\" value=\"go\">\n<input type=\"text\" name=\"userName\">", html);
    }

    [Fact]
    public async Task Html_section_capped_at_20_lines()
    {
        var sb = new StringBuilder("<html>\n");
        for (var i = 0; i < 30; i++) sb.Append($"<input id=\"f{i}\">\n");
        sb.Append("<script>\nfunction fnA() {\n");
        for (var i = 0; i < 30; i++) sb.Append($"  $(\"#f{i}\").hide();\n");
        sb.Append("}\n</script>\n");

        var ctx = await Sliced(sb.ToString());

        var lines = Part(ctx, "html").Split('\n');
        Assert.Equal(20, lines.Length);
        Assert.Equal("<input id=\"f0\">", lines[0]);
    }

    [Fact]
    public async Task Page_section_lists_other_block_declarations()
    {
        var ctx = await Sliced(SearchPage);

        var page = Part(ctx, "page");

        Assert.Contains("function fnB(a, b) {", page);
        Assert.Contains("function fnC(x) {", page);
        Assert.DoesNotContain("function fnA", page);
        Assert.DoesNotContain("return a;", page);
    }

    [Fact]
    public async Task Page_section_lists_included_blocks_and_callees_find_other_blocks()
    {
        _fs.AddFile(Root + "/p/inc/menu.inc", new MockFileData(Encoding.UTF8.GetBytes("<script>\nfunction incFn(q) {\n}\n</script>\n")));
        var page = "<!--#include file=\"inc/menu.inc\"-->\n<script>\nfunction fnA() {\n  fnB(1);\n  incFn(2);\n}\nfunction fnB(a) {\n}\n</script>\n";

        var ctx = await Sliced(page);

        Assert.Contains("// p/inc/menu.inc", Part(ctx, "page"));
        Assert.Contains("function incFn(q) {", Part(ctx, "page"));
        Assert.Contains("function fnB(a) {", Part(ctx, "callees"));
    }

    [Fact]
    public async Task Css_page_env_and_related_cover_other_style_blocks()
    {
        const string page = "<style>\n.card {\n  margin: 0;\n}\n</style>\n<style>\n.card, .box {\n  color: red;\n}\n</style>\n";

        var ctx = await Sliced(page, EditKind.Css, function: null, selector: ".box");

        Assert.Contains("ASP page <style> block", Part(ctx, "env"));
        Assert.Contains(".card {\n  margin: 0;\n}", Part(ctx, "related"));
    }

    [Fact]
    public async Task Js_file_prompt_unchanged()
    {
        const string js = "function fnA() {\n  return 1;\n}\nfunction fnB() {\n}\n";
        var ctx = await Sliced(js, file: Root + "/js/a.js");
        var resources = PromptResources.LoadDefault();

        Assert.Equal("js-v1", RunLogger.Summarize(ctx).PromptVersion);
        Assert.Equal(["target", "env", "file"], ctx.SliceResult!.Slice.Included.Select(p => p.Kind).Where(k => k != "around").ToArray());
        Assert.DoesNotContain("ASP page", Part(ctx, "env"));
        Assert.DoesNotContain("(source:", Part(ctx, "env"));
        var result = Builder().BuildWeb(ctx.Target!, ctx.SliceResult.Slice, "do it", null);
        Assert.Contains("[READ-ONLY: file]", result.BasePrompt);
        Assert.Equal(resources.SystemJs, resources.SystemJs);
        Assert.DoesNotContain("[READ-ONLY: html]", result.BasePrompt);
    }
}
