using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Verification;

/// <summary>The three ASP VERIFY steps with a fake process runner and a fake SQL executor.</summary>
public sealed class AspStepsTests
{
    private const string Root = "/w";
    private const string PagePath = Root + "/page.asp";

    private const string Original =
        "<%@ Language=\"VBScript\" CodePage=949 %>\n" + // 1
        "<%\n" +                                        // 2
        "Option Explicit\n" +                           // 3
        "Dim conn, total\n" +                           // 4
        "Function Total(id)\n" +                        // 5
        "  conn.Execute \"EXEC dbo.usp_Old 1\"\n" +     // 6
        "  Total = id\n" +                              // 7
        "End Function\n" +                              // 8
        "%>\n";                                         // 9

    private readonly MockFileSystem _fs = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly FakeSqlExecutor _sql = new();
    private readonly FeedbackBuilder _feedback = new(QwenTokenCounter.Shared, new BudgetOptions().Feedback);

    private static RunContext Ctx(string current, string original = Original, AspIncludeSet? includes = null)
    {
        var request = new RunRequest(PagePath, null, "Total", null, "x", true, false, null, Kind: EditKind.Asp);
        var ctx = new RunContext("r1", request, new PellyOptions())
        {
            FilePath = PagePath,
            Workspace = new Workspace(Root, Root + "/.pelly"),
            Original = Snapshot(original),
            Current = Snapshot(current),
        };
        var page = AspPage.Parse(original);
        ctx.Asp = new AspRunState(page, AspTargetUnit.Lines, new TextSpanInfo(1, page.LineCount), null,
            includes ?? new AspIncludeSet([], [], false), "CP949", Root)
        {
            OriginalSpCalls = AspSpCallFinder.Find(page, []),
        };
        return ctx;
    }

    private static FileSnapshot Snapshot(string text) => new(PagePath, Encoding.UTF8, false, "\n", true, "sha", text);

    private static ProcessResult Result(int exitCode, string stdout = "", string stderr = "") =>
        new(exitCode, stdout, stderr, Encoding.UTF8.GetBytes(stdout), TimeSpan.Zero, false, Encoding.UTF8.GetBytes(stderr));

    private AspCompileStep CompileStep(bool windows = true) => new(_processes, _feedback, _fs, () => windows);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- names ----

    [Fact]
    public async Task Names_Unknown_Fails()
    {
        var ctx = Ctx(Original.Replace("  Total = id", "  Total = FormatTotal(id)"));

        var result = await new AspNamesStep(_feedback).VerifyAsync(ctx, Ct);

        Assert.False(result.Passed);
        Assert.Equal(FailureKinds.AspUnknownNames, result.FailureKind);
        var error = Assert.Single(result.NewErrors);
        Assert.Equal(("page.asp", 7), (error.File, error.Line));
        Assert.Contains("line 7: FormatTotal", result.Feedback!.Text);
        Assert.Contains("[READ-ONLY: procedures]와 VBScript 내장 함수만 쓰십시오", result.Feedback.Text);
        Assert.Equal(AspCheckStatus.Failed, ctx.Asp!.Checks["AspNames"].Status);
    }

    [Fact]
    public async Task Names_Downgraded_PassesWithWarning()
    {
        var includes = new AspIncludeSet([], ["missing.inc"], false);
        var ctx = Ctx(Original.Replace("  Total = id", "  Total = FormatTotal(id)"), includes: includes);

        var result = await new AspNamesStep(_feedback).VerifyAsync(ctx, Ct);

        Assert.True(result.Passed);
        var check = ctx.Asp!.Checks["AspNames"];
        Assert.Equal(AspCheckStatus.Warned, check.Status);
        Assert.Contains("include를 다 찾지 못해", check.Detail);
        Assert.Contains("FormatTotal", check.Detail);
    }

    [Fact]
    public async Task Names_NoUnknown_Passes()
    {
        var ctx = Ctx(Original.Replace("  Total = id", "  Total = id + 1"));

        var result = await new AspNamesStep(_feedback).VerifyAsync(ctx, Ct);

        Assert.True(result.Passed);
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp!.Checks["AspNames"].Status);
    }

    // ---- compile ----

    [Fact]
    public async Task Compile_NotWindows_Skipped()
    {
        var ctx = Ctx(Original);

        var result = await CompileStep(windows: false).VerifyAsync(ctx, Ct);

        Assert.True(result.Passed);
        Assert.Empty(_processes.Calls);
        Assert.Equal(new AspCheck("AspCompile", AspCheckStatus.Skipped, "Windows가 아님"), ctx.Asp!.Checks["AspCompile"]);
    }

    [Fact]
    public async Task Compile_WshDisabled_Skipped()
    {
        _processes.Enqueue(Result(1, stderr: "Windows Script Host access is disabled on this machine. Contact your administrator for details.\r\nmore\r\n"));
        var ctx = Ctx(Original);

        var result = await CompileStep().VerifyAsync(ctx, Ct);

        Assert.True(result.Passed);
        var check = ctx.Asp!.Checks["AspCompile"];
        Assert.Equal(AspCheckStatus.Skipped, check.Status);
        Assert.Equal("Windows Script Host access is disabled on this machine. Contact your administrator for details.", check.Detail);
    }

    [Fact]
    public async Task Compile_StartFailureOrTimeout_Skipped()
    {
        _processes.EnqueueThrow(new ProcessStartException("cscript", new FileNotFoundException("nope")));
        var ctx = Ctx(Original);
        Assert.True((await CompileStep().VerifyAsync(ctx, Ct)).Passed);
        Assert.Equal(AspCheckStatus.Skipped, ctx.Asp!.Checks["AspCompile"].Status);

        _processes.Enqueue(new ProcessResult(-1, "", "", [], TimeSpan.Zero, true));
        ctx = Ctx(Original);
        Assert.True((await CompileStep().VerifyAsync(ctx, Ct)).Passed);
        Assert.Equal(AspCheckStatus.Skipped, ctx.Asp!.Checks["AspCompile"].Status);

        _processes.Enqueue(Result(5));
        ctx = Ctx(Original);
        Assert.True((await CompileStep().VerifyAsync(ctx, Ct)).Passed);
        Assert.Equal(AspCheckStatus.Skipped, ctx.Asp!.Checks["AspCompile"].Status);
    }

    [Fact]
    public async Task Compile_NewError_Fails()
    {
        _processes.Enqueue(Result(0));
        // Script: Option Explicit(3), WScript.Quit 0(3), Dim(4), Function(5), conn.Execute(6), If…(7), End Function(8).
        _processes.Enqueue((spec, _) => Task.FromResult(Result(1,
            stderr: spec.Arguments[^1] + "(6, 3) Microsoft VBScript 컴파일 오류: 'End'가 필요합니다.\r\n")));
        var ctx = Ctx(Original.Replace("  Total = id", "  If id Then Total = id Else"));

        var result = await CompileStep().VerifyAsync(ctx, Ct);

        Assert.False(result.Passed);
        Assert.Equal(FailureKinds.AspCompileErrors, result.FailureKind);
        var error = Assert.Single(result.NewErrors);
        Assert.Equal(("page.asp", 7, "VBS", "'End'가 필요합니다."), (error.File, error.Line, error.Code, error.Message));
        Assert.StartsWith("VBSCRIPT COMPILE FAILED (1 error)", result.Feedback!.Text);
        Assert.Equal(AspCheckStatus.Failed, ctx.Asp!.Checks["AspCompile"].Status);
        Assert.Empty(ctx.Asp.OriginalCompileErrors!);

        Assert.Equal(2, _processes.Calls.Count);
        var spec = _processes.Calls[1];
        Assert.Equal("cscript", spec.FileName);
        var script = spec.Arguments[^1];
        Assert.Equal(["//Nologo", "//T:10", "//E:vbscript"], spec.Arguments.Take(3));
        Assert.EndsWith("/w/.pelly/runs/r1/asp-compile-2.vbs", script.Replace('\\', '/'));
        Assert.Equal(TimeSpan.FromSeconds(30), spec.Timeout);
        Assert.EndsWith("/w/.pelly/runs/r1/asp-compile-1.vbs", _processes.Calls[0].Arguments[^1].Replace('\\', '/'));
        var bytes = _fs.File.ReadAllBytes(script);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
        var text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        Assert.Contains("If id Then Total = id Else", text);
        Assert.Contains("Option Explicit\r\nWScript.Quit 0\r\nDim conn, total\r\n", text);
    }

    [Fact]
    public async Task Compile_OriginalErrorOnly_Passes()
    {
        _processes.Enqueue((spec, _) => Task.FromResult(Result(1,
            stdout: spec.Arguments[^1] + "(6, 1) Microsoft VBScript compilation error: Expected 'End'\r\n")));
        _processes.Enqueue((spec, _) => Task.FromResult(Result(1,
            stdout: spec.Arguments[^1] + "(8, 1) Microsoft VBScript compilation error: Expected 'End'\r\n")));
        var ctx = Ctx(Original.Replace("  Total = id", "  x = 1\n  Total = id"));

        var result = await CompileStep().VerifyAsync(ctx, Ct);

        Assert.True(result.Passed);
        Assert.Single(ctx.Asp!.OriginalCompileErrors!);
        var check = ctx.Asp.Checks["AspCompile"];
        Assert.Equal(AspCheckStatus.Warned, check.Status);
        Assert.Equal("원본에 컴파일 오류가 있어 첫 오류 뒤는 검사하지 못했습니다", check.Detail);
    }

    [Fact]
    public async Task Compile_OriginalCompiledOnce()
    {
        _processes.WhenEmpty(Result(0));
        var ctx = Ctx(Original);

        await CompileStep().VerifyAsync(ctx, Ct);
        await CompileStep().VerifyAsync(ctx, Ct);

        Assert.Equal(3, _processes.Calls.Count);
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp!.Checks["AspCompile"].Status);
    }

    // ---- stored procedures ----

    [Fact]
    public async Task Sp_NoSql_Skipped()
    {
        var ctx = Ctx(Original);

        var result = await new AspSpStep(null, _feedback).VerifyAsync(ctx, Ct);

        Assert.True(result.Passed);
        Assert.Equal(AspCheckStatus.Skipped, ctx.Asp!.Checks["AspSp"].Status);
    }

    [Fact]
    public async Task Sp_NewMissing_Fails()
    {
        _sql.Missing.Add(SqlObjectName.Parse("dbo.usp_New"));
        var ctx = Ctx(Original.Replace("  Total = id", "  conn.Execute \"EXEC dbo.usp_New 1\"\n  Total = id"));

        var result = await new AspSpStep(_sql, _feedback).VerifyAsync(ctx, Ct);

        Assert.False(result.Passed);
        Assert.Equal(FailureKinds.AspSpNotFound, result.FailureKind);
        Assert.Contains("개발 DB에 없는 프로시저: dbo.usp_New", result.Feedback!.Text);
        var error = Assert.Single(result.NewErrors);
        Assert.Equal(("page.asp", 7), (error.File, error.Line));
        Assert.Equal(AspCheckStatus.Failed, ctx.Asp!.Checks["AspSp"].Status);
    }

    [Fact]
    public async Task Sp_ExistingCall_NotChecked()
    {
        _sql.Missing.Add(SqlObjectName.Parse("dbo.usp_Old"));
        var ctx = Ctx(Original.Replace("  Total = id", "  Total = id + 1"));

        var result = await new AspSpStep(_sql, _feedback).VerifyAsync(ctx, Ct);

        Assert.True(result.Passed);
        Assert.DoesNotContain("FindMissing", _sql.Calls);
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp!.Checks["AspSp"].Status);
    }

    [Fact]
    public async Task Sp_ArgCountMismatch_Warned()
    {
        _sql.Signatures.Add(new SqlProcedureSignature(SqlObjectName.Parse("dbo.usp_New"),
            [new SqlParameterInfo("@Id", "int", false), new SqlParameterInfo("@Out", "int", true)], null));
        var ctx = Ctx(Original.Replace("  Total = id", "  conn.Execute \"EXEC dbo.usp_New 1, 2, 3\"\n  Total = id"));

        var result = await new AspSpStep(_sql, _feedback).VerifyAsync(ctx, Ct);

        Assert.True(result.Passed);
        var check = ctx.Asp!.Checks["AspSp"];
        Assert.Equal(AspCheckStatus.Warned, check.Status);
        Assert.Contains("dbo.usp_New", check.Detail);
    }

    [Fact]
    public async Task Sp_SqlUnavailable_Skipped()
    {
        _sql.SignaturesFailure = new SqlUnavailableException("down");
        var ctx = Ctx(Original.Replace("  Total = id", "  conn.Execute \"EXEC dbo.usp_New 1\"\n  Total = id"));

        var result = await new AspSpStep(_sql, _feedback).VerifyAsync(ctx, Ct);

        Assert.True(result.Passed);
        Assert.Equal(AspCheckStatus.Skipped, ctx.Asp!.Checks["AspSp"].Status);
    }
}
