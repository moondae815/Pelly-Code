using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using Spectre.Console.Cli.Testing;

namespace PellyCoder.Cli.Tests.Commands;

public class EditCommandTests
{
    private sealed class FakeRunner : IRunner
    {
        public RunRequest? Request;
        public int Calls;

        public Task<RunResult> RunDetailedAsync(RunRequest request, PellyOptions options, CancellationToken ct)
        {
            Calls++;
            Request = request;
            return Task.FromResult(new RunResult(0, "run-fake", request.FilePath, null, null, null));
        }
    }

    private sealed class FakeDoctor : IDoctorRunner
    {
        public Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, DoctorContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DoctorCheck>>([]);
    }

    private static (CommandAppTester App, FakeRunner Runner) Build(string file, string? pellyJson = null, string content = "x", Action<MockFileData>? arrange = null)
    {
        var fs = new MockFileSystem();
        var data = new MockFileData(content);
        arrange?.Invoke(data);
        fs.AddFile(file, data);
        fs.AddDirectory("/w/.git");
        if (pellyJson is not null) fs.AddFile("/w/pelly.json", new MockFileData(pellyJson));
        var runner = new FakeRunner();
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IRunner>(runner);
        services.AddSingleton<IUserInteraction>(new FakeUi(false));
        services.AddSingleton<IDoctorRunner>(new FakeDoctor());
        services.AddSingleton<IProcessRunner>(new NoProcessRunner());
        services.AddSingleton(TimeProvider.System);
        Func<string, ISqlDefinitionReader> noReader = _ => throw new InvalidOperationException("no connection expected");
        services.AddSingleton(noReader);
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return (app, runner);
    }

    [Fact]
    public void Edit_CsFile_RunsAsCs()
    {
        var (app, runner) = Build("/w/src/OrderService.cs");

        var result = app.Run("edit", "/w/src/OrderService.cs", "요청", "--method", "Cancel", "--test");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.CSharp, runner.Request!.Kind);
        Assert.Equal("Cancel", runner.Request.MethodName);
        Assert.True(runner.Request.RunTests);
        Assert.Contains("pelly cs", result.Output);
    }

    [Fact]
    public void Edit_SqlFile_RunsAsSql()
    {
        var (app, runner) = Build("/w/db/usp_X.sql");

        var result = app.Run("edit", "/w/db/usp_X.sql", "요청", "--object", "dbo.usp_X");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Sql, runner.Request!.Kind);
        Assert.Equal("dbo.usp_X", runner.Request.ObjectName);
        Assert.Contains("pelly sql", result.Output);
    }

    [Theory]
    [InlineData("/w/site/list.asp")]
    [InlineData("/w/site/inc/func.inc")]
    [InlineData("/w/site/LIST.ASP")]
    public void Edit_AspFile_RunsAsAsp(string file)
    {
        var (app, runner) = Build(file);

        var result = app.Run("edit", file, "요청", "--lines", "3-9");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Asp, runner.Request!.Kind);
        Assert.Equal(new TextSpanInfo(3, 9), runner.Request.Lines);
        Assert.Contains("pelly asp", result.Output);
    }

    [Fact]
    public void Edit_JsFile_RunsAsJs()
    {
        var (app, runner) = Build("/w/site/js/order.js");

        var result = app.Run("edit", "/w/site/js/order.js", "요청", "--function", "fnSearch");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Js, runner.Request!.Kind);
        Assert.Equal("fnSearch", runner.Request.MethodName);
        Assert.Contains("pelly js", result.Output);
    }

    [Fact]
    public void Edit_CssFile_RunsAsCss()
    {
        var (app, runner) = Build("/w/site/css/common.CSS");

        var result = app.Run("edit", "/w/site/css/common.CSS", "요청", "--selector", ".list_tbl th");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Css, runner.Request!.Kind);
        Assert.Equal(".list_tbl th", runner.Request.Selector);
        Assert.Contains("pelly css", result.Output);
    }

    [Theory]
    [InlineData("/w/site/js/a.js", EditKind.Js)]
    [InlineData("/w/site/css/a.css", EditKind.Css)]
    public void Edit_LinesOnJsAndCss_PassedThrough(string file, EditKind kind)
    {
        var (app, runner) = Build(file);

        var result = app.Run("edit", file, "요청", "--lines", "3-9");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(kind, runner.Request!.Kind);
        Assert.Equal(new TextSpanInfo(3, 9), runner.Request.Lines);
    }

    [Fact]
    public void Edit_UpperCaseCsExtension_RunsAsCs()
    {
        var (app, runner) = Build("/w/src/A.CS");

        var result = app.Run("edit", "/w/src/A.CS", "요청", "--line", "4");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.CSharp, runner.Request!.Kind);
        Assert.Equal(4, runner.Request.Line);
    }

    [Fact]
    public void Edit_ExtensionFromAspConfig_RunsAsAsp()
    {
        var (app, runner) = Build("/w/site/page.html", """{ "asp": { "extensions": [".asp", ".html"] } }""");

        var result = app.Run("edit", "/w/site/page.html", "요청", "--lines", "1-2");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Asp, runner.Request!.Kind);
    }

    [Fact]
    public void Edit_UnknownExtension_Exit4()
    {
        var (app, runner) = Build("/w/site/readme.txt");

        var result = app.Run("edit", "/w/site/readme.txt", "요청");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("지원하지 않는 파일 형식입니다: .txt", result.Output);
    }

    [Theory]
    [InlineData("/w/db/usp_X.sql", "--method", "M", "--method", ".sql")]
    [InlineData("/w/db/usp_X.sql", "--test", null, "--test", ".sql")]
    [InlineData("/w/src/A.cs", "--lines", "1-3", "--lines", ".cs")]
    [InlineData("/w/src/A.cs", "--object", "dbo.X", "--object", ".cs")]
    [InlineData("/w/site/a.asp", "--class", "C", "--class", ".asp")]
    [InlineData("/w/site/a.asp", "--strict-baseline", null, "--strict-baseline", ".asp")]
    [InlineData("/w/site/a.js", "--selector", ".x", "--selector", ".js")]
    [InlineData("/w/site/a.js", "--proc", "P", "--proc", ".js")]
    [InlineData("/w/site/a.css", "--function", "f", "--function", ".css")]
    [InlineData("/w/site/a.css", "--method", "M", "--method", ".css")]
    [InlineData("/w/db/usp_X.sql", "--selector", ".x", "--selector", ".sql")]
    public void Edit_OptionForAnotherLanguage_Exit4(string file, string option, string? value, string named, string ext)
    {
        var (app, runner) = Build(file);
        string[] args = value is null ? ["edit", file, "요청", option] : ["edit", file, "요청", option, value];

        var result = app.Run(args);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains($"{named}은(는) {ext} 파일에 쓸 수 없습니다", result.Output);
    }

    [Fact]
    public void Edit_SharedOptions_PassedThrough()
    {
        var (app, runner) = Build("/w/site/a.asp");

        var result = app.Run("edit", "/w/site/a.asp", "요청", "--proc", "P", "--yes", "--dry-run", "--max-retries", "2", "--verbose");

        Assert.Equal(0, result.ExitCode);
        var request = runner.Request!;
        Assert.Equal("P", request.MethodName);
        Assert.True(request.Yes);
        Assert.True(request.DryRun);
        Assert.True(request.Verbose);
        Assert.Equal(2, request.MaxRetries);
    }

    [Fact]
    public void Edit_TargetCommandValidationStillApplies()
    {
        var (app, runner) = Build("/w/site/a.asp");

        var result = app.Run("edit", "/w/site/a.asp", "요청", "--lines", "9-3");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("--lines는 A-B 형식입니다", result.Output);
    }

    // Lines: 1 html, 2 <script>, 3 js, 4 </script>, 5 html, 6 <style>, 7 css, 8 </style>, 9 html
    private const string Page = "<p>a</p>\n<script>\nvar a = 1;\n</script>\n<p>b</p>\n<style>\n.x { color: red; }\n</style>\n<p>c</p>\n";

    [Theory]
    [InlineData("/w/site/p.asp")]
    [InlineData("/w/site/p.html")]
    public void Function_on_page_goes_to_js(string file)
    {
        var (app, runner) = Build(file, content: Page);

        var result = app.Run("edit", file, "요청", "--function", "fnA");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Js, runner.Request!.Kind);
        Assert.Equal("fnA", runner.Request.MethodName);
    }

    [Fact]
    public void Selector_on_page_goes_to_css()
    {
        var (app, runner) = Build("/w/site/p.asp", content: Page);

        var result = app.Run("edit", "/w/site/p.asp", "요청", "--selector", ".x");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Css, runner.Request!.Kind);
        Assert.Equal(".x", runner.Request.Selector);
    }

    [Fact]
    public void Line_in_script_goes_to_js()
    {
        var (app, runner) = Build("/w/site/p.asp", content: Page);

        var result = app.Run("edit", "/w/site/p.asp", "요청", "--line", "3");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Js, runner.Request!.Kind);
        Assert.Equal(3, runner.Request.Line);
        Assert.Contains("/w/site/p.asp 3줄 → <script> 블록이라 pelly js로 실행합니다", result.Output);
    }

    [Fact]
    public void Line_in_style_goes_to_css()
    {
        var (app, runner) = Build("/w/site/p.asp", content: Page);

        var result = app.Run("edit", "/w/site/p.asp", "요청", "--line", "7");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Css, runner.Request!.Kind);
        Assert.Contains("7줄 → <style> 블록이라 pelly css로 실행합니다", result.Output);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("5")]
    public void Line_in_html_goes_to_asp(string line)
    {
        var (app, runner) = Build("/w/site/p.asp", content: Page);

        var result = app.Run("edit", "/w/site/p.asp", "요청", "--line", line);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Asp, runner.Request!.Kind);
    }

    [Fact]
    public void Lines_inside_block_goes_to_js()
    {
        var (app, runner) = Build("/w/site/p.asp", content: "<script>\na();\nb();\nc();\n</script>\n");

        var result = app.Run("edit", "/w/site/p.asp", "요청", "--lines", "2-4");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Js, runner.Request!.Kind);
        Assert.Equal(new TextSpanInfo(2, 4), runner.Request.Lines);
    }

    [Fact]
    public void Lines_crossing_block_goes_to_asp()
    {
        var (app, runner) = Build("/w/site/p.asp", content: Page);

        var result = app.Run("edit", "/w/site/p.asp", "요청", "--lines", "3-7");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Asp, runner.Request!.Kind);
    }

    [Fact]
    public void Lines_on_unreadable_page_is_exit_4()
    {
        var (app, runner) = Build("/w/site/p.asp", content: Page, arrange: d => d.AllowedFileShare = FileShare.None);

        var result = app.Run("edit", "/w/site/p.asp", "요청", "--lines", "2-4");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("/w/site/p.asp", result.Output);
    }

    [Fact]
    public void Htm_without_target_goes_to_js()
    {
        var (app, runner) = Build("/w/site/p.htm", content: Page);

        var result = app.Run("edit", "/w/site/p.htm", "요청");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Js, runner.Request!.Kind);
        Assert.Contains("pelly js", result.Output);
    }

    [Theory]
    [InlineData("--line", "1")]
    [InlineData("--lines", "3-7")]
    [InlineData("--proc", "P")]
    public void Htm_line_in_html_is_exit_4(string option, string value)
    {
        var (app, runner) = Build("/w/site/p.HTML", content: Page);

        var result = app.Run("edit", "/w/site/p.HTML", "요청", option, value);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains(".htm 파일의 HTML 부분은 고치지 않습니다", result.Output);
    }

    [Theory]
    [InlineData("--function", "f")]
    [InlineData("--selector", ".x")]
    public void Proc_and_function_together_is_exit_4(string other, string value)
    {
        var (app, runner) = Build("/w/site/p.asp", content: Page);

        var result = app.Run("edit", "/w/site/p.asp", "요청", "--proc", "P", other, value);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("--proc", result.Output);
        Assert.Contains(other, result.Output);
    }
}
