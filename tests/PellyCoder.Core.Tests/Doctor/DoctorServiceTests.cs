using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Sql;
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

    private FakeSqlExecutor? _sql;
    private DoctorContext? _context;

    private DoctorService Create(int localCount = 12)
    {
        if (_processes.Calls.Count == 0 && !_dotnetQueued)
            _processes.Enqueue(Proc(0, "10.0.100\n"));
        return new(_backend, _diag, _template, new FixedCounter(localCount), _options, _time, _fs, _processes, () => _windows, _sql, _context);
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
            ["Ollama 서버", "모델", "렌더러", "템플릿 일치", "토크나이저 일치", "응답 시간", "pelly", "설정", ".NET SDK", "MSBuild (레거시)", "서버 설정 확인 필요", "SQL 연결"],
            checks.Select(c => c.Name));
        Assert.All(checks.Take(9), c => Assert.Equal(DoctorStatus.Ok, c.Status));
        Assert.Equal(DoctorStatus.Warn, checks[9].Status);
        var settings = checks[10];
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

    private void SqlOptionsAllowed(string cs = "Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=Secret!1")
    {
        _options.Sql.ConnectionString = cs;
        _options.Sql.AllowedHosts.Add("localhost");
        _options.Sql.AllowedDatabases.Add("PellyCorpus");
        _sql = new FakeSqlExecutor();
    }

    [Fact]
    public async Task Pelly_RowHasVersionAndTokenizerHash()
    {
        EnqueueProbes();
        var asm = typeof(DoctorService).Assembly;
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var file in new[] { "vocab.json", "merges.txt" })
        {
            using var stream = asm.GetManifestResourceStream("PellyCoder.Core.Llm.Resources." + file)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            sha.AppendData(ms.ToArray());
        }
        var expected = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant()[..12];

        var c = Get(await Create().RunAsync(CancellationToken.None), "pelly");

        Assert.Equal(DoctorStatus.Ok, c.Status);
        Assert.Matches("^[0-9a-f]{12}$", expected);
        Assert.Equal($"{PellyInfo.Version}, 토크나이저 {expected}", c.Detail);
    }

    [Fact]
    public async Task Settings_RowMasksConnection()
    {
        EnqueueProbes();
        SqlOptionsAllowed("Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=Secret!1");
        _options.Llm.Headers["Authorization"] = "Bearer hdr-token-xyz";
        _context = new DoctorContext("/home/u/pelly.json", "/w/pelly.json");
        _fs.AddFile("/w/pelly.json", new System.IO.Abstractions.TestingHelpers.MockFileData("{}"));

        var c = Get(await Create().RunAsync(CancellationToken.None), "설정");

        Assert.Equal(DoctorStatus.Ok, c.Status);
        Assert.Contains("/home/u/pelly.json (없음)", c.Detail);
        Assert.Contains("/w/pelly.json (있음)", c.Detail);
        Assert.Contains("llm.thinkMode=Retry", c.Detail);
        Assert.Contains("sql.compatLevel=160", c.Detail);
        Assert.Contains("password=***", c.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", c.Detail);
        Assert.DoesNotContain("hdr-token-xyz", c.Detail);
    }

    private const string SdkProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    private const string LegacyProject = "<Project ToolsVersion=\"15.0\"><PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup></Project>";

    private void WithProject(string xml, string? programFilesX86 = null)
    {
        _fs.AddFile("/w/a.csproj", new System.IO.Abstractions.TestingHelpers.MockFileData(xml));
        _context = new DoctorContext(null, "/w/pelly.json", programFilesX86, "/w/a.csproj");
    }

    [Fact]
    public async Task TargetingPack_Sdk_Ok()
    {
        EnqueueProbes();
        WithProject(SdkProject);

        var c = Get(await Create().RunAsync(CancellationToken.None), "타겟팅 팩");

        Assert.Equal(DoctorStatus.Ok, c.Status);
        Assert.Equal("SDK 스타일 — NuGet 참조 어셈블리 사용", c.Detail);
    }

    [Fact]
    public async Task TargetingPack_LegacyOnMac_Warn()
    {
        EnqueueProbes();
        WithProject(LegacyProject);
        _windows = false;

        var c = Get(await Create().RunAsync(CancellationToken.None), "타겟팅 팩");

        Assert.Equal(DoctorStatus.Warn, c.Status);
        Assert.Equal("Windows에서만 확인합니다", c.Detail);
    }

    [Fact]
    public async Task TargetingPack_LegacyMissingFolder_Fail()
    {
        EnqueueProbes();
        WithProject(LegacyProject, "/pf86");
        _windows = true;

        var c = Get(await Create().RunAsync(CancellationToken.None), "타겟팅 팩");

        Assert.Equal(DoctorStatus.Fail, c.Status);
        Assert.Equal("Developer Pack을 설치하거나 Microsoft.NETFramework.ReferenceAssemblies.net48 패키지를 쓰세요", c.Action);
    }

    [Fact]
    public async Task TargetingPack_LegacyFolderPresent_Ok()
    {
        EnqueueProbes();
        WithProject(LegacyProject, "/pf86");
        _windows = true;
        _fs.AddDirectory(_fs.Path.Combine("/pf86", "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8"));

        var c = Get(await Create().RunAsync(CancellationToken.None), "타겟팅 팩");

        Assert.Equal(DoctorStatus.Ok, c.Status);
    }

    [Fact]
    public async Task NoProject_NoTargetingRow()
    {
        EnqueueProbes();

        var checks = await Create().RunAsync(CancellationToken.None);

        Assert.DoesNotContain(checks, c => c.Name == "타겟팅 팩");
    }

    [Fact]
    public async Task Sql_QuotedPasswordWithSemicolon_Masked()
    {
        EnqueueProbes();
        SqlOptionsAllowed();
        _sql!.ThrowOnProbe(new InvalidOperationException("login failed for Password=\"a;secret\";Encrypt=True"));

        var c = Get(await Create().RunAsync(CancellationToken.None), "SQL 연결");

        Assert.Equal(DoctorStatus.Fail, c.Status);
        Assert.DoesNotContain("secret", c.Detail);
    }

    [Fact]
    public async Task Sql_NoConnection_Warn()
    {
        EnqueueProbes();

        var c = Get(await Create().RunAsync(CancellationToken.None), "SQL 연결");

        Assert.Equal(DoctorStatus.Warn, c.Status);
        Assert.Equal("pelly sql을 쓰려면 sql.connectionString 또는 PELLY_SQL_CONNECTION을 설정하세요", c.Detail);
    }

    [Fact]
    public async Task Sql_NotAllowed_Fail()
    {
        EnqueueProbes();
        SqlOptionsAllowed();
        _options.Sql.AllowedHosts.Clear();

        var c = Get(await Create().RunAsync(CancellationToken.None), "SQL 연결");

        Assert.Equal(DoctorStatus.Fail, c.Status);
        Assert.Contains("sql.allowedHosts", c.Detail);
        Assert.DoesNotContain("Secret", c.Detail);
        Assert.DoesNotContain("Probe", _sql!.Calls);
    }

    [Fact]
    public async Task Sql_ProbeFails_Fail_Masked()
    {
        EnqueueProbes();
        SqlOptionsAllowed();
        _sql!.ThrowOnProbe(new InvalidOperationException("login failed for Password=Secret!1;"));

        var c = Get(await Create().RunAsync(CancellationToken.None), "SQL 연결");

        Assert.Equal(DoctorStatus.Fail, c.Status);
        Assert.DoesNotContain("Secret", c.Detail + c.Action);
    }

    [Fact]
    public async Task Sql_NoViewDefinition_Fail()
    {
        EnqueueProbes();
        SqlOptionsAllowed();
        _sql!.Probe = new SqlServerProbe("Microsoft SQL Server 2022\nx", "PellyCorpus", false);

        var c = Get(await Create().RunAsync(CancellationToken.None), "SQL 연결");

        Assert.Equal(DoctorStatus.Fail, c.Status);
        Assert.Equal("VIEW DEFINITION 권한 필요", c.Detail);
    }

    [Fact]
    public async Task Sql_Ok_ShowsTarget()
    {
        EnqueueProbes();
        SqlOptionsAllowed();
        _sql!.Probe = new SqlServerProbe("Microsoft SQL Server 2022\n\tCopyright", "PellyCorpus", true);

        var c = Get(await Create().RunAsync(CancellationToken.None), "SQL 연결");

        Assert.Equal(DoctorStatus.Ok, c.Status);
        Assert.Equal("localhost/PellyCorpus, Microsoft SQL Server 2022", c.Detail);
        Assert.DoesNotContain("Secret", c.Detail);
    }

    [Fact]
    public async Task Facts_FilledPerRow_SurviveSanitizer()
    {
        EnqueueProbes();
        _windows = true;
        _options.Build.MsbuildPath = "/tools/MSBuild.exe";
        _fs.AddFile("/tools/MSBuild.exe", new MockFileData(""));
        WithProject(LegacyProject, "/pf86");
        _fs.AddDirectory(_fs.Path.Combine("/pf86", "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8"));
        SqlOptionsAllowed();
        _sql!.Probe = new SqlServerProbe("Microsoft SQL Server 2022 (RTM) - 16.0.1000.6 (X64)", "PellyCorpus", true);
        _backend.BeforeRespond = _ => { _time.Advance(TimeSpan.FromMilliseconds(40)); return Task.CompletedTask; };

        var checks = await Create().RunAsync(CancellationToken.None);
        using var doc = System.Text.Json.JsonDocument.Parse(DoctorSanitizer.ToJson(checks));
        var rows = doc.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(r => r.GetProperty("Name").GetString()!);

        void Has(string row, params string[] keys)
        {
            var facts = rows[row].GetProperty("Facts");
            foreach (var key in keys) Assert.True(facts.TryGetProperty(key, out _), $"{row}/{key}");
        }
        Has(DoctorFacts.OllamaServer, DoctorFacts.OllamaVersion, DoctorFacts.LatencyMs);
        Has(DoctorFacts.Model, DoctorFacts.Loaded);
        Has(DoctorFacts.TokenizerMatch, DoctorFacts.Match);
        Has(DoctorFacts.Latency, DoctorFacts.TtftMs);
        Has(DoctorFacts.Pelly, DoctorFacts.PellyVersion, DoctorFacts.TokenizerHash);
        Has(DoctorFacts.DotnetSdk, DoctorFacts.DotnetVersion);
        Has(DoctorFacts.MsBuild, DoctorFacts.Found);
        Has(DoctorFacts.TargetingPack, DoctorFacts.FrameworkVersion, DoctorFacts.Installed);
        Has(DoctorFacts.Sql, DoctorFacts.CanViewDefinition, DoctorFacts.ServerMajor);
        string V(string row, string key) => rows[row].GetProperty("Facts").GetProperty(key).GetString()!;
        Assert.Equal("16", V(DoctorFacts.Sql, DoctorFacts.ServerMajor));
        Assert.Equal("true", V(DoctorFacts.Sql, DoctorFacts.CanViewDefinition));
        Assert.Equal("0.40.0", V(DoctorFacts.OllamaServer, DoctorFacts.OllamaVersion));
        Assert.Matches("^[0-9]+$", V(DoctorFacts.OllamaServer, DoctorFacts.LatencyMs));
        Assert.Equal("true", V(DoctorFacts.Model, DoctorFacts.Loaded));
        Assert.Equal("true", V(DoctorFacts.TokenizerMatch, DoctorFacts.Match));
        Assert.Matches("^[0-9]+$", V(DoctorFacts.Latency, DoctorFacts.TtftMs));
        Assert.Equal(PellyInfo.Version, V(DoctorFacts.Pelly, DoctorFacts.PellyVersion));
        Assert.Matches("^[0-9a-f]{12}$", V(DoctorFacts.Pelly, DoctorFacts.TokenizerHash));
        Assert.Equal("10.0.100", V(DoctorFacts.DotnetSdk, DoctorFacts.DotnetVersion));
        Assert.Equal("true", V(DoctorFacts.MsBuild, DoctorFacts.Found));
        Assert.Equal("v4.8", V(DoctorFacts.TargetingPack, DoctorFacts.FrameworkVersion));
        Assert.Equal("true", V(DoctorFacts.TargetingPack, DoctorFacts.Installed));
    }

    [Fact]
    public async Task Facts_NullOllamaVersionAndUnparsableSqlVersion_AreOmitted()
    {
        EnqueueProbes();
        _backend.Status = new LlmServerStatus(true, null, true, true, "qwen3.5", TimeSpan.FromMilliseconds(1), null);
        SqlOptionsAllowed();
        _sql!.Probe = new SqlServerProbe("Microsoft SQL Server (fake)", "PellyCorpus", true);

        var checks = await Create().RunAsync(CancellationToken.None);

        Assert.DoesNotContain(DoctorFacts.OllamaVersion, Get(checks, DoctorFacts.OllamaServer).Facts!.Keys);
        Assert.DoesNotContain(DoctorFacts.ServerMajor, Get(checks, DoctorFacts.Sql).Facts!.Keys);
    }
}
