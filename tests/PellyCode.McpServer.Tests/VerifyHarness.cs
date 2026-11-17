using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PellyCode.Core.Abstractions;
using PellyCode.Core.Config;
using PellyCode.Core.Models;
using PellyCode.Core.Processes;
using PellyCode.Core.Specs;
using PellyCode.Core.Testing;
using PellyCode.Core.Workspaces;
using PellyCode.McpServer.Baseline;
using PellyCode.McpServer.Git;
using PellyCode.McpServer.Verify;

namespace PellyCode.McpServer.Tests;

/// <summary>
/// A committed temp repo with copies of eval/corpus folders (original bytes) and a pelly.json with <c>asp</c>·<c>web</c> sections
/// (site root Asp), and a <see cref="VerifyService"/> over it whose build and test runners must never be needed.
/// </summary>
internal sealed class VerifyHarness : IDisposable
{
    public const string PellyJson = """
        {
          "asp": {
            "siteRoot": "Asp",
            "sqlSanitizers": ["SafeSQL"],
            "spCallFunctions": [{ "name": "ExecSP", "argument": 1 }]
          },
          "web": {
            "globals": ["gfnOpenPopup"]
          }
        }

        """;

    public static readonly Encoding Cp949;

    static VerifyHarness()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949);
    }

    private readonly string _mirror = Path.Combine(Path.GetTempPath(), "pelly-verify-mirror-" + Guid.NewGuid().ToString("N")[..8], "0123456789abcdef");
    private readonly List<BaselineService> _services = [];

    public string Root { get; }

    public VerifyHarness(params string[] corpus)
    {
        Root = TempRepo.Create();
        foreach (var name in corpus) TempRepo.CopyCorpus(Root, name);
        File.WriteAllText(Path.Combine(Root, "pelly.json"), PellyJson);
        File.WriteAllText(Path.Combine(Root, ".gitignore"), ".pelly/\n");
        TempRepo.Commit(Root);
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        TempRepo.Delete(Root);
        TempRepo.Delete(Path.GetDirectoryName(_mirror)!);
    }

    /// <summary>The repo's pelly.json alone: no user config and no environment (a developer's PELLY_SQL_CONNECTION stays out).</summary>
    public PellyOptions Options(Action<PellyOptions>? change = null)
    {
        var loaded = PellyOptionsLoader.Load(new FileSystem(), null, Path.Combine(Root, "pelly.json"),
            new Dictionary<string, string?>(), new Dictionary<string, string?>());
        var options = loaded.Options ?? throw new InvalidOperationException(string.Join("\n", loaded.Errors));
        change?.Invoke(options);
        return options;
    }

    public VerifyService Service(PellyOptions? options = null, Func<string, ISqlExecutor>? sql = null, Func<bool>? isWindows = null,
        IProcessRunner? processes = null)
    {
        var fs = new FileSystem();
        var git = new GitRepo(new SystemProcessRunner(TimeProvider.System), Root);
        var verifier = new NoBuild();
        var tests = new NoTests();
        var baseline = new BaselineService(fs, Root, _mirror, git, verifier, tests, new TestOptions(), TimeProvider.System,
            NullLogger<BaselineService>.Instance, new NoRestore());
        _services.Add(baseline);
        var ctx = new ServerContext(Root, options ?? Options(), new Workspace(Root, Path.Combine(Root, ".pelly")));
        return new VerifyService(fs, ctx, git, baseline, verifier, tests, TimeSpan.FromMinutes(5),
            sql ?? (_ => throw new InvalidOperationException("SQL executor not expected")),
            processes ?? new SystemProcessRunner(TimeProvider.System), isWindows ?? (() => false));
    }

    public string Full(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Rewrites a file through <paramref name="change"/> in its own encoding: a UTF-8 BOM is kept, valid UTF-8 stays UTF-8,
    /// anything else is CP949 (the corpus' legacy files).
    /// </summary>
    public void Edit(string rel, Func<string, string> change)
    {
        var bytes = File.ReadAllBytes(Full(rel));
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = bom ? bytes[3..] : bytes;
        Encoding encoding;
        try
        {
            new UTF8Encoding(false, true).GetString(body);
            encoding = new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            encoding = Cp949;
        }
        var text = change(encoding.GetString(body));
        var written = encoding.GetBytes(text);
        File.WriteAllBytes(Full(rel), bom ? [0xEF, 0xBB, 0xBF, .. written] : written);
    }

    /// <summary>Writes a new file (UTF-8 without BOM, or CP949 when asked).</summary>
    public void Write(string rel, string text, bool cp949 = false)
    {
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, cp949 ? Cp949.GetBytes(text) : new UTF8Encoding(false).GetBytes(text));
    }

    public void Commit() => TempRepo.Commit(Root);

    public void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} 실패:\n{output}{err.Result}");
    }

    public static string[] Lines(string text) => text.Split('\n');

    private sealed class NoBuild : IGroupVerifier
    {
        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct) =>
            throw new InvalidOperationException("no C# build expected");
    }

    private sealed class NoTests : IBaselineTestRunner
    {
        public Task<TestRun> RunAsync(string testProject, string workDir, string trxPath, CancellationToken ct) =>
            throw new InvalidOperationException("no test run expected");
    }

    private sealed class NoRestore : IBaselineRestorer
    {
        public Task<string?> RestoreAsync(string project, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
