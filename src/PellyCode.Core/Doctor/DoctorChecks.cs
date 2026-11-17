using System.IO.Abstractions;
using PellyCode.Core.Abstractions;
using PellyCode.Core.Builds;
using PellyCode.Core.Config;
using PellyCode.Core.Sql;

namespace PellyCode.Core.Doctor;

/// <summary>
/// The doctor's judgement of the .NET SDK, MSBuild and the development SQL connection, shared by <c>pelly doctor</c> and
/// <c>PellyCodeMcpServer --check</c> so both tools agree.
/// </summary>
public static class DoctorChecks
{
    private static readonly TimeSpan DotnetTimeout = TimeSpan.FromSeconds(10);

    public static async Task<DoctorCheck> SqlAsync(PellyOptions options, ISqlExecutor? sql, CancellationToken ct)
    {
        const string name = DoctorFacts.Sql;
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
                return new(name, DoctorStatus.Fail, "VIEW DEFINITION 권한 필요", "접속 계정에 VIEW DEFINITION 권한을 부여하세요",
                    Facts((DoctorFacts.CanViewDefinition, "false"), (DoctorFacts.ServerMajor, ServerMajor(probe.Version)),
                        (DoctorFacts.AuthMode, SqlConnectionText.UsesIntegratedSecurity(cs) ? "windows" : "sql")));
            var firstLine = probe.Version.Split('\n', 2)[0].Trim();
            var windows = SqlConnectionText.UsesIntegratedSecurity(cs);
            var login = string.IsNullOrWhiteSpace(probe.LoginName) ? "" : $" ({probe.LoginName.Trim()})";
            var auth = (windows ? "Windows 인증" : "SQL 로그인") + login;
            return new(name, DoctorStatus.Ok, $"{endpoint.DataSource}/{endpoint.Database}, {auth}, {firstLine}", null,
                Facts((DoctorFacts.CanViewDefinition, "true"), (DoctorFacts.ServerMajor, ServerMajor(probe.Version)),
                    (DoctorFacts.AuthMode, windows ? "windows" : "sql")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var text = SqlConnectionText.MaskInText(ex.Message, cs);
            return new(name, DoctorStatus.Fail, $"{endpoint.DataSource}/{endpoint.Database} 에 연결할 수 없음: {text}",
                "연결 문자열과 서버 상태를 확인하세요");
        }
    }

    public static async Task<DoctorCheck> DotnetAsync(IFileSystem fs, IProcessRunner processes, CancellationToken ct)
    {
        const string name = DoctorFacts.DotnetSdk;
        var fail = new DoctorCheck(name, DoctorStatus.Fail, "dotnet을 실행할 수 없습니다", ".NET 10 SDK를 설치하고 PATH를 확인하세요");
        try
        {
            var result = await processes.RunAsync(
                new ProcessSpec("dotnet", ["--version"], fs.Directory.GetCurrentDirectory(), DotnetTimeout, BuildVerifier.DotnetChildEnv), ct);
            var version = result.StdOut.Trim();
            return result.ExitCode == 0 && !result.TimedOut && version.Length > 0
                ? new(name, DoctorStatus.Ok, version, null, Facts((DoctorFacts.DotnetVersion, version)))
                : fail;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fail; // e.g. Win32Exception when dotnet is not on PATH
        }
    }

    public static async Task<DoctorCheck> MsBuildAsync(IFileSystem fs, IProcessRunner processes, BuildOptions build, Func<bool> isWindows, CancellationToken ct)
    {
        const string name = DoctorFacts.MsBuild;
        if (!isWindows())
            return new(name, DoctorStatus.Warn, "Windows에서만 확인합니다", null);
        var resolution = await new BuildToolResolver(fs, processes, build, isWindows).ResolveAsync(ProjectStyle.Legacy, ct);
        return resolution.Tool is { } tool
            ? new(name, DoctorStatus.Ok, tool.Executable, null, Facts((DoctorFacts.Found, "true")))
            : new(name, DoctorStatus.Warn, resolution.Error ?? "MSBuild.exe를 찾을 수 없습니다",
                "레거시 .NET Framework 프로젝트를 고치려면 Visual Studio Build Tools가 필요합니다",
                Facts((DoctorFacts.Found, "false")));
    }

    private static IReadOnlyDictionary<string, string> Facts(params (string Key, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrEmpty(p.Value)).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);

    /// <summary>Major number of the 4-part build version in <c>@@VERSION</c> (e.g. <c>16.0.4135.4</c> → <c>16</c>); empty when absent.</summary>
    private static string ServerMajor(string version)
    {
        var m = System.Text.RegularExpressions.Regex.Match(version, @"(?<![0-9.])([0-9]{1,2})\.[0-9]+\.[0-9]+\.[0-9]+");
        return m.Success ? m.Groups[1].Value : "";
    }
}
