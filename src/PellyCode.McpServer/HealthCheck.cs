using System.IO.Abstractions;
using PellyCode.Core.Abstractions;
using PellyCode.Core.Doctor;
using PellyCode.Core.Impact;
using PellyCode.Core.Processes;
using PellyCode.Core.Sql;
using PellyCode.McpServer.Baseline;

namespace PellyCode.McpServer;

/// <summary>
/// <c>PellyCodeMcpServer --check</c>: a self-test printed to <paramref name="output"/> without starting MCP. The .NET SDK, MSBuild,
/// BuildHost and SQL rows use the same judgement as <c>pelly doctor</c>. Builds no baseline: it only reads what is on disk.
/// </summary>
public static class HealthCheck
{
    private const string GitPrefix = "git 저장소가 아닙니다";

    /// <summary>0 when no row failed, 1 otherwise. The trailing parameters default to the real machine; tests pass fakes.</summary>
    public static async Task<int> RunAsync(ServerContext? ctx, string? contextError, BaselineService? baseline,
        IReadOnlyList<string> tools, TextWriter output, CancellationToken ct, string? baseDir = null,
        IProcessRunner? processes = null, IFileSystem? fs = null, Func<bool>? isWindows = null, ISqlExecutor? sql = null)
    {
        fs ??= new FileSystem();
        processes ??= new SystemProcessRunner(TimeProvider.System);
        isWindows ??= OperatingSystem.IsWindows;
        baseDir ??= AppContext.BaseDirectory;
        var rows = new List<DoctorCheck>();

        // The root: TryCreate fails first on a missing repository, then on settings; an options error means the repository was found.
        var notGit = ctx is null && contextError?.StartsWith(GitPrefix, StringComparison.Ordinal) == true;
        rows.Add(ctx is not null ? new("루트·git", DoctorStatus.Ok, ctx.Root, null)
            : notGit ? new("루트·git", DoctorStatus.Fail, contextError!, "--root로 git 저장소 폴더를 주거나 그 안에서 실행하세요")
            : new("루트·git", DoctorStatus.Ok, "git 저장소 확인됨", null));
        rows.Add(ctx is not null ? new("설정", DoctorStatus.Ok, SettingsText(ctx), null)
            : notGit ? new("설정", DoctorStatus.Warn, "루트가 없어 건너뜀", null)
            : new("설정", DoctorStatus.Fail, contextError ?? "설정을 읽지 못했습니다", "pelly.json과 사용자 설정 파일을 확인하세요"));

        rows.Add(await DoctorChecks.DotnetAsync(fs, processes, ct));
        rows.Add(await DoctorChecks.MsBuildAsync(fs, processes, ctx?.Options.Build ?? new(), isWindows, ct));
        rows.Add(await SemanticCheck.RunAsync(fs, new NoLoader(), baseDir, null, ct));

        var cs = ctx?.Options.Sql.ConnectionString;
        rows.Add(ctx is null ? new("SQL", DoctorStatus.Warn, "설정을 읽지 못해 건너뜀", null)
            : string.IsNullOrWhiteSpace(cs) ? new("SQL", DoctorStatus.Ok, "설정 안 됨 (SQL 검사는 건너뜁니다)", null)
            : await DoctorChecks.SqlAsync(ctx.Options, sql ?? new SqlServerExecutor(cs), ct));

        rows.Add(new("도구", DoctorStatus.Ok, $"{tools.Count}개: {string.Join(", ", tools)}", null));
        rows.Add(await BaselineRowAsync(baseline, ct));

        output.WriteLine("PellyCodeMcpServer 점검");
        foreach (var r in rows)
        {
            var mark = r.Status switch { DoctorStatus.Ok => "[정상]", DoctorStatus.Warn => "[주의]", _ => "[실패]" };
            var lines = r.Detail.Split('\n');
            output.WriteLine($"{mark} {r.Name}: {lines[0].TrimEnd('\r')}");
            foreach (var l in lines.Skip(1)) output.WriteLine($"       {l.TrimEnd('\r')}");
            if (r.Status != DoctorStatus.Ok && r.Action is not null) output.WriteLine($"       -> {r.Action}");
        }
        var failed = rows.Count(r => r.Status == DoctorStatus.Fail);
        output.WriteLine(failed == 0 ? "결과: 모든 항목 통과" : $"결과: 실패 {failed}건");
        return failed == 0 ? 0 : 1;
    }

    private static string SettingsText(ServerContext ctx)
    {
        var cs = ctx.Options.Sql.ConnectionString;
        return $"sql.connectionString={(string.IsNullOrWhiteSpace(cs) ? "(없음)" : SqlConnectionText.Mask(cs))}, " +
               $"build.timeoutSeconds={ctx.Options.Build.TimeoutSeconds}, test.timeoutSeconds={ctx.Options.Test.TimeoutSeconds}";
    }

    private static async Task<DoctorCheck> BaselineRowAsync(BaselineService? service, CancellationToken ct)
    {
        const string name = "HEAD 기준";
        if (service is null) return new(name, DoctorStatus.Ok, "기록 없음 (루트가 없어 만들 수 없음)", null);
        var b = await service.LastOrCachedAsync(ct);
        if (b is null)
            return new(name, DoctorStatus.Ok, "기록 없음 (실패한 기준은 저장하지 않으므로, 실패했다면 재시작 뒤 여기에 보이지 않습니다)", null);
        var head = b.HeadSha.Length > 12 ? b.HeadSha[..12] : b.HeadSha;
        var text = $"HEAD {head}, 상태 {b.Status}" + (b.FailureReason is null ? "" : $", 이유: {b.FailureReason}");
        return new(name, b.Status == BaselineStatus.Failed ? DoctorStatus.Warn : DoctorStatus.Ok, text, null);
    }

    /// <summary>SemanticCheck only loads a project when one is given; --check gives none.</summary>
    private sealed class NoLoader : ISemanticLoader
    {
        public Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct) => throw new NotSupportedException();
    }
}
