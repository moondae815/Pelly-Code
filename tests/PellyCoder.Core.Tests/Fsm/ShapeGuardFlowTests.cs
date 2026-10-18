using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>Member shape guard inside the run: checked after the patch, before any baseline wait or write.</summary>
public sealed class ShapeGuardFlowTests : IDisposable
{
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

    private const string Header = "    public int Divide(int a, int b)";
    private const string Original = "        return a / b;";
    private const string Guarded = "        if (b == 0) throw new DivideByZeroException();\n        return a / b;";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-shape-flow-" + Guid.NewGuid().ToString("N"));
    private readonly WriteCountingFileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly PellyOptions _options = new();

    public ShapeGuardFlowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        File.WriteAllText(CsprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(CalcPath, CalcSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");
    private string CsprojPath => Path.Combine(_root, "Demo", "Demo.csproj");

    private static RunRequest Req(string path, bool dryRun = false, int? maxRetries = null) =>
        new(path, null, "Divide", null, "Throw DivideByZeroException when b is zero.", true, dryRun, maxRetries);

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private Task<int> Run(RunRequest request) =>
        FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes, null)
            .RunAsync(request, TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    private static ProcessResult Build(int exit = 0, params string[] lines)
    {
        var stdout = string.Join("\n", lines);
        return new ProcessResult(exit, stdout, "", Encoding.UTF8.GetBytes(stdout), TimeSpan.FromSeconds(1), false);
    }

    private JsonElement Summary()
    {
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories));
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement;
    }

    private int WritesTo(string path) => _fs.Writes.Count(w => w == path);

    [Fact]
    public async Task SignatureChange_IsShapeRejected_NeverWritten()
    {
        _processes.Enqueue(Build()); // baseline
        _processes.Enqueue(Build()); // build of attempt 2
        _backend.Enqueue(Block(Header, "    public int Divide(int a, int b, bool check)"));
        _backend.Enqueue("Keep the signature.</think>", evalCount: 10);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Equal(1, WritesTo(CalcPath)); // attempt 2 only
        Assert.Equal(2, _processes.Calls.Count);
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("SHAPE REJECTED: signature of Calc.Divide(int, int) changed to Calc.Divide(int, int, bool). Signature changes are not allowed.", prompt);
    }

    [Fact]
    public async Task HelperAddedOnAttempt1_IsEditableOnAttempt2()
    {
        const string withHelper =
            "        return Half(a) / b;\n" +
            "    }\n" +
            "\n" +
            "    private static int Half(int v)\n" +
            "    {\n" +
            "        return v / c;\n" +
            "    }";
        _processes.Enqueue(Build()); // baseline
        _processes.Enqueue(Build(1, $"{CalcPath}(12,20): error CS0103: The name 'c' does not exist in the current context [{CsprojPath}]"));
        _processes.Enqueue(Build());
        _backend.Enqueue(Block(Original + "\n    }", withHelper));
        _backend.Enqueue("Fix the helper.</think>", evalCount: 10);
        _backend.Enqueue(Block("        return v / c;", "        return v / 2;"));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        var prompt = _backend.Requests[2].RawPrompt;
        Assert.Contains("[EDITABLE: helper]\n    private static int Half(int v)\n    {\n        return v / c;\n    }\n[EDITABLE: target]", prompt);
        Assert.DoesNotContain("OUTSIDE EDITABLE REGION", prompt);
        Assert.Equal(CalcSource.Replace(Original + "\n    }", withHelper.Replace("v / c", "v / 2")), File.ReadAllText(CalcPath));
        Assert.Equal(3, _processes.Calls.Count);
    }

    [Fact]
    public async Task DryRun_ShapeRejected_StillReported()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _backend.Enqueue(Block(Original, Guarded + "\n    }\n\n    public void Extra()\n    {"));

        var exit = await Run(Req(CalcPath, dryRun: true, maxRetries: 0));

        Assert.Equal(1, exit);
        Assert.Empty(_ui.Diffs);
        Assert.Empty(_processes.Calls);
        Assert.Equal(0, WritesTo(CalcPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal("ShapeRejected", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task TargetMissingFromOriginal_IsNotFound_NothingWritten()
    {
        var guard = new FileGuard(_fs, TimeProvider.System);
        var snapshot = guard.Read(CalcPath);
        var ctx = new RunContext("run", Req(CalcPath), _options)
        {
            FilePath = CalcPath,
            Original = snapshot,
            Current = snapshot,
            Candidate = new MemberCandidate("Calc.Missing()", "Calc", new TextSpanInfo(5, 8)),
            Language = new LanguageTarget(LanguageVersion.CSharp12, "C# 12"),
            Blocks = [new PatchBlock(Original, Guarded)],
            SliceResult = new SliceResult(new Slice([], [], "C# 12"), [new EditableRegion(new TextSpanInfo(5, 8), "target")], false),
        };
        var phase = new ApplyPhase(guard, new FeedbackBuilder(QwenTokenCounter.Shared, 296), _ui, _options.Build);

        var outcome = await phase.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal("TargetNotFound", ctx.FailureKind);
        Assert.Equal(["대상 멤버 'Calc.Missing()'을(를) 원본에서 찾을 수 없습니다"], ctx.Messages);
        Assert.Equal(0, WritesTo(CalcPath));
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
        Assert.Equal(new Transition(RunPhase.Failed, 3), Transitions.Next(RunPhase.Apply, outcome, 1, 3));
    }

    // ---- no-change guard (M3a T14): the target must differ from the ORIGINAL target, whitespace-insensitively ----

    private const string NoChangeFeedback = "NO CHANGE: the edit leaves the target identical to the original. Make the requested change.";

    [Fact]
    public async Task NoOpBlock_IsNoChange_RetriedBeforeAnyWrite()
    {
        _processes.Enqueue(Build()); // baseline
        _processes.Enqueue(Build()); // build of attempt 2
        _backend.Enqueue(Block(Original, Original));
        _backend.Enqueue("Make the change.</think>", evalCount: 10);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(1, WritesTo(CalcPath)); // attempt 2 only
        Assert.Contains(NoChangeFeedback, _backend.Requests[1].RawPrompt);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task WhitespaceOnlyEdit_IsNoChange_Exit1_NothingWritten()
    {
        _processes.Enqueue(Build()); // baseline
        _backend.Enqueue(Block(Original, "        return   a /\n            b;"));

        var exit = await Run(Req(CalcPath, maxRetries: 0));

        Assert.Equal(1, exit);
        Assert.Equal(0, WritesTo(CalcPath));
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
        Assert.Equal("NoChange", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task RevertToOriginalOnAttempt2_IsNoChange_Exit1_Restored()
    {
        _processes.Enqueue(Build()); // baseline
        _processes.Enqueue(Build(1, $"{CalcPath}(7,9): error CS0103: The name 'x' does not exist in the current context [{CsprojPath}]"));
        _backend.Enqueue(Block(Original, Guarded));
        _backend.Enqueue("Undo it.</think>", evalCount: 10);
        _backend.Enqueue(Block(Guarded, Original)); // back to the original target: compared with the original, not attempt 1

        var exit = await Run(Req(CalcPath, maxRetries: 1));

        Assert.Equal(1, exit);
        Assert.Equal(2, _processes.Calls.Count); // the reverting attempt is never built
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
        var summary = Summary();
        Assert.Equal("NoChange", summary.GetProperty("failureKind").GetString());
        Assert.Equal(2, summary.GetProperty("attempts").GetInt32());
    }

    /// <summary>Real file system that records the destination of every atomic replace (FileGuard's write path).</summary>
    private sealed class WriteCountingFileSystem : FileSystem, IFileSystem
    {
        private readonly IFile _file;

        public WriteCountingFileSystem() => _file = new CountingFile(this);

        public List<string> Writes { get; } = [];

        IFile IFileSystem.File => _file;

        private sealed class CountingFile(WriteCountingFileSystem fs) : FileWrapper(fs)
        {
            public override void Move(string sourceFileName, string destFileName, bool overwrite)
            {
                fs.Writes.Add(destFileName);
                base.Move(sourceFileName, destFileName, overwrite);
            }

            public override void Replace(string sourceFileName, string destinationFileName, string? destinationBackupFileName)
            {
                fs.Writes.Add(destinationFileName);
                base.Replace(sourceFileName, destinationFileName, destinationBackupFileName);
            }
        }
    }
}
