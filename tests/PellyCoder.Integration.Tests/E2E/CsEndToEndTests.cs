using System.IO.Abstractions;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.Infrastructure;
using Xunit.Sdk;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// Real-model runs of <c>pelly cs</c> against copies of the sample corpus. They need Ollama
/// (<c>PELLY_TEST_OLLAMA</c>, <c>PELLY_TEST_MODEL</c>); the judging rules follow eval/benchmark/README.md.
/// </summary>
public sealed class CsEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private const string ModernRequest = "배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘";
    private const string LegacyRequest = "배송 중(Shipped)인 주문은 취소할 수 없도록 InvalidOperationException을 던지게 수정해줘";

    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var dir in _temps)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* temp folder, best effort */ }
        }
    }

    [OllamaFact]
    public async Task ModernCancelAsync_AddsShippedGuard()
    {
        var root = CopyCorpus("Modern", ["."]);
        var file = Path.Combine(root, "Shop.Api", "Services", "OrderService.cs");
        var before = File.ReadAllText(file);

        var (exit, ui) = await RunAsync(file, "CancelAsync", ModernRequest, dryRun: false);
        DumpRun(root, ui);

        var after = File.ReadAllText(file);
        Assert.Equal(ExitCodes.Success, exit);
        AssertTargetChangedAndRestUntouched(before, after, "CancelAsync", LanguageVersion.Latest, "OrderStatus.Shipped");
    }

    [OllamaFact]
    public async Task LegacyCp949Cancel_KeepsEncodingAndSyntax()
    {
        var root = CopyCorpus("Legacy", ["Legacy.sln", "Shop.Web", "Shop.Data"]);
        var file = Path.Combine(root, "Shop.Web", "Services", "OrderService.cs");
        var beforeBytes = File.ReadAllBytes(file);
        var cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949)!;
        var before = cp949.GetString(beforeBytes);

        var (exit, ui) = await RunAsync(file, "Cancel", LegacyRequest, dryRun: false);
        DumpRun(root, ui);

        var afterBytes = File.ReadAllBytes(file);
        if (!OperatingSystem.IsWindows())
        {
            // Legacy projects are build-verified only on Windows (design §8.9): the run stops before touching the file.
            Assert.Equal(ExitCodes.EnvError, exit);
            Assert.Equal(beforeBytes, afterBytes);
            Assert.Contains("warn: 레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증할 수 있습니다", ui.Lines);
            return;
        }
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(afterBytes));
        var after = cp949.GetString(afterBytes);
        Assert.DoesNotContain("�", after);

        // CRLF is kept everywhere: no bare LF was introduced.
        Assert.Equal(0, CountBareLf(after));
        Assert.True(after.Contains("\r\n", StringComparison.Ordinal));

        var version = LanguageVersionDetector.Detect(new FileSystem(), ProjectFileLocator.FindNearestCsproj(new FileSystem(), file)).Version;
        AssertTargetChangedAndRestUntouched(before, after, "Cancel", version, "OrderStatus.Shipped");
    }

    [OllamaFact]
    public async Task DryRun_DoesNotModifyCorpusCopy()
    {
        var root = CopyCorpus("Modern", ["."]);
        var file = Path.Combine(root, "Shop.Api", "Services", "OrderService.cs");
        var before = File.ReadAllBytes(file);

        var (exit, ui) = await RunAsync(file, "CancelAsync", ModernRequest, dryRun: true);
        DumpRun(root, ui);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Contains(ui.Lines, l => l.StartsWith("diff:", StringComparison.Ordinal));
    }

    private async Task<(int Exit, RecordingUserInteraction Ui)> RunAsync(string file, string method, string request, bool dryRun)
    {
        var fs = new FileSystem();
        var overrides = new Dictionary<string, string?>
        {
            ["llm:endpoint"] = TestEnv.OllamaEndpoint,
            ["llm:model"] = TestEnv.OllamaModel,
        };
        var loaded = PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), overrides);
        var options = Assert.IsType<PellyOptions>(loaded.Options);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var backend = new OllamaBackend(http, options.Llm, TimeProvider.System);
        var ui = new RecordingUserInteraction();
        var orchestrator = FsmOrchestratorFactory.Create(fs, backend, ui, options, TimeProvider.System, new SystemProcessRunner(TimeProvider.System));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var exit = await orchestrator.RunAsync(new RunRequest(file, null, method, request, Yes: true, DryRun: dryRun, MaxRetries: null), cts.Token);
        return (exit, ui);
    }

    /// <summary>Benchmark rule: the target must change, mustAdd occurrences increase, other members are untouched.</summary>
    private static void AssertTargetChangedAndRestUntouched(string before, string after, string member, LanguageVersion version, string mustAdd)
    {
        var beforeLf = Lf(before);
        var afterLf = Lf(after);
        var (beforeTarget, beforeRest) = Split(beforeLf, version, member);
        var (afterTarget, afterRest) = Split(afterLf, version, member);

        Assert.NotEqual(beforeTarget, afterTarget);
        Assert.True(Count(afterTarget, mustAdd) > Count(beforeTarget, mustAdd),
            $"'{mustAdd}' did not increase in {member}:\n{afterTarget}");
        Assert.Equal(beforeRest, afterRest);

        var outcome = SyntaxVerifier.Verify(afterLf, version, "OrderService.cs");
        Assert.True(outcome.Passed, string.Join("\n", outcome.NewErrors.Select(e => $"{e.Line}:{e.Column} {e.Code} {e.Message}")));
    }

    private static (string Target, string Others) Split(string textLf, LanguageVersion version, string member)
    {
        var found = Assert.Single(MemberLocator.FindByName(textLf, version, null, member));
        var lines = textLf.Split('\n');
        var start = found.Span.StartLine - 1;
        var end = found.Span.EndLine;
        var target = string.Join('\n', lines[start..end]);
        var rest = string.Join('\n', lines[..start].Concat(lines[end..]));
        return (target, rest);
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static int CountBareLf(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r')) count++;
        return count;
    }

    private string CopyCorpus(string corpus, string[] entries)
    {
        var temp = Path.Combine(Path.GetTempPath(), "pelly-e2e-" + Guid.NewGuid().ToString("N"));
        _temps.Add(temp);
        Directory.CreateDirectory(temp);
        var source = CorpusPaths.Get(corpus);
        foreach (var entry in entries)
        {
            var from = Path.GetFullPath(Path.Combine(source, entry));
            var to = Path.GetFullPath(Path.Combine(temp, entry));
            if (Directory.Exists(from)) CopyDirectory(from, to);
            else File.Copy(from, to);
        }
        return temp;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or "TestResults" or ".pelly") continue;
            CopyDirectory(dir, Path.Combine(to, name));
        }
    }

    private void DumpRun(string root, RecordingUserInteraction ui)
    {
        foreach (var line in ui.Lines) output.WriteLine(line);
        var runs = Path.Combine(root, ".pelly", "runs");
        if (!Directory.Exists(runs)) return;
        foreach (var summary in Directory.GetFiles(runs, "summary.json", SearchOption.AllDirectories))
            output.WriteLine(File.ReadAllText(summary));
        var keep = Environment.GetEnvironmentVariable("PELLY_TEST_KEEP");
        if (!string.IsNullOrEmpty(keep))
        {
            var dest = Path.Combine(keep, Path.GetFileName(root));
            CopyDirectory(runs, dest);
            output.WriteLine($"runs kept at {dest}");
        }
    }
}
