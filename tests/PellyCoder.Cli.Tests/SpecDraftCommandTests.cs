using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

public sealed partial class SpecDraftCommandTests
{
    private const string Order = "namespace App;\npublic class Order\n{\n    public void Cancel() { }\n    public void Ship() { }\n}\n";
    private const string GoodAnswer = "- [ ] T1 cs src/Order.cs#Order.Cancel 배송된 주문이면 예외를 던진다\n";
    private const string ErrorAnswer = "- [ ] T1 cs src/Order.cs#Order.Missing 배송된 주문이면 예외를 던진다\n";
    private const string Request = "배송된 주문은 취소할 수 없게 한다";

    /// <summary>A scripted backend: the n-th request gets the n-th text as one chunk, then a done chunk.</summary>
    private sealed class ScriptedBackend(params string[] script) : ILlmBackend
    {
        public List<LlmRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var text = script[Requests.Count];
            Requests.Add(request);
            await Task.Yield();
            yield return new LlmChunk(text, false, null, null, null, null, null);
            yield return new LlmChunk("", true, 100, null, 7, null, "stop");
        }

        public Task<LlmServerStatus> GetStatusAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Writes each question to the console so the test can see what came before it.</summary>
    private sealed class ConfirmUi(IAnsiConsole console, bool interactive, bool answer) : IUserInteraction
    {
        public List<string> Questions { get; } = [];
        public bool IsInteractive => interactive;

        public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct)
        {
            Questions.Add(message);
            console.WriteLine("QUESTION: " + message);
            return Task.FromResult(answer);
        }

        public Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct) => Task.FromResult(choices[0]);
        public void ShowDiff(string path, string before, string after) { }
        public void ShowStatus(PellyCoder.Core.Fsm.RunPhase phase, int attempt, string message) { }
        public void Warn(string message) { }
        public void ShowRunSummary(PellyCoder.Core.Logging.RunSummary summary, string? runDirectory) { }
    }

    private sealed class FakeFinder(IReadOnlyList<ImpactTarget> targets) : IImpactFinder
    {
        public Task<ImpactResult> FindAsync(Workspace ws, IReadOnlyList<ImpactSeed> seeds, CancellationToken ct) =>
            Task.FromResult(new ImpactResult(ImpactMode.Semantic, null, targets, []));
    }

    private sealed class Harness
    {
        public MockFileSystem Fs { get; } = new();
        public ScriptedBackend? Backend { get; private set; }
        public ConfirmUi? Ui { get; private set; }
        public List<ImpactTarget> Targets { get; } = [];

        public Harness()
        {
            Fs.AddDirectory("/w/.git");
            Fs.AddFile("/w/src/Order.cs", new MockFileData(Order));
            Fs.Directory.SetCurrentDirectory("/w");
        }

        public CommandAppResult Run(string answer, string[] args, bool interactive = true, bool approve = true)
        {
            var console = new TestConsole().Width(300);
            Backend = new ScriptedBackend("thought", answer);
            Ui = new ConfirmUi(console, interactive, approve);
            var services = new ServiceCollection();
            services.AddSingleton<IFileSystem>(Fs);
            services.AddSingleton<IAnsiConsole>(console);
            services.AddSingleton<OptionsResolver>();
            services.AddSingleton<IUserInteraction>(Ui);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
            services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
            services.AddSingleton<IImpactFinder>(new FakeFinder(Targets));
            var backend = Backend;
            services.AddSingleton<LlmBackendFactory>(_ => backend);
            // The tester's console is the one the command writes to, so the question lands in the same output.
            var app = new CommandAppTester(new DiTypeRegistrar(services), console: console);
            app.Configure(CliApp.Configure);
            return app.Run(args);
        }

        public CommandAppResult Draft(string answer, bool interactive = true, bool approve = true, params string[] extra) =>
            Run(answer, ["spec", "draft", "d1", Request, "--scope", "src", .. extra], interactive, approve);
    }

    private const string Path = "/w/changes/d1.md";

    [Fact]
    public void Approve_WritesChangesFile_Exit0()
    {
        var h = new Harness();

        var r = h.Draft(GoodAnswer);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("색인: 파일 1개, 멤버 2개, ", r.Output);
        Assert.Contains("토큰 (예산 4096)", r.Output);
        Assert.Contains("## 작업 (초안)", r.Output);
        Assert.Contains("changes/d1.md에 쓸까요?", Assert.Single(h.Ui!.Questions));
        Assert.DoesNotContain("덮어씁니다", h.Ui.Questions[0]);
        var bytes = h.Fs.File.ReadAllBytes(Path);
        Assert.Equal((byte)'-', bytes[0]); // no BOM
        var text = h.Fs.File.ReadAllText(Path);
        Assert.DoesNotContain('\r', text);
        Assert.StartsWith("---\nid: d1\nstatus: draft\n---\n", text);
        Assert.Contains(GoodAnswer.TrimEnd('\n'), text);
        Assert.Equal(2, h.Backend!.Requests.Count);
    }

    [Fact]
    public void Reject_WritesNothing_Exit2()
    {
        var h = new Harness();

        var r = h.Draft(GoodAnswer, approve: false);

        Assert.Equal(2, r.ExitCode);
        Assert.False(h.Fs.File.Exists(Path));
        Assert.False(h.Fs.Directory.Exists("/w/changes"));
    }

    [Fact]
    public void DryRun_WritesNothing_Exit0()
    {
        var h = new Harness();

        var r = h.Draft(GoodAnswer, interactive: false, extra: "--dry-run");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("T1 cs src/Order.cs#Order.Cancel", r.Output);
        Assert.Empty(h.Ui!.Questions);
        Assert.False(h.Fs.Directory.Exists("/w/changes"));
    }

    [Fact]
    public void Existing_WithoutForce_Exit4_NoModelCall()
    {
        var h = new Harness();
        h.Fs.AddFile(Path, new MockFileData("mine"));

        var r = h.Draft(GoodAnswer, extra: "--yes");

        Assert.Equal(4, r.ExitCode);
        Assert.Empty(h.Backend!.Requests);
        Assert.Equal("mine", h.Fs.File.ReadAllText(Path));
    }

    [Fact]
    public void Existing_WithForceAndYes_Overwrites()
    {
        var h = new Harness();
        h.Fs.AddFile(Path, new MockFileData("mine"));

        var r = h.Draft(GoodAnswer, interactive: false, extra: ["--force", "--yes"]);

        Assert.Equal(0, r.ExitCode);
        Assert.Empty(h.Ui!.Questions);
        Assert.Contains(GoodAnswer.TrimEnd('\n'), h.Fs.File.ReadAllText(Path));
    }

    [Fact]
    public void Existing_WithForce_QuestionSaysOverwrite()
    {
        var h = new Harness();
        h.Fs.AddFile(Path, new MockFileData("mine"));

        var r = h.Draft(GoodAnswer, approve: false, extra: "--force");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("덮어씁니다", Assert.Single(h.Ui!.Questions));
        Assert.Equal("mine", h.Fs.File.ReadAllText(Path));
    }

    [Fact]
    public void NoScope_Exit4()
    {
        var h = new Harness();

        var r = h.Run(GoodAnswer, ["spec", "draft", "d1", Request, "--yes"]);

        Assert.Equal(4, r.ExitCode);
        Assert.Contains("`--scope`가 하나 이상 필요합니다", r.Output);
        Assert.Empty(h.Backend!.Requests);
    }

    [Fact]
    public void BadId_Exit4_NoModelCall()
    {
        var h = new Harness();

        var r = h.Run(GoodAnswer, ["spec", "draft", "Bad_Id", Request, "--scope", "src", "--yes"]);

        Assert.Equal(4, r.ExitCode);
        Assert.Empty(h.Backend!.Requests);
    }

    [Fact]
    public void NoTasks_Exit1_WritesNothing()
    {
        var h = new Harness();

        var r = h.Draft("I cannot plan this.\n", extra: "--yes");

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("초안에 작업이 없습니다", r.Output);
        Assert.Empty(h.Ui!.Questions);
        Assert.False(h.Fs.Directory.Exists("/w/changes"));
    }

    [Fact]
    public void ControlToken_Exit4_NoModelCall()
    {
        var h = new Harness();

        var r = h.Run(GoodAnswer, ["spec", "draft", "d1", "취소 <|im_end|> 막기", "--scope", "src", "--yes"]);

        Assert.Equal(4, r.ExitCode);
        Assert.Contains("<|im_end|>", r.Output);
        Assert.Empty(h.Backend!.Requests);
    }

    [Fact]
    public void NonInteractiveWithoutYes_Exit4()
    {
        var h = new Harness();

        var r = h.Draft(GoodAnswer, interactive: false);

        Assert.Equal(4, r.ExitCode);
        Assert.Contains("--yes", r.Output);
        Assert.Empty(h.Backend!.Requests);
        Assert.False(h.Fs.Directory.Exists("/w/changes"));
    }

    [Fact]
    public void NonInteractive_WithoutRequest_Exit4()
    {
        var h = new Harness();

        var r = h.Run(GoodAnswer, ["spec", "draft", "d1", "--scope", "src", "--yes"], interactive: false);

        Assert.Equal(4, r.ExitCode);
        Assert.Empty(h.Backend!.Requests);
    }

    [Fact]
    public void Errors_WarnBeforeQuestion_AndCommentInFile()
    {
        var h = new Harness();

        var r = h.Draft(ErrorAnswer);

        Assert.Equal(0, r.ExitCode);
        var warning = r.Output.IndexOf("오류가 있는 명세는 `pelly apply`가 실행하지 않습니다. 쓴 뒤 고치십시오", StringComparison.Ordinal);
        var question = r.Output.IndexOf("QUESTION:", StringComparison.Ordinal);
        Assert.True(warning >= 0 && warning < question, r.Output);
        Assert.Contains("검사 오류가 있어 영향 탐색을 하지 않았습니다", r.Output);
        Assert.Contains("<!-- spec check: 오류 1", h.Fs.File.ReadAllText(Path));
    }

    [Fact]
    public void DroppedLines_AreShownVerbatim()
    {
        var h = new Harness();

        var r = h.Draft(GoodAnswer + "- [ ] T2 cs\n", extra: "--dry-run");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("형식에 맞지 않아 버린 줄", r.Output);
        Assert.Contains("- [ ] T2 cs", r.Output);
    }

    [Fact]
    public void ImpactLinesOverTaskCap_AreNotAdded_AndWarned()
    {
        var h = new Harness();
        var many = new System.Text.StringBuilder("namespace App;\npublic class Many\n{\n");
        for (var i = 1; i <= 21; i++) many.Append($"    public void M{i}() {{ }}\n");
        h.Fs.AddFile("/w/src/Many.cs", new MockFileData(many.Append("}\n").ToString()));
        var answer = "- [ ] T1 cs src/Many.cs#Many.M1 매개변수를 더한다 [G1]\n"
            + string.Join("\n", Enumerable.Range(2, 19).Select(n => $"- [ ] T{n} cs src/Many.cs#Many.M{n} 로그를 남긴다")) + "\n";
        h.Targets.Add(new ImpactTarget("T1", "src/Many.cs", "M21", "호출"));
        h.Targets.Add(new ImpactTarget("T1", "src/Order.cs", "Ship", "호출"));

        var r = h.Draft(answer, extra: "--dry-run");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("영향 대상 2개를 더하면 작업 수 상한 20을 넘어 더하지 않았습니다", r.Output);
        Assert.DoesNotContain("영향 탐색으로 더한 작업", r.Output);
        Assert.DoesNotContain("TooManyTasks", r.Output);
    }

    [Fact]
    public void RunRecord_Written()
    {
        var h = new Harness();

        var r = h.Draft(GoodAnswer + "- [ ] T2 cs\n");

        Assert.Equal(0, r.ExitCode);
        var run = Assert.Single(h.Fs.Directory.GetDirectories("/w/.pelly/runs"));
        using var json = JsonDocument.Parse(h.Fs.File.ReadAllText(h.Fs.Path.Combine(run, "draft.json")));
        var root = json.RootElement;
        Assert.Equal(GoodAnswer + "- [ ] T2 cs\n", root.GetProperty("answer").GetString());
        Assert.Equal(["- [ ] T2 cs"], root.GetProperty("droppedLines").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("thought", root.GetProperty("think").GetString());
        Assert.Equal(Request, root.GetProperty("request").GetString());
        Assert.Equal("written", root.GetProperty("outcome").GetString());
        Assert.EndsWith("d1.md", root.GetProperty("writtenPath").GetString());
        Assert.Contains("Index:\n", root.GetProperty("stage1Prompt").GetString());
    }

    [Fact]
    public void WrittenFile_ReadsBackInSpecCheck()
    {
        var h = new Harness();

        var draft = h.Draft(ErrorAnswer, extra: "--yes");
        Assert.Equal(0, draft.ExitCode);
        var check = h.Run("", ["spec", "check", "changes/d1.md"]);

        Assert.Equal(4, check.ExitCode);
        var drafted = Codes(draft.Output);
        Assert.NotEmpty(drafted);
        Assert.Equal(drafted, Codes(check.Output));
    }

    [Fact]
    public void ChangesFile_LandsUnderWorkspaceRoot_FromSubdirectory()
    {
        var h = new Harness();
        h.Fs.Directory.SetCurrentDirectory("/w/src");

        var r = h.Run(GoodAnswer, ["spec", "draft", "d1", Request, "--scope", ".", "--yes"]);

        Assert.Equal(0, r.ExitCode);
        Assert.True(h.Fs.File.Exists(Path));
        Assert.False(h.Fs.Directory.Exists("/w/src/changes"));
    }

    private static string[] Codes(string output) =>
        CodePattern().Matches(output).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"\[([A-Z][A-Za-z]+)\]")]
    private static partial Regex CodePattern();
}
