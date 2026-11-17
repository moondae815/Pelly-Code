using System.IO.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using PellyCode.Core.Builds;
using PellyCode.Core.Config;
using PellyCode.Core.Processes;
using PellyCode.Core.Specs;
using PellyCode.Core.Testing;
using PellyCode.Core.Workspaces;
using PellyCode.McpServer.Baseline;
using PellyCode.McpServer.Git;
using PellyCode.McpServer.Verify;

namespace PellyCode.McpServer.Tests;

/// <summary>pelly_verify with real git and dotnet over a temp copy of eval/corpus/Modern (built the way Program.cs builds it).</summary>
public sealed class VerifyCsIntegrationTests : IDisposable
{
    private const string ServiceFile = "Modern/Shop.Api/Services/OrderService.cs";

    private readonly string _dir = TempRepo.CreateModern(restore: true);
    private readonly string _mirrorParent = Path.Combine(Path.GetTempPath(), "pelly-verify-it-" + Guid.NewGuid().ToString("N")[..8]);
    private BaselineService? _baseline;

    public void Dispose()
    {
        if (_baseline is not null)
        {
            try { _baseline.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _baseline.Dispose();
        }
        TempRepo.Delete(_dir);
        TempRepo.Delete(_mirrorParent);
    }

    private VerifyService NewService()
    {
        var fs = new FileSystem();
        var processes = new SystemProcessRunner(TimeProvider.System);
        var options = new PellyOptions();
        var tools = new BuildToolResolver(fs, processes, options.Build);
        var verifier = new GroupVerifier(fs, new BuildVerifier(processes, options.Build), tools, OperatingSystem.IsWindows);
        var tests = new BaselineTestRunner(fs, new TestRunner(processes, options.Build, options.Test), tools, OperatingSystem.IsWindows);
        var git = new GitRepo(processes, _dir);
        _baseline = new BaselineService(fs, _dir, Path.Combine(_mirrorParent, "0123456789abcdef"), git, verifier, tests, options.Test,
            TimeProvider.System, NullLogger<BaselineService>.Instance, new DotnetRestorer(fs, processes, options.Build));
        var ctx = new ServerContext(_dir, options, new Workspace(_dir, Path.Combine(_dir, ".pelly")));
        return new VerifyService(fs, ctx, git, _baseline, verifier, tests, VerifyService.DefaultWaitLimit,
            _ => throw new InvalidOperationException("no SQL expected"), processes, () => false);
    }

    [Fact]
    public async Task Integration_BreakBuild_ThenFix()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = NewService();

        var path = Path.Combine(_dir, ServiceFile.Replace('/', Path.DirectorySeparatorChar));
        var original = await File.ReadAllTextAsync(path, ct);
        const string anchor = "private const int MaxPageSize = 100;";
        Assert.Contains(anchor, original);
        await File.WriteAllTextAsync(path, original.Replace(anchor, anchor + " private int _extra = \"text\";", StringComparison.Ordinal), ct);

        var first = (await service.VerifyAsync(new Progress<string>(), ct)).Split('\n');
        Assert.True(first[0] == "결과: 새 오류 1건, 새 경고 0건", string.Join('\n', first));
        Assert.DoesNotContain(first, l => l.StartsWith("HEAD 기준을 만들지 못해", StringComparison.Ordinal));
        Assert.Contains(first, l => l.StartsWith("- Modern/Shop.Api/Services/OrderService.cs:", StringComparison.Ordinal) && l.Contains(" CS0029 ", StringComparison.Ordinal));

        // Fixed, but still a change against HEAD.
        await File.WriteAllTextAsync(path, original.Replace(anchor, anchor + " private int _extra = 1;", StringComparison.Ordinal), ct);
        var second = (await service.VerifyAsync(new Progress<string>(), ct)).Split('\n');
        Assert.True(second[0] == "결과: 새 문제 없음", string.Join('\n', second));
        Assert.Contains("[테스트] Shop.Api.Tests — 통과", second);
    }

    [Fact]
    public async Task Integration_HeadError_NotReported()
    {
        var ct = TestContext.Current.CancellationToken;
        // HEAD already has a compile error in another file of the same project: the baseline must hide it.
        await File.WriteAllTextAsync(Path.Combine(_dir, "Modern", "Shop.Api", "Services", "HeadBroken.cs"),
            "namespace Shop.Api.Services { internal static class HeadBroken { internal static int Value => missingName; } }", ct);
        TempRepo.Commit(_dir);
        var service = NewService();

        var path = Path.Combine(_dir, ServiceFile.Replace('/', Path.DirectorySeparatorChar));
        var original = await File.ReadAllTextAsync(path, ct);
        const string anchor = "private const int MaxPageSize = 100;";
        await File.WriteAllTextAsync(path, original.Replace(anchor, anchor + " private int _extra = \"text\";", StringComparison.Ordinal), ct);

        var lines = (await service.VerifyAsync(new Progress<string>(), ct)).Split('\n');
        var text = string.Join('\n', lines);
        Assert.True(lines[0] == "결과: 새 오류 1건, 새 경고 0건", text);
        Assert.DoesNotContain(lines, l => l.StartsWith("HEAD 기준을 만들지 못해", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("- Modern/Shop.Api/Services/OrderService.cs:", StringComparison.Ordinal) && l.Contains(" CS0029 ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("HeadBroken", StringComparison.Ordinal) || l.Contains("CS0103", StringComparison.Ordinal));
    }
}
