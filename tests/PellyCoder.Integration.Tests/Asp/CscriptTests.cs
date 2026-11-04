using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Workspaces;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Asp;

/// <summary>
/// The real <c>cscript</c> on Windows (CI windows-integration runs these with <c>--fail-skips on</c>): it must exist,
/// its compile errors must parse, and the compile script must never run the page.
/// </summary>
public sealed class CscriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pelly-cscript-" + Guid.NewGuid().ToString("N"));
    private readonly SystemProcessRunner _processes = new(TimeProvider.System);

    public CscriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Writes <paramref name="script"/> as UTF-16 LE with BOM (as the compile step does) and runs cscript on it.</summary>
    private async Task<(ProcessResult Result, string Path, string Output)> RunAsync(string script, string name = "test.vbs")
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(script.ReplaceLineEndings("\r\n"))], Ct);
        var result = await _processes.RunAsync(
            new ProcessSpec("cscript", ["//Nologo", "//T:10", "//E:vbscript", path], _dir, TimeSpan.FromSeconds(30)), Ct);
        var output = TextDecoding.Decode(result.StdOutBytes) + "\n" + TextDecoding.Decode(result.StdErrBytes ?? []);
        return (result, path, output);
    }

    private async Task<IReadOnlyList<Diagnostic>> CompilePageAsync(string pageLf)
    {
        var script = VbsCompileScript.Build(AspPage.Parse(pageLf), pageLf);
        var (_, path, output) = await RunAsync(script.Text);
        return CscriptOutput.Parse(output, path, script);
    }

    [WindowsFact]
    public async Task Cscript_Available()
    {
        var (result, _, output) = await RunAsync("WScript.Echo \"ok\"\n");

        Assert.True(result.ExitCode == 0, $"cscript를 쓸 수 없습니다 (VBScript가 없는 이미지?): exit {result.ExitCode}\n{output}");
        Assert.Contains("ok", output);
    }

    [WindowsFact]
    public async Task Cscript_CompileError_Parsed()
    {
        var (result, path, output) = await RunAsync("x = 1\nIf x Then\n");

        Assert.Equal(1, result.ExitCode);
        var error = Assert.Single(CscriptOutput.Parse(output, path, new VbsScript("", [1, 2], 2)));
        Assert.Equal("VBS", error.Code);
        Assert.Equal(2, error.Line);
    }

    [WindowsFact]
    public async Task Cscript_ValidPage_NoErrors()
    {
        // Inline sample (Ruling P2): the every-corpus-file compile test belongs to Task 13.
        const string page =
            "<%@ Language=\"VBScript\" CodePage=949 %>\n" +
            "<%\n" +
            "Option Explicit\n" +
            "%>\n" +
            "<!--#include virtual=\"/inc/common.inc\"-->\n" +
            "<%\n" +
            "' 주문 목록\n" +
            "Dim rs, total, i\n" +
            "total = 0\n" +
            "Function FormatPrice(value)\n" +
            "    FormatPrice = FormatNumber(value, 0) & \"원\"\n" +
            "End Function\n" +
            "%>\n" +
            "<html>\n" +
            "<head><title>주문 목록</title></head>\n" +
            "<body>\n" +
            "<% If total > 0 Then %>\n" +
            "  <p>합계: <%= FormatPrice(total) %></p>\n" +
            "<% Else %>\n" +
            "  <p>주문이 없습니다.</p>\n" +
            "<% End If %>\n" +
            "<% If total = 0 Then %><b>없음</b><% Else %><%= total %><% End If %>\n" +
            "<% For i = 1 To 3 %>\n" +
            "  <span><%= i %></span>\n" +
            "<% Next %>\n" +
            "<script runat=\"server\" language=\"VBScript\">\n" +
            "Sub Log(message)\n" +
            "    Dim s : s = \"[\" & message & \"]\"\n" +
            "End Sub\n" +
            "</script>\n" +
            "</body>\n" +
            "</html>\n";

        var errors = await CompilePageAsync(page);

        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [WindowsFact]
    public async Task Cscript_DoesNotExecute()
    {
        var marker = Path.Combine(_dir, "executed.txt");
        var page =
            "<%@ Language=\"VBScript\" %>\n" +
            "<%\n" +
            "Dim fso\n" +
            "Set fso = CreateObject(\"Scripting.FileSystemObject\")\n" +
            "fso.CreateTextFile(\"" + marker + "\")\n" +
            "%>\n";

        var errors = await CompilePageAsync(page);

        Assert.Empty(errors);
        Assert.False(File.Exists(marker));
    }

    [WindowsFact]
    public async Task Cscript_DoesNotExecute_WithOptionExplicit()
    {
        // Option Explicit page: the script reads "Option Explicit", then "WScript.Quit 0" on its own line.
        var marker = Path.Combine(_dir, "executed-explicit.txt");
        var page =
            "<% Option Explicit %>\n" +
            "<%\n" +
            "Dim fso\n" +
            "Set fso = CreateObject(\"Scripting.FileSystemObject\")\n" +
            "fso.CreateTextFile(\"" + marker + "\")\n" +
            "%>\n";
        Assert.Contains("Option Explicit\nWScript.Quit 0\n", VbsCompileScript.Build(AspPage.Parse(page), page).Text);

        var errors = await CompilePageAsync(page);

        Assert.Empty(errors);
        Assert.False(File.Exists(marker));
    }

    [WindowsFact]
    public async Task Cscript_SubOnFirstLine_Compiles()
    {
        // The page has no Option Explicit, so the script starts with "WScript.Quit 0" and then "Sub Hello()".
        const string page = "<% Sub Hello() %>\n<b>hi</b>\n<% End Sub %>\n<% Hello %>\n";

        var errors = await CompilePageAsync(page);

        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [WindowsFact]
    public async Task Cscript_ClassOnFirstLine_Compiles()
    {
        // "Class C" may not follow ":" on a line; each fragment is its own script line, so this compiles.
        const string page = "<% Class C %>\n<% Public Name %>\n<% End Class %>\n<% Dim c : Set c = New C %>\n";
        Assert.StartsWith("WScript.Quit 0\nClass C\n", VbsCompileScript.Build(AspPage.Parse(page), page).Text);

        var errors = await CompilePageAsync(page);

        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [WindowsFact]
    public async Task Cscript_RemOptionExplicit_DoesNotExecute()
    {
        // A Rem line mentioning Option Explicit must not swallow the Quit: the page still does not run.
        var marker = Path.Combine(_dir, "executed-rem.txt");
        var page =
            "<%\n" +
            "Rem Option Explicit is below\n" +
            "Option Explicit\n" +
            "Dim fso\n" +
            "Set fso = CreateObject(\"Scripting.FileSystemObject\")\n" +
            "fso.CreateTextFile(\"" + marker + "\")\n" +
            "%>\n";

        var errors = await CompilePageAsync(page);

        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.False(File.Exists(marker));
    }

    [WindowsFact]
    public async Task Cscript_ErrorLine_MappedToPageLine()
    {
        // HTML lines produce no script line; the reported error must still name the page line of the bad code.
        const string page = "<%@ Language=\"VBScript\" %>\n<html>\n<body>\n<% If x Then %>\n<p>a</p>\n<% x = ( %>\n<% End If %>\n";

        var error = Assert.Single(await CompilePageAsync(page));

        Assert.Equal(6, error.Line);
    }

    [WindowsFact]
    public async Task Cscript_CompileStep_NewErrorFails()
    {
        // The whole step against the real cscript: an unclosed If added to a valid page is a new compile error.
        const string original = "<%@ Language=\"VBScript\" %>\n<%\nOption Explicit\nDim x\nx = 1\n%>\n";
        var current = original.Replace("x = 1\n", "If x Then\nx = 1\n");
        var pagePath = Path.Combine(_dir, "page.asp");
        var request = new RunRequest(pagePath, null, null, null, "x", true, false, null, Kind: EditKind.Asp);
        var ctx = new RunContext("r1", request, new PellyOptions())
        {
            FilePath = pagePath,
            Workspace = new Workspace(_dir, Path.Combine(_dir, ".pelly")),
            Original = new FileSnapshot(pagePath, Encoding.UTF8, false, "\n", true, "sha", original),
            Current = new FileSnapshot(pagePath, Encoding.UTF8, false, "\n", true, "sha", current),
        };
        var parsed = AspPage.Parse(original);
        ctx.Asp = new AspRunState(parsed, AspTargetUnit.Lines, new TextSpanInfo(1, parsed.LineCount), null,
            new AspIncludeSet([], [], false), "CP949", _dir);
        var feedback = new FeedbackBuilder(QwenTokenCounter.Shared, new BudgetOptions().Feedback);

        var result = await new AspCompileStep(_processes, feedback, new FileSystem(), () => true).VerifyAsync(ctx, Ct);

        Assert.False(result.Passed, ctx.Asp.Checks["AspCompile"].Detail);
        Assert.Empty(ctx.Asp.OriginalCompileErrors!);
        Assert.Equal("VBS", Assert.Single(result.NewErrors).Code);
    }
}
