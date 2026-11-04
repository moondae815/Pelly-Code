using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Compiles the page with <c>cscript</c> (Windows only) through <see cref="VbsCompileScript"/> and fails only on compile
/// errors the original page did not have, compared by (Code, Message) count. When cscript cannot be used (not Windows,
/// missing, disabled, timed out) the check is recorded as skipped and the run goes on (design §4.5.7).
/// </summary>
public sealed class AspCompileStep(IProcessRunner processes, FeedbackBuilder feedback, IFileSystem fs, Func<bool> isWindows)
    : IVerifierStep
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    public string Name => "AspCompile";

    public async Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Asp ?? throw new InvalidOperationException("ASP verification needs the ASP run state.");
        if (!isWindows())
        {
            state.Record(Name, AspCheckStatus.Skipped, "Windows가 아님");
            return StepResult.Pass;
        }

        if (state.OriginalCompileErrors is null)
        {
            var original = await CompileAsync(ctx, ctx.Original.Text, ct);
            if (original.Skipped is { } reason)
                return Skip(state, reason);
            state.OriginalCompileErrors = original.Errors;
            if (original.Errors.Count > 0)
                ctx.Messages.Add($"원본 페이지에 이미 VBScript 컴파일 오류가 {original.Errors.Count}개 있습니다 — 새로 생긴 오류만 검사합니다");
        }

        var current = await CompileAsync(ctx, ctx.Current.Text, ct);
        if (current.Skipped is { } currentReason)
            return Skip(state, currentReason);

        var newErrors = BaselineDiff.New(state.OriginalCompileErrors, current.Errors);
        if (newErrors.Count == 0)
        {
            state.Record(Name, AspCheckStatus.Passed);
            return StepResult.Pass;
        }
        state.Record(Name, AspCheckStatus.Failed, string.Join(", ", newErrors.Select(e => e.Message).Distinct()));
        return StepResult.Fail(feedback.ForDiagnostics("VBSCRIPT COMPILE FAILED", newErrors, ctx.Current.Text), newErrors, [],
            FailureKinds.AspCompileErrors);
    }

    private StepResult Skip(AspRunState state, string reason)
    {
        state.Record(Name, AspCheckStatus.Skipped, reason);
        return StepResult.Pass;
    }

    private sealed record Compiled(IReadOnlyList<Diagnostic> Errors, string? Skipped);

    private async Task<Compiled> CompileAsync(RunContext ctx, string textLf, CancellationToken ct)
    {
        var page = AspPage.Parse(textLf);
        var script = VbsCompileScript.Build(page, textLf);
        var workspace = ctx.Workspace ?? throw new InvalidOperationException("ASP compile verification needs the workspace.");
        var runDir = fs.Path.Combine(workspace.PellyDir, "runs", ctx.RunId);
        fs.Directory.CreateDirectory(runDir);
        var path = NextScriptPath(runDir);
        var body = Encoding.Unicode.GetBytes(script.Replace("\n", "\r\n", StringComparison.Ordinal));
        fs.File.WriteAllBytes(path, [.. Encoding.Unicode.GetPreamble(), .. body]);

        ProcessResult result;
        try
        {
            result = await processes.RunAsync(
                new ProcessSpec("cscript", ["//Nologo", "//T:10", "//E:vbscript", path], runDir, ProcessTimeout), ct);
        }
        catch (ProcessStartException ex)
        {
            return new Compiled([], FirstLine(ex.Message));
        }

        if (result.TimedOut)
            return new Compiled([], $"cscript 시간 초과({ProcessTimeout.TotalSeconds:0}초)");
        var output = TextDecoding.Decode(result.StdOutBytes) + "\n" + TextDecoding.Decode(result.StdErrBytes ?? []);
        if (result.ExitCode is not (0 or 1))
            return new Compiled([], FirstLine(output) ?? $"cscript 종료 코드 {result.ExitCode}");
        if (result.ExitCode == 1 && !output.Contains("Microsoft VBScript", StringComparison.Ordinal))
            return new Compiled([], FirstLine(output) ?? "cscript 종료 코드 1");

        var file = Path.GetFileName(ctx.FilePath);
        var errors = CscriptOutput.Parse(output, path, page.LineCount).Select(e => e with { File = file }).ToList();
        return new Compiled(errors, null);
    }

    /// <summary><c>asp-compile-&lt;n&gt;.vbs</c> with the first unused n, so every compile of the run keeps its script.</summary>
    private string NextScriptPath(string runDir)
    {
        for (var n = 1; ; n++)
        {
            var path = fs.Path.Combine(runDir, $"asp-compile-{n}.vbs");
            if (!fs.File.Exists(path))
                return path;
        }
    }

    private static string? FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}
