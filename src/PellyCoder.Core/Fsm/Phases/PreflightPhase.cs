using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Patchers;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Rules;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>Workspace, lock, file and LLM server checks. Nothing on disk is changed except <c>.pelly</c>.</summary>
public sealed class PreflightPhase(IFileSystem fs, FileGuard fileGuard, ILlmBackend backend, LlmOptions llm,
    IReadOnlyCollection<string> specialTokens) : IPhase
{
    public RunPhase Phase => RunPhase.Preflight;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        // Before anything is touched: raw mode would let the server read this string as a control token.
        if (SpecialTokenGuard.FindIn(ctx.Request.Request, specialTokens) is { } requestToken)
            return Env(ctx, $"요청문에 모델 제어 토큰 '{requestToken}'이(가) 있어 실행할 수 없습니다");

        var path = fs.Path.GetFullPath(ctx.Request.FilePath);
        ctx.FilePath = path;
        if (!fs.File.Exists(path))
            return Env(ctx, $"대상 파일이 없습니다: {path}");

        Workspace ws;
        if (ctx.Request.Host is { } host)
        {
            // The apply holds the lock for all its tasks; ctx.Lock stays null so the orchestrator never releases it.
            if (!IsUnder(host.Workspace.Root, path))
                return Env(ctx, $"대상 파일이 apply 작업 공간 밖입니다: {path} (작업 공간: {host.Workspace.Root})");
            ws = host.Workspace;
            ctx.Workspace = ws;
            WorkspaceLocator.EnsurePellyDir(fs, ws);
        }
        else
        {
            ws = WorkspaceLocator.Resolve(fs, path);
            ctx.Workspace = ws;
            WorkspaceLocator.EnsurePellyDir(fs, ws);
            ctx.Lock = WorkspaceLock.TryAcquire(fs, ws);
            if (ctx.Lock is null)
                return Env(ctx, $"다른 pelly 실행이 진행 중입니다 (작업 폴더: {ws.Root}). 그 실행이 끝난 뒤 다시 시도하세요.");

            // An unfinished `pelly test` may have left a changed test file or csproj; nothing else runs until a person looks.
            var pending = new CharTestPending(fs).FindAll(ws);
            if (pending.Count > 0)
                return EnvKind(ctx, FailureKinds.CharTestPending, CharTestPending.Describe(pending));
        }

        var loaded = RulesLoader.Load(fs, ws);
        if (loaded.Errors.Count > 0)
        {
            var first = loaded.Errors[0];
            var where = first.Line > 0 ? $"줄 {first.Line}: " : "";
            var more = loaded.Errors.Count > 1 ? $" 외 {loaded.Errors.Count - 1}건" : "";
            return EnvKind(ctx, FailureKinds.RulesInvalid,
                $"{RulesLoader.FileName}의 Pelly rules 절에 오류가 있습니다 ({where}{first.Message}{more}). `pelly rules check`로 확인하세요");
        }
        foreach (var rule in loaded.Rules)
        {
            var token = SpecialTokenGuard.FindIn(rule.Text, specialTokens)
                ?? (rule.Forbid is null ? null : SpecialTokenGuard.FindIn(rule.Forbid, specialTokens));
            if (token is not null)
                return EnvKind(ctx, FailureKinds.RulesInvalid,
                    $"{RulesLoader.FileName} 줄 {rule.Line}의 규칙에 모델 제어 토큰 '{token}'이(가) 있어 실행할 수 없습니다");
        }
        ctx.Rules = loaded.Rules;

        try
        {
            ctx.Original = fileGuard.Read(path);
        }
        catch (InvalidDataException ex)
        {
            return Env(ctx, ex.Message);
        }
        ctx.Current = ctx.Original;

        // A notice, not a failure: the write unifies newlines to the majority style (FileSnapshot.NewLine).
        if (ctx.Original.MixedNewLines)
            ctx.Messages.Add("파일에 CRLF와 LF가 섞여 있습니다 — 수정하면 줄바꿈이 "
                + (ctx.Original.NewLine == "\n" ? "LF" : "CRLF") + "로 통일됩니다");

        if (MarkerScanner.HasConflictMarkers(ctx.Original.Text))
            return Env(ctx, $"파일에 병합 충돌 표시(<<<<<<<, =======, >>>>>>>)가 있습니다. 충돌을 먼저 해결하세요: {path}");

        if (SpecialTokenGuard.FindIn(ctx.Original.Text, specialTokens) is { } fileToken)
        {
            var index = ctx.Original.Text.IndexOf(fileToken, StringComparison.Ordinal);
            var line = 1 + ctx.Original.Text.AsSpan(0, index).Count('\n');
            return Env(ctx, $"파일에 모델 제어 토큰 문자열 '{fileToken}'이(가) 있어 안전하게 편집할 수 없습니다 (줄 {line})");
        }

        var status = await backend.GetStatusAsync(ct);
        ctx.ServerStatus = status;
        if (!status.Reachable)
            return Env(ctx, $"LLM 서버 {llm.Endpoint}에 연결할 수 없습니다 ({status.Error ?? "응답 없음"}). "
                + "Ollama가 실행 중인지, llm.endpoint 설정이 맞는지 `pelly doctor`로 확인하세요.");
        if (!status.ModelInstalled && status.Error is not null)
            return Env(ctx, $"서버에서 모델 목록을 확인하지 못했습니다: {status.Error} (pelly doctor)");
        if (!status.ModelInstalled)
            return Env(ctx, $"모델 '{llm.Model}'이(가) 서버 {llm.Endpoint}에 없습니다. "
                + $"`ollama pull {llm.Model}`로 받거나 llm.model 설정을 확인하세요 (`pelly doctor`).");

        return PhaseOutcome.Ok;
    }

    /// <summary>True when <paramref name="path"/> lies inside <paramref name="root"/> (path-segment aware, platform case rules).</summary>
    private bool IsUnder(string root, string path)
    {
        var relative = fs.Path.GetRelativePath(fs.Path.GetFullPath(root), path);
        return relative != "."
            && !fs.Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + fs.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + fs.Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static PhaseOutcome EnvKind(RunContext ctx, string kind, string message)
    {
        ctx.FailureKind = kind;
        return Env(ctx, message);
    }

    private static PhaseOutcome Env(RunContext ctx, string message)
    {
        ctx.Messages.Add(message);
        return PhaseOutcome.EnvError;
    }
}
