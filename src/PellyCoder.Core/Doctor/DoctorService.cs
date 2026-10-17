using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Llm;

namespace PellyCoder.Core.Doctor;

public enum DoctorStatus { Ok, Warn, Fail }

public sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail, string? Action);

/// <summary>Runs the environment checks behind <c>pelly doctor</c> (design §8.13, M1 scope).</summary>
public sealed class DoctorService(
    ILlmBackend backend,
    IOllamaDiagnostics diagnostics,
    QwenChatTemplate template,
    ITokenCounter tokens,
    PellyOptions options,
    TimeProvider time)
{
    private const string ProbeSystem = "You are a test.";
    private const string ProbeUser = "ping";
    private const string TokenizerProbe = "public int Add(int a, int b) => a + b; // 합계";

    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var checks = new List<DoctorCheck>();
        var status = await backend.GetStatusAsync(ct);

        var serverOk = status.Reachable;
        checks.Add(serverOk
            ? new("Ollama 서버", DoctorStatus.Ok,
                $"{options.Llm.Endpoint} (Ollama {status.Version}, {(int)status.Latency.TotalMilliseconds}ms)", null)
            : new("Ollama 서버", DoctorStatus.Fail,
                $"{options.Llm.Endpoint} 에 연결할 수 없음{(status.Error is null ? "" : $": {status.Error}")}",
                "llm.endpoint 설정과 서버 실행 상태를 확인하세요"));

        if (!serverOk)
        {
            checks.Add(Skipped("모델", "서버 연결 실패로 건너뜀"));
        }
        else if (!status.ModelInstalled)
        {
            checks.Add(new("모델", DoctorStatus.Fail, $"{options.Llm.Model} 이(가) 설치되어 있지 않음",
                $"서버에서 'ollama pull {options.Llm.Model}' 또는 GGUF 가져오기를 하세요"));
        }
        else if (!status.ModelLoaded)
        {
            checks.Add(new("모델", DoctorStatus.Warn, "설치됨, 아직 로드되지 않음 — 첫 요청은 모델 로딩으로 느릴 수 있습니다", null));
        }
        else
        {
            checks.Add(new("모델", DoctorStatus.Ok, $"{options.Llm.Model} (로드됨)", null));
        }

        var modelUsable = serverOk && status.ModelInstalled;
        var skipReason = !serverOk ? "서버 연결 실패로 건너뜀" : "모델이 없어 건너뜀";
        if (!modelUsable)
        {
            foreach (var name in new[] { "렌더러", "템플릿 일치", "토크나이저 일치", "응답 시간" })
                checks.Add(Skipped(name, skipReason));
        }
        else
        {
            checks.Add(string.IsNullOrWhiteSpace(status.Renderer)
                ? new("렌더러", DoctorStatus.Warn, "서버가 렌더러를 알리지 않음", "Ollama 버전과 모델 구성을 확인하세요")
                : new("렌더러", DoctorStatus.Ok, status.Renderer, null));
            checks.Add(await CheckTemplateAsync(ct));
            checks.Add(await CheckTokenizerAsync(ct));
            checks.Add(await CheckLatencyAsync(ct));
        }

        checks.Add(new("서버 설정 확인 필요", DoctorStatus.Warn,
            "서버에서 직접 확인할 수 없음: OLLAMA_FLASH_ATTENTION, OLLAMA_KV_CACHE_TYPE, OLLAMA_NUM_PARALLEL, OLLAMA_KEEP_ALIVE",
            "서버 관리자에게 PRD §7.1 값을 확인하세요"));
        return checks;
    }

    private static DoctorCheck Skipped(string name, string reason) => new(name, DoctorStatus.Warn, reason, null);

    private LlmRequest Probe(string raw, int numPredict) =>
        new(raw, options.Sampling.Code, numPredict, [], options.Llm.NumCtx, options.Llm.KeepAlive);

    private async Task<DoctorCheck> CheckTemplateAsync(CancellationToken ct)
    {
        const string name = "템플릿 일치";
        const string action = "모델의 chat template이 바뀌었을 수 있습니다. llm.template 설정을 확인하세요";
        try
        {
            var chat = await diagnostics.ChatPromptTokenCountAsync(ProbeSystem, ProbeUser, ct);
            var raw = (await DrainAsync(Probe(template.Build(ProbeSystem, ProbeUser) + template.ThinkOpen, 1), ct)).PromptEvalCount;
            if (chat is null || raw is null)
                return new(name, DoctorStatus.Warn, "서버가 프롬프트 토큰 수를 알리지 않아 비교할 수 없음", action);
            return chat == raw
                ? new(name, DoctorStatus.Ok, $"/api/chat 과 raw 프롬프트가 같은 토큰 수({raw})", null)
                : new(name, DoctorStatus.Warn, $"/api/chat {chat} 토큰, raw 프롬프트 {raw} 토큰으로 다름", action);
        }
        catch (LlmUnavailableException ex)
        {
            return new(name, DoctorStatus.Warn, ex.Message, action);
        }
    }

    private async Task<DoctorCheck> CheckTokenizerAsync(CancellationToken ct)
    {
        const string name = "토크나이저 일치";
        const string action = "모델과 내장 토크나이저가 다릅니다. 같은 모델을 쓰는지 확인하세요";
        try
        {
            var server = (await DrainAsync(Probe(TokenizerProbe, 1), ct)).PromptEvalCount;
            var local = tokens.Count(TokenizerProbe);
            if (server is null)
                return new(name, DoctorStatus.Fail, "서버가 프롬프트 토큰 수를 알리지 않아 비교할 수 없음", action);
            return local == server
                ? new(name, DoctorStatus.Ok, $"로컬과 서버가 같은 토큰 수({local})", null)
                : new(name, DoctorStatus.Fail, $"로컬 {local} 토큰, 서버 {server} 토큰으로 다름", action);
        }
        catch (LlmUnavailableException ex)
        {
            return new(name, DoctorStatus.Fail, ex.Message, action);
        }
    }

    private async Task<DoctorCheck> CheckLatencyAsync(CancellationToken ct)
    {
        const string name = "응답 시간";
        try
        {
            var start = time.GetTimestamp();
            TimeSpan? ttft = null;
            await foreach (var chunk in backend.GenerateAsync(
                Probe(template.Build(ProbeSystem, ProbeUser) + template.EmptyThink, 8), ct))
            {
                if (ttft is null && chunk.Text.Length > 0)
                    ttft = time.GetElapsedTime(start);
            }
            return ttft is null
                ? new(name, DoctorStatus.Warn, "응답 텍스트를 받지 못함", "서버 로그를 확인하세요")
                : new(name, DoctorStatus.Ok, $"첫 토큰까지 {(int)ttft.Value.TotalMilliseconds}ms", null);
        }
        catch (LlmUnavailableException ex)
        {
            return new(name, DoctorStatus.Warn, ex.Message, "서버 상태를 확인하세요");
        }
    }

    private async Task<LlmChunk> DrainAsync(LlmRequest request, CancellationToken ct)
    {
        LlmChunk? last = null;
        await foreach (var chunk in backend.GenerateAsync(request, ct))
        {
            if (chunk.Done)
                last = chunk;
        }
        return last ?? throw new LlmUnavailableException("서버 응답이 완료 표시 없이 끝남");
    }
}
