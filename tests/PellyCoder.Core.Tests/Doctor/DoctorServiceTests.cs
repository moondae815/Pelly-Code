using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Doctor;

public sealed class DoctorServiceTests
{
    private const string TokenizerProbe = "public int Add(int a, int b) => a + b; // 합계";

    private sealed class FixedCounter(int count) : ITokenCounter
    {
        public int Count(string text) => count;
    }

    private readonly ScriptedLlmBackend _backend = new();
    private readonly FakeOllamaDiagnostics _diag = new() { Count = 20 };
    private readonly FakeTimeProvider _time = new();
    private readonly QwenChatTemplate _template = QwenChatTemplate.LoadDefault();
    private readonly PellyOptions _options = new();

    private DoctorService Create(int localCount = 12) =>
        new(_backend, _diag, _template, new FixedCounter(localCount), _options, _time);

    private void EnqueueProbes(int templatePrompt = 20, int tokenizerPrompt = 12)
    {
        _backend.Enqueue("x", promptEvalCount: templatePrompt);
        _backend.Enqueue("x", promptEvalCount: tokenizerPrompt);
        _backend.Enqueue("hello");
    }

    private static DoctorCheck Get(IReadOnlyList<DoctorCheck> checks, string name) =>
        checks.Single(c => c.Name == name);

    [Fact]
    public async Task AllGood_AllOkExceptServerSettingsWarn()
    {
        EnqueueProbes();
        _backend.BeforeRespond = _ => { _time.Advance(TimeSpan.FromMilliseconds(40)); return Task.CompletedTask; };

        var checks = await Create().RunAsync(CancellationToken.None);

        Assert.Equal(
            ["Ollama 서버", "모델", "렌더러", "템플릿 일치", "토크나이저 일치", "응답 시간", "서버 설정 확인 필요"],
            checks.Select(c => c.Name));
        Assert.All(checks.Take(6), c => Assert.Equal(DoctorStatus.Ok, c.Status));
        var settings = checks[6];
        Assert.Equal(DoctorStatus.Warn, settings.Status);
        foreach (var name in new[] { "OLLAMA_FLASH_ATTENTION", "OLLAMA_KV_CACHE_TYPE", "OLLAMA_NUM_PARALLEL", "OLLAMA_KEEP_ALIVE" })
            Assert.Contains(name, settings.Detail);
        Assert.Contains("ms", Get(checks, "응답 시간").Detail);
    }

    [Fact]
    public async Task ServerDown_FirstFail_RestSkipped()
    {
        _backend.Status = new LlmServerStatus(false, null, false, false, null, TimeSpan.Zero, "refused");

        var checks = await Create().RunAsync(CancellationToken.None);

        Assert.Equal(DoctorStatus.Fail, checks[0].Status);
        Assert.Contains("llm.endpoint", checks[0].Action);
        foreach (var name in new[] { "모델", "렌더러", "템플릿 일치", "토크나이저 일치", "응답 시간" })
        {
            var c = Get(checks, name);
            Assert.Equal(DoctorStatus.Warn, c.Status);
            Assert.Equal("서버 연결 실패로 건너뜀", c.Detail);
        }
        Assert.Empty(_backend.Requests);
        Assert.Equal(DoctorStatus.Warn, Get(checks, "서버 설정 확인 필요").Status);
    }

    [Fact]
    public async Task ModelNotInstalled_IsFailAndLaterChecksSkipped()
    {
        _backend.Status = new LlmServerStatus(true, "0.40.0", false, false, null, TimeSpan.FromMilliseconds(3), null);

        var checks = await Create().RunAsync(CancellationToken.None);

        var model = Get(checks, "모델");
        Assert.Equal(DoctorStatus.Fail, model.Status);
        Assert.Contains($"ollama pull {_options.Llm.Model}", model.Action);
        Assert.Equal("모델이 없어 건너뜀", Get(checks, "템플릿 일치").Detail);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task ModelNotLoaded_IsWarn()
    {
        _backend.Status = new LlmServerStatus(true, "0.40.0", true, false, "qwen3.5", TimeSpan.FromMilliseconds(3), null);
        EnqueueProbes();

        var checks = await Create().RunAsync(CancellationToken.None);

        var model = Get(checks, "모델");
        Assert.Equal(DoctorStatus.Warn, model.Status);
        Assert.Contains("아직 로드되지 않음", model.Detail);
        Assert.Equal(DoctorStatus.Ok, Get(checks, "템플릿 일치").Status);
    }

    [Fact]
    public async Task TemplateMismatch_IsWarn()
    {
        EnqueueProbes(templatePrompt: 25);

        var checks = await Create().RunAsync(CancellationToken.None);

        var t = Get(checks, "템플릿 일치");
        Assert.Equal(DoctorStatus.Warn, t.Status);
        Assert.Contains("llm.template", t.Action);
    }

    [Fact]
    public async Task TokenizerMismatch_IsFail()
    {
        EnqueueProbes(tokenizerPrompt: 99);

        var checks = await Create().RunAsync(CancellationToken.None);

        var t = Get(checks, "토크나이저 일치");
        Assert.Equal(DoctorStatus.Fail, t.Status);
        Assert.Contains("같은 모델", t.Action);
    }

    [Fact]
    public async Task ProbeThrowsLlmUnavailable_DegradesAndContinues()
    {
        _backend.EnqueueException(new LlmUnavailableException("timeout"));
        _backend.Enqueue("x", promptEvalCount: 12);
        _backend.EnqueueException(new LlmUnavailableException("boom"));

        var checks = await Create().RunAsync(CancellationToken.None);

        Assert.Equal(DoctorStatus.Warn, Get(checks, "템플릿 일치").Status);
        Assert.Contains("timeout", Get(checks, "템플릿 일치").Detail);
        Assert.Equal(DoctorStatus.Ok, Get(checks, "토크나이저 일치").Status);
        Assert.Equal(DoctorStatus.Warn, Get(checks, "응답 시간").Status);
        Assert.Contains("boom", Get(checks, "응답 시간").Detail);
    }

    [Fact]
    public async Task UsesFixedProbeStrings()
    {
        EnqueueProbes();

        await Create().RunAsync(CancellationToken.None);

        Assert.Equal(("You are a test.", "ping"), Assert.Single(_diag.Calls));
        Assert.Equal(3, _backend.Requests.Count);
        Assert.Equal(_template.Build("You are a test.", "ping") + _template.ThinkOpen, _backend.Requests[0].RawPrompt);
        Assert.Equal(1, _backend.Requests[0].NumPredict);
        Assert.Equal(TokenizerProbe, _backend.Requests[1].RawPrompt);
        Assert.Equal(1, _backend.Requests[1].NumPredict);
        Assert.Equal(_template.Build("You are a test.", "ping") + _template.EmptyThink, _backend.Requests[2].RawPrompt);
        Assert.Equal(8, _backend.Requests[2].NumPredict);
        Assert.All(_backend.Requests, r =>
        {
            Assert.Equal(_options.Sampling.Code, r.Sampling);
            Assert.Equal(_options.Llm.NumCtx, r.NumCtx);
            Assert.Equal(_options.Llm.KeepAlive, r.KeepAlive);
            Assert.Empty(r.Stop);
        });
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        EnqueueProbes();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().RunAsync(cts.Token));
    }
}
