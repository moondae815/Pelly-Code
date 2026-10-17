using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

public sealed class FsmOrchestratorTests : IDisposable
{
    // A regression must fail the test, not hang the run.
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(60);

    private const string CalcSource =
        "namespace Demo;\n" +
        "\n" +
        "public class Calc\n" +
        "{\n" +
        "    public int Divide(int a, int b)\n" +
        "    {\n" +
        "        return a / b;\n" +
        "    }\n" +
        "}\n";

    private const string Original = "        return a / b;";
    private const string Guarded = "        if (b == 0) throw new DivideByZeroException();\n        return a / b;";
    private const string Broken = "        return a / b";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-fsm-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly PellyOptions _options = new();
    private readonly ScriptedProcessRunner _processes = new();

    public FsmOrchestratorTests()
    {
        // These runs are about the M1 flow: every build (baseline and verification) succeeds without output.
        _processes.WhenEmpty(new ProcessResult(0, "", "", [], TimeSpan.Zero, false));
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), ""); // pins the workspace root
        File.WriteAllText(Path.Combine(_root, "Demo", "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(CalcPath, CalcSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");

    private string BackupDir => Path.Combine(_root, ".pelly", "backup");

    private static RunRequest Req(string path, string method = "Divide", bool yes = true, bool dryRun = false, string? cls = null) =>
        new(path, cls, method, "Throw DivideByZeroException when b is zero.", yes, dryRun, null);

    /// <summary>Model output after the "&lt;&lt;&lt;&lt;&lt;&lt;&lt; SEARCH\n" prefill.</summary>
    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private Task<int> Run(RunRequest request) => RunAsync(request, TestContext.Current.CancellationToken);

    private Task<int> RunAsync(RunRequest request, CancellationToken ct) =>
        FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes)
            .RunAsync(request, ct)
            .WaitAsync(WallClock, CancellationToken.None);

    private JsonElement Summary()
    {
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories));
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement;
    }

    private bool HasBackup() => Directory.Exists(BackupDir) && Directory.EnumerateFiles(BackupDir, "*", SearchOption.AllDirectories).Any();

    [Fact]
    public async Task HappyPath_AppliesPatch_Exit0_LogsSummary()
    {
        _backend.Enqueue(Block(Original, Guarded), evalCount: 30, promptEvalCount: 900, promptEvalCachedCount: 600);

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        var summary = Summary();
        Assert.Equal(0, summary.GetProperty("exitCode").GetInt32());
        Assert.Equal(1, summary.GetProperty("attempts").GetInt32());
        Assert.Equal("Sdk", summary.GetProperty("projectStyle").GetString());
        Assert.Equal(900, summary.GetProperty("inputTokensServer").GetInt32());
        Assert.Equal(600, summary.GetProperty("inputTokensCached").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("failureKind").ValueKind);
        var runDir = Path.GetDirectoryName(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories)[0])!;
        Assert.True(File.Exists(Path.Combine(runDir, "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(runDir, "attempt-1", "prompt.txt")));
        Assert.True(File.Exists(Path.Combine(runDir, "attempt-1", "output.txt")));
    }

    [Fact]
    public async Task SearchMismatch_ThenFixed_SucceedsOnAttempt2()
    {
        _backend.Enqueue(Block("        return a * b;", Guarded));
        _backend.Enqueue("The SEARCH must copy the original line.</think>", evalCount: 512);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(3, _backend.Requests.Count); // attempt 1 code, attempt 2 think + code
        Assert.Contains("SEARCH NOT FOUND", _backend.Requests[1].RawPrompt);
        Assert.Contains("SEARCH NOT FOUND", _backend.Requests[2].RawPrompt);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Equal(2, Summary().GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task SyntaxError_FixForward_SucceedsOnAttempt2()
    {
        string? fileDuringAttempt2 = null;
        _backend.Enqueue(Block(Original, Broken));
        _backend.Enqueue("Add the missing semicolon.</think>", evalCount: 20);
        _backend.Enqueue(Block(Broken, Guarded));
        _backend.BeforeRespond = _ =>
        {
            if (_backend.Requests.Count == 2) fileDuringAttempt2 = File.ReadAllText(CalcPath);
            return Task.CompletedTask;
        };

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(CalcSource.Replace(Original, Broken), fileDuringAttempt2); // attempt 1 was written
        Assert.Contains("SYNTAX CHECK FAILED", _backend.Requests[1].RawPrompt);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        var codes = Summary().GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("CS1002", codes);
    }

    [Fact]
    public async Task AllAttemptsFail_RestoresOriginal_Exit1()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _backend.Enqueue(Block(Original, Broken));
        for (var i = 0; i < 3; i++)
        {
            _backend.Enqueue("Retry.</think>", evalCount: 5);
            _backend.Enqueue(Block(Broken, Broken));
        }

        var exit = await Run(Req(CalcPath));

        Assert.Equal(1, exit);
        Assert.Equal(7, _backend.Requests.Count);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        var summary = Summary();
        Assert.Equal(4, summary.GetProperty("attempts").GetInt32());
        Assert.Equal("SyntaxErrors", summary.GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task ExternalEditDuringGeneration_DoesNotOverwrite()
    {
        var external = CalcSource.Replace("return a / b;", "return a / b; // user edit");
        _backend.Enqueue(Block(Original, Guarded));
        _backend.BeforeRespond = _ =>
        {
            File.WriteAllText(CalcPath, external);
            return Task.CompletedTask;
        };

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(external, File.ReadAllText(CalcPath));
        Assert.Equal("FileChangedExternally", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task ExternalEditBetweenAttempts_DoesNotOverwrite()
    {
        var external = CalcSource.Replace("return a / b;", "return a / b; // user edit");
        _backend.Enqueue(Block(Original, Broken));
        _ui.OnStatus = (phase, attempt) =>
        {
            if (phase == RunPhase.Slice && attempt == 1) File.WriteAllText(CalcPath, external);
        };

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Single(_backend.Requests);
        Assert.Equal(external, File.ReadAllText(CalcPath));
        Assert.Contains(_ui.Warnings, w => w.Contains(BackupDir, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerDown_FailsInPreflightWithoutTouchingFile()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _backend.Status = new LlmServerStatus(false, null, false, false, null, TimeSpan.Zero, "connection refused");

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.False(HasBackup());
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Contains(_ui.Warnings, w => w.Contains("pelly doctor") && w.Contains(_options.Llm.Endpoint));
    }

    [Fact]
    public async Task ModelMissing_FailsInPreflight()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _backend.Status = new LlmServerStatus(true, "0.40.0", false, false, null, TimeSpan.Zero, null);

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Contains(_ui.Warnings, w => w.Contains(_options.Llm.Model) && w.Contains("pelly doctor"));
    }

    [Fact]
    public async Task Cp949CrLfFile_PreservesUnchangedBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp949 = Encoding.GetEncoding(949);
        var project = Path.Combine(_root, "Shop.Web");
        Directory.CreateDirectory(Path.Combine(project, "Services"));
        // SDK style so the build tool resolves on every OS; legacy resolution is covered by BuildVerificationFlowTests.
        File.WriteAllText(Path.Combine(project, "Shop.Web.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
            "  <PropertyGroup>\r\n    <TargetFramework>net48</TargetFramework>\r\n    <LangVersion>7.3</LangVersion>\r\n  </PropertyGroup>\r\n" +
            "</Project>\r\n");
        var path = Path.Combine(project, "Services", "OrderService.cs");
        File.Copy(CorpusLocation.Get("Legacy/Shop.Web/Services/OrderService.cs"), path);
        var originalBytes = File.ReadAllBytes(path);
        const string oldLine = "            var trimmed = reason.Trim();";
        const string newLine = "            var trimmed = (reason ?? string.Empty).Trim(); // 공백 제거";
        _backend.Enqueue(Block(oldLine, newLine));

        var exit = await Run(Req(path, method: "Cancel"));

        Assert.Equal(0, exit);
        var expected = cp949.GetBytes(cp949.GetString(originalBytes).Replace(oldLine, newLine));
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)));
        Assert.Equal("Sdk", Summary().GetProperty("projectStyle").GetString());
    }

    [Fact]
    public async Task CancelAfterWrite_RestoresOriginalBytes()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        using var cts = new CancellationTokenSource();
        string? fileWhenCancelled = null;
        _backend.Enqueue(Block(Original, Broken));
        _backend.Enqueue("never streamed</think>", evalCount: 5);
        _backend.BeforeRespond = _ =>
        {
            if (_backend.Requests.Count == 2)
            {
                fileWhenCancelled = File.ReadAllText(CalcPath);
                cts.Cancel();
            }
            return Task.CompletedTask;
        };

        var exit = await RunAsync(Req(CalcPath), cts.Token);

        Assert.Equal(2, exit);
        Assert.Equal(CalcSource.Replace(Original, Broken), fileWhenCancelled);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal("Cancelled", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task UserRejects_Restores_Exit2()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _ui.IsInteractive = true;
        _ui.ConfirmAnswers.Enqueue(false);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, yes: false));

        Assert.Equal(2, exit);
        Assert.Equal(["적용할까요?"], _ui.ConfirmMessages);
        var diff = Assert.Single(_ui.Diffs);
        Assert.Equal(CalcSource, diff.Before);
        Assert.Equal(CalcSource.Replace(Original, Guarded), diff.After);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
    }

    [Fact]
    public async Task NonInteractiveWithoutYes_Rejects_Exit2()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _ui.IsInteractive = false;
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, yes: false));

        Assert.Equal(2, exit);
        Assert.Empty(_ui.ConfirmMessages);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal("Rejected", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task DryRun_ShowsDiff_DoesNotWrite_Exit0()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, yes: false, dryRun: true));

        Assert.Equal(0, exit);
        var diff = Assert.Single(_ui.Diffs);
        Assert.Equal(CalcSource, diff.Before);
        Assert.Equal(CalcSource.Replace(Original, Guarded), diff.After);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.False(HasBackup());
    }

    [Fact]
    public async Task AmbiguousOverload_NonInteractive_Exit3()
    {
        File.WriteAllText(CalcPath, CalcSource.Replace("}\n}\n", "}\n\n    public double Divide(double a, double b) => a / b;\n}\n"));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(3, exit);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task AmbiguousOverload_Interactive_UsesSelection()
    {
        var source = CalcSource.Replace("}\n}\n", "}\n\n    public double Divide(double a, double b) => a / b;\n}\n");
        File.WriteAllText(CalcPath, source);
        _ui.IsInteractive = true;
        _ui.SelectIndex = labels => labels.ToList().FindIndex(l => l.Contains("Divide(int, int)"));
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(2, Assert.Single(_ui.SelectLabels).Count);
        Assert.Equal(source.Replace(Original, Guarded), File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task UnknownMethod_Exit3()
    {
        var exit = await Run(Req(CalcPath, method: "Multiply"));

        Assert.Equal(3, exit);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task SecondRunWhileLocked_Exit4()
    {
        using var held = WorkspaceLock.TryAcquire(_fs, new Workspace(_root, Path.Combine(_root, ".pelly")));
        Assert.NotNull(held);

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Contains(_ui.Warnings, w => w.Contains("다른 pelly 실행이 진행 중입니다"));
    }

    [Fact]
    public async Task UnhandledException_Restores_Exit4()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _backend.Enqueue(Block(Original, Broken));
        _backend.EnqueueException(new InvalidOperationException("scripted failure"));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Contains(_ui.Warnings, w => w.Contains("InvalidOperationException") && w.Contains("scripted failure"));
        Assert.Equal("Crashed", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task ConflictMarkersInFile_Exit4()
    {
        var conflicted = CalcSource.Replace(Original, "<<<<<<< HEAD\n" + Original + "\n=======\n        return b / a;\n>>>>>>> other");
        File.WriteAllText(CalcPath, conflicted);

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Equal(conflicted, File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task LlmUnavailableDuringGeneration_Exit4_Restores()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _backend.Enqueue(Block(Original, Broken));
        _backend.EnqueueException(new LlmUnavailableException("server went away"));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Contains(_ui.Warnings, w => w.Contains("server went away"));
        Assert.Equal("EnvError", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task WarnThrows_StillReturnsExitCode_RestoresAndReleasesLock()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _ui.IsInteractive = false;
        _ui.WarnThrows = new InvalidOperationException("markup error");
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, yes: false));

        Assert.Equal(2, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        using var next = WorkspaceLock.TryAcquire(_fs, new Workspace(_root, Path.Combine(_root, ".pelly")));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task LoggerFailure_DoesNotUndoApprovedEdit()
    {
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await FsmOrchestratorFactory.Create(new LogFailingFileSystem(), _backend, _ui, _options, TimeProvider.System, _processes)
            .RunAsync(Req(CalcPath), TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Contains(_ui.Warnings, w => w.Contains("scripted log failure"));
    }

    /// <summary>Calc with one syntax error already in another member (missing semicolon in <c>Two</c>, line 9).</summary>
    private const string CalcWithPreexistingError =
        "namespace Demo;\n" +
        "\n" +
        "public class Calc\n" +
        "{\n" +
        "    public int Divide(int a, int b)\n" +
        "    {\n" +
        "        return a / b;\n" +
        "    }\n" +
        "    public int Two() { return 2 }\n" +
        "}\n";

    [Fact]
    public async Task PreexistingSyntaxError_ValidPatch_Exit0_WarnsOnce()
    {
        File.WriteAllText(CalcPath, CalcWithPreexistingError);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(CalcWithPreexistingError.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Single(_ui.Warnings, w => w == "원본 파일에 이미 구문 오류가 1개 있습니다 — 새로 생긴 오류만 검사합니다");
    }

    [Fact]
    public async Task PreexistingSyntaxError_PatchAddsSameKindError_ReportsOnlyTheNewOne()
    {
        File.WriteAllText(CalcPath, CalcWithPreexistingError);
        var originalBytes = File.ReadAllBytes(CalcPath);
        _backend.Enqueue(Block(Original, Broken)); // a second CS1002 "; expected", on line 7
        for (var i = 0; i < 3; i++)
        {
            _backend.Enqueue("Retry.</think>", evalCount: 5);
            _backend.Enqueue(Block(Broken, Broken));
        }

        var exit = await Run(Req(CalcPath));

        Assert.Equal(1, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("SYNTAX CHECK FAILED (1 error)", prompt);
        Assert.Contains("Calc.cs(7): CS1002", prompt);
        Assert.DoesNotContain("Calc.cs(9)", prompt);
    }
}
