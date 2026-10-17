using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Patchers;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>Workspace, lock, file and LLM server checks. Nothing on disk is changed except <c>.pelly</c>.</summary>
public sealed class PreflightPhase(IFileSystem fs, FileGuard fileGuard, ILlmBackend backend, LlmOptions llm) : IPhase
{
    public RunPhase Phase => RunPhase.Preflight;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var path = fs.Path.GetFullPath(ctx.Request.FilePath);
        ctx.FilePath = path;
        if (!fs.File.Exists(path))
            return Env(ctx, $"대상 파일이 없습니다: {path}");

        var ws = WorkspaceLocator.Resolve(fs, path);
        ctx.Workspace = ws;
        WorkspaceLocator.EnsurePellyDir(fs, ws);
        ctx.Lock = WorkspaceLock.TryAcquire(fs, ws);
        if (ctx.Lock is null)
            return Env(ctx, $"다른 pelly 실행이 진행 중입니다 (작업 폴더: {ws.Root}). 그 실행이 끝난 뒤 다시 시도하세요.");

        try
        {
            ctx.Original = fileGuard.Read(path);
        }
        catch (InvalidDataException ex)
        {
            return Env(ctx, ex.Message);
        }
        ctx.Current = ctx.Original;

        if (MarkerScanner.HasConflictMarkers(ctx.Original.Text))
            return Env(ctx, $"파일에 병합 충돌 표시(<<<<<<<, =======, >>>>>>>)가 있습니다. 충돌을 먼저 해결하세요: {path}");

        var status = await backend.GetStatusAsync(ct);
        ctx.ServerStatus = status;
        if (!status.Reachable)
            return Env(ctx, $"LLM 서버 {llm.Endpoint}에 연결할 수 없습니다 ({status.Error ?? "응답 없음"}). "
                + "Ollama가 실행 중인지, llm.endpoint 설정이 맞는지 `pelly doctor`로 확인하세요.");
        if (!status.ModelInstalled)
            return Env(ctx, $"모델 '{llm.Model}'이(가) 서버 {llm.Endpoint}에 없습니다. "
                + $"`ollama pull {llm.Model}`로 받거나 llm.model 설정을 확인하세요 (`pelly doctor`).");

        return PhaseOutcome.Ok;
    }

    private static PhaseOutcome Env(RunContext ctx, string message)
    {
        ctx.Messages.Add(message);
        return PhaseOutcome.EnvError;
    }
}
