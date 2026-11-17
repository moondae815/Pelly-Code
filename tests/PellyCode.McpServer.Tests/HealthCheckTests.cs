using System.Diagnostics;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using PellyCode.Core.Builds;
using PellyCode.Core.Config;
using PellyCode.Core.Models;
using PellyCode.Core.Processes;
using PellyCode.Core.Specs;
using PellyCode.Core.Testing;
using PellyCode.Core.Workspaces;
using PellyCode.McpServer.Baseline;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Tests;

public sealed class HealthCheckTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "pelly-health-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Tools = ["pelly_impact", "pelly_verify"];

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string HostDir(bool complete)
    {
        var dir = Path.Combine(_base, "host");
        foreach (var f in complete ? new[] { "BuildHost-net472", "BuildHost-netcore" } : ["BuildHost-netcore"])
        {
            Directory.CreateDirectory(Path.Combine(dir, f));
            File.WriteAllText(Path.Combine(dir, f, "x.dll"), "x");
        }
        return dir;
    }

    private ServerContext Context()
    {
        var root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(root);
        return new ServerContext(root, new PellyOptions(), new Workspace(root, Path.Combine(root, ".pelly")));
    }

    [Fact]
    public async Task NotGitRepo_Fails()
    {
        var output = new StringWriter();

        var code = await HealthCheck.RunAsync(null, "git 저장소가 아닙니다: /x/y", null, Tools, output, Ct, HostDir(true));

        Assert.Equal(1, code);
        Assert.Contains("git 저장소가 아닙니다: /x/y", output.ToString());
    }

    [Fact]
    public async Task MissingBuildHost_Fails()
    {
        var output = new StringWriter();

        var code = await HealthCheck.RunAsync(Context(), null, null, Tools, output, Ct, HostDir(false));

        Assert.Equal(1, code);
        Assert.Contains("BuildHost-net472", output.ToString());
    }

    [Fact]
    public async Task ListsTwoTools()
    {
        var output = new StringWriter();

        await HealthCheck.RunAsync(Context(), null, null, Tools, output, Ct, HostDir(true));

        var text = output.ToString();
        Assert.Contains("pelly_impact", text);
        Assert.Contains("pelly_verify", text);
        Assert.Contains("2개", text);
    }

    [Fact]
    public async Task ShowsLastBaselineFailureReason()
    {
        var repo = Path.Combine(_base, "repo");
        Directory.CreateDirectory(repo);
        Run(repo, "init", "-q");
        Directory.CreateDirectory(Path.Combine(repo, "App"));
        File.WriteAllText(Path.Combine(repo, "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Run(repo, "add", "-A");
        Run(repo, "-c", "user.name=t", "-c", "user.email=t@e.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "c");
        var ctx = Context();
        var service = new BaselineService(new FileSystem(), repo, Path.Combine(_base, "mcp-base", "abc"),
            new GitRepo(new SystemProcessRunner(TimeProvider.System), repo), new FailingVerifier(), new NoTests(), new TestOptions(),
            TimeProvider.System, NullLogger<BaselineService>.Instance, new NoRestore());
        await service.GetAsync(["App/App.csproj"], TimeSpan.FromSeconds(20), null, Ct);
        var output = new StringWriter();

        await HealthCheck.RunAsync(ctx, null, service, Tools, output, Ct, HostDir(true));
        await service.StopAsync(CancellationToken.None);

        Assert.Contains("HEAD", output.ToString());
        Assert.Contains("boom-reason", output.ToString());
    }

    [Fact]
    public async Task NoBaselineRecorded_SaysSo()
    {
        var output = new StringWriter();

        await HealthCheck.RunAsync(Context(), null, null, Tools, output, Ct, HostDir(true));

        Assert.Contains("기록 없음", output.ToString());
    }

    [Fact]
    public async Task LogFile_ReceivesStartupFailure()
    {
        var repo = Path.Combine(_base, "badrepo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, "pelly.json"), "{ not json");
        var log = Path.Combine(_base, "logs", "server.log");
        var dll = Path.Combine(AppContext.BaseDirectory, "PellyCodeMcpServer.dll");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { dll, "--root", repo, "--log-file", log, "--check" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync(Ct);
        await p.StandardError.ReadToEndAsync(Ct);
        await p.WaitForExitAsync(Ct);

        Assert.Equal(1, p.ExitCode);
        var logged = File.ReadAllText(log);
        Assert.Contains("pelly.json", logged);
        Assert.Contains("pelly.json", stdout);
    }

    private static void Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private sealed class FailingVerifier : IGroupVerifier
    {
        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct) =>
            Task.FromResult(new GroupBuild(false, "boom-reason", null, []));
    }

    private sealed class NoTests : IBaselineTestRunner
    {
        public Task<TestRun> RunAsync(string testProject, string workDir, string trxPath, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoRestore : IBaselineRestorer
    {
        public Task<string?> RestoreAsync(string project, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
