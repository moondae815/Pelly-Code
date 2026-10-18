using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Sql;
using System.Security.Cryptography;

namespace PellyCoder.Core.Doctor;

public enum DoctorStatus { Ok, Warn, Fail }

public sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail, string? Action);

/// <summary>Facts the CLI passes in so Core never reads the environment: config file locations, <c>%ProgramFiles(x86)%</c>, optional <c>--project</c>.</summary>
public sealed record DoctorContext(string? UserConfigPath, string WorkspaceConfigPath, string? ProgramFilesX86 = null, string? ProjectPath = null);

/// <summary>Runs the environment checks behind <c>pelly doctor</c> (design §8.13, M1 scope).</summary>
public sealed class DoctorService(
    ILlmBackend backend,
    IOllamaDiagnostics diagnostics,
    QwenChatTemplate template,
    ITokenCounter tokens,
    PellyOptions options,
    TimeProvider time,
    IFileSystem fs,
    IProcessRunner processes,
    Func<bool>? isWindows = null,
    ISqlExecutor? sql = null,
    DoctorContext? context = null)
{
    private static readonly TimeSpan DotnetTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<bool> _isWindows = isWindows ?? OperatingSystem.IsWindows;

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

        checks.Add(new("pelly", DoctorStatus.Ok, $"{PellyInfo.Version}, 토크나이저 {TokenizerHash()}", null));
        checks.Add(CheckSettings());
        checks.Add(await CheckDotnetAsync(ct));
        checks.Add(await CheckMsBuildAsync(ct));

        if (context?.ProjectPath is { } project)
            checks.Add(TargetingPackCheck.Run(fs, _isWindows, context.ProgramFilesX86, project));

        checks.Add(new("서버 설정 확인 필요", DoctorStatus.Warn,
            "서버에서 직접 확인할 수 없음: OLLAMA_FLASH_ATTENTION, OLLAMA_KV_CACHE_TYPE, OLLAMA_NUM_PARALLEL, OLLAMA_KEEP_ALIVE. 아래 체크리스트를 서버 관리자와 확인하세요",
            "서버 관리자에게 PRD §7.1 값을 확인하세요"));
        checks.Add(await CheckSqlAsync(ct));
        return checks;
    }

    /// <summary>SHA-256 over the embedded vocab.json bytes then merges.txt bytes, first 12 lowercase hex chars.</summary>
    private static string TokenizerHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in new[] { "vocab.json", "merges.txt" })
        {
            using var stream = typeof(DoctorService).Assembly.GetManifestResourceStream("PellyCoder.Core.Llm.Resources." + file)
                ?? throw new InvalidOperationException($"Embedded resource not found: {file}");
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
    }

    private DoctorCheck CheckSettings()
    {
        var lines = new List<string>();
        if (context is not null)
        {
            lines.Add(context.UserConfigPath is { } user
                ? $"사용자 설정: {user} ({(fs.File.Exists(user) ? "있음" : "없음")})"
                : "사용자 설정: 경로를 알 수 없음");
            lines.Add($"작업 공간 설정: {context.WorkspaceConfigPath} ({(fs.File.Exists(context.WorkspaceConfigPath) ? "있음" : "없음")})");
        }
        var cs = options.Sql.ConnectionString;
        lines.Add($"llm.endpoint={options.Llm.Endpoint}, llm.model={options.Llm.Model}, llm.thinkMode={options.Llm.ThinkMode}");
        lines.Add($"budget.inputTotal={options.Budget.InputTotal}, retry.max={options.Retry.Max}, " +
                  $"build.timeoutSeconds={options.Build.TimeoutSeconds}, test.timeoutSeconds={options.Test.TimeoutSeconds}");
        lines.Add($"sql.compatLevel={options.Sql.CompatLevel}, sql.connectionString={(string.IsNullOrWhiteSpace(cs) ? "(없음)" : SqlConnectionText.Mask(cs))}");
        return new("설정", DoctorStatus.Ok, string.Join('\n', lines), null);
    }

    private async Task<DoctorCheck> CheckSqlAsync(CancellationToken ct)
    {
        const string name = "SQL 연결";
        var cs = options.Sql.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            return new(name, DoctorStatus.Warn, "pelly sql을 쓰려면 sql.connectionString 또는 PELLY_SQL_CONNECTION을 설정하세요", null);

        var endpoint = SqlConnectionText.Endpoint(cs);
        var check = SqlTargetPolicy.Check(endpoint, options.Sql);
        if (!check.Allowed)
            return new(name, DoctorStatus.Fail, check.Message ?? "허용되지 않은 SQL 대상", "sql.allowedHosts·sql.allowedDatabases 설정을 확인하세요");
        if (sql is null)
            return new(name, DoctorStatus.Warn, "SQL 점검기가 없어 확인하지 못함", null);

        try
        {
            var probe = await sql.ProbeAsync(ct);
            if (!probe.CanViewDefinition)
                return new(name, DoctorStatus.Fail, "VIEW DEFINITION 권한 필요", "접속 계정에 VIEW DEFINITION 권한을 부여하세요");
            var firstLine = probe.Version.Split('\n', 2)[0].Trim();
            return new(name, DoctorStatus.Ok, $"{endpoint.DataSource}/{endpoint.Database}, {firstLine}", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var text = SqlConnectionText.MaskInText(ex.Message, cs);
            return new(name, DoctorStatus.Fail, $"{endpoint.DataSource}/{endpoint.Database} 에 연결할 수 없음: {text}",
                "연결 문자열과 서버 상태를 확인하세요");
        }
    }

    private async Task<DoctorCheck> CheckDotnetAsync(CancellationToken ct)
    {
        const string name = ".NET SDK";
        var fail = new DoctorCheck(name, DoctorStatus.Fail, "dotnet을 실행할 수 없습니다", ".NET 10 SDK를 설치하고 PATH를 확인하세요");
        try
        {
            var result = await processes.RunAsync(
                new ProcessSpec("dotnet", ["--version"], fs.Directory.GetCurrentDirectory(), DotnetTimeout), ct);
            var version = result.StdOut.Trim();
            return result.ExitCode == 0 && !result.TimedOut && version.Length > 0
                ? new(name, DoctorStatus.Ok, version, null)
                : fail;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fail; // e.g. Win32Exception when dotnet is not on PATH
        }
    }

    private async Task<DoctorCheck> CheckMsBuildAsync(CancellationToken ct)
    {
        const string name = "MSBuild (레거시)";
        if (!_isWindows())
            return new(name, DoctorStatus.Warn, "Windows에서만 확인합니다", null);
        var resolution = await new BuildToolResolver(fs, processes, options.Build, _isWindows).ResolveAsync(ProjectStyle.Legacy, ct);
        return resolution.Tool is { } tool
            ? new(name, DoctorStatus.Ok, tool.Executable, null)
            : new(name, DoctorStatus.Warn, resolution.Error ?? "MSBuild.exe를 찾을 수 없습니다",
                "레거시 .NET Framework 프로젝트를 고치려면 Visual Studio Build Tools가 필요합니다");
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
