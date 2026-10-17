using System.IO.Abstractions.TestingHelpers;
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

    private readonly ScriptedProcessRunner _processes = new();
    private readonly MockFileSystem _fs = new();
    private bool _windows;

    private DoctorService Create(int localCount = 12)
    {
        if (_processes.Calls.Count == 0 && !_dotnetQueued)
            _processes.Enqueue(Proc(0, "10.0.100\n"));
        return new(_backend, _diag, _template, new FixedCounter(localCount), _options, _time, _fs, _processes, () => _windows);
    }

    private bool _dotnetQueued;

    private static ProcessResult Proc(int exit, string stdout) =>
        new(exit, stdout, "", System.Text.Encoding.UTF8.GetBytes(stdout), TimeSpan.Zero, false);

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
            ["Ollama 서버", "모델", "렌더러", "템플릿 일치", "토크나이저 일치", "응답 시간", ".NET SDK", "MSBuild (레거시)", "서버 설정 확인 필요"],
            checks.Select(c => c.Name));
        Assert.All(checks.Take(7), c => Assert.Equal(DoctorStatus.Ok, c.Status));
        Assert.Equal(DoctorStatus.Warn, checks[7].Status);
        var settings = checks[8];
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

    [Fact]
    public async Task DotnetVersion_Ok()
    {
        EnqueueProbes();
        _processes.Enqueue(Proc(0, "10.0.100\r\n"));
        _dotnetQueued = true;

        var checks = await Create().RunAsync(CancellationToken.None);

        var c = Get(checks, ".NET SDK");
        Assert.Equal(DoctorStatus.Ok, c.Status);
        Assert.Equal("10.0.100", c.Detail);
        var call = _processes.Calls[0];
        Assert.Equal("dotnet", call.FileName);
        Assert.Equal(["--version"], call.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(10), call.Timeout);
    }

    [Fact]
    public async Task DotnetMissing_Fail()
    {
        EnqueueProbes();
        _processes.Enqueue(Proc(1, ""));
        _dotnetQueued = true;

        var checks = await Create().RunAsync(CancellationToken.None);

        var c = Get(checks, ".NET SDK");
        Assert.Equal(DoctorStatus.Fail, c.Status);
        Assert.Equal("dotnet을 실행할 수 없습니다", c.Detail);
        Assert.Equal(".NET 10 SDK를 설치하고 PATH를 확인하세요", c.Action);
    }

    [Fact]
    public async Task DotnetThrows_Fail()
    {
        EnqueueProbes();
        _processes.EnqueueThrow(new System.ComponentModel.Win32Exception("not found"));
        _dotnetQueued = true;

        var checks = await Create().RunAsync(CancellationToken.None);

        Assert.Equal(DoctorStatus.Fail, Get(checks, ".NET SDK").Status);
    }

    [Fact]
    public async Task MsBuild_NonWindows_Warn()
    {
        EnqueueProbes();
        _windows = false;

        var checks = await Create().RunAsync(CancellationToken.None);

        var c = Get(checks, "MSBuild (레거시)");
        Assert.Equal(DoctorStatus.Warn, c.Status);
        Assert.Equal("Windows에서만 확인합니다", c.Detail);
    }

    [Fact]
    public async Task MsBuild_WindowsResolved_Ok()
    {
        EnqueueProbes();
        _windows = true;
        _options.Build.MsbuildPath = "/tools/MSBuild.exe";
        _fs.AddFile("/tools/MSBuild.exe", new MockFileData(""));

        var checks = await Create().RunAsync(CancellationToken.None);

        var c = Get(checks, "MSBuild (레거시)");
        Assert.Equal(DoctorStatus.Ok, c.Status);
        Assert.Equal("/tools/MSBuild.exe", c.Detail);
    }

    [Fact]
    public async Task MsBuild_WindowsMissing_Warn()
    {
        EnqueueProbes();
        _windows = true;

        var checks = await Create().RunAsync(CancellationToken.None);

        var c = Get(checks, "MSBuild (레거시)");
        Assert.Equal(DoctorStatus.Warn, c.Status);
        Assert.Contains("MSBuild.exe를 찾을 수 없습니다", c.Detail);
        Assert.Equal("레거시 .NET Framework 프로젝트를 고치려면 Visual Studio Build Tools가 필요합니다", c.Action);
    }
}
