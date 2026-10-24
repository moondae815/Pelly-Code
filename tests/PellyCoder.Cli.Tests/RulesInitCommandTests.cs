using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Rules;
using PellyCoder.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

public class RulesInitCommandTests
{
    private sealed class ConfirmUi(bool interactive, bool answer) : IUserInteraction
    {
        public bool IsInteractive => interactive;
        public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct) => Task.FromResult(answer);
        public Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct) => Task.FromResult(choices[0]);
        public void ShowDiff(string path, string before, string after) { }
        public void ShowStatus(PellyCoder.Core.Fsm.RunPhase phase, int attempt, string message) { }
        public void Warn(string message) { }
        public void ShowRunSummary(PellyCoder.Core.Logging.RunSummary summary, string? runDirectory) { }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static string AsyncSource()
    {
        var sb = new StringBuilder("using System.Threading.Tasks;\nnamespace N;\npublic class C\n{\n");
        for (var i = 0; i < 12; i++) sb.Append($"    public async Task M{i}Async() {{ await Task.Delay(1); }}\n");
        return sb.Append("}\n").ToString();
    }

    private static (CommandAppTester App, MockFileSystem Fs, TestConsole Console) Build(
        byte[]? agents, bool interactive = true, bool answer = true)
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/w/.git");
        fs.AddFile("/w/src/C.cs", new MockFileData(AsyncSource()));
        if (agents is not null) fs.AddFile("/w/AGENTS.md", new MockFileData(agents));
        fs.Directory.SetCurrentDirectory("/w");
        var console = new TestConsole().Width(300);
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<IAnsiConsole>(console);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IUserInteraction>(new ConfirmUi(interactive, answer));
        services.AddSingleton<TimeProvider>(new FixedTime());
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return (app, fs, console);
    }

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    [Fact]
    public void Init_DryRun_DoesNotWrite()
    {
        var (app, fs, _) = Build(null);
        var r = app.Run("rules", "init", "--dry-run");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("분석: C# 파일 1개, SQL 파일 0개 — 규칙 초안", r.Output);
        Assert.Contains("## Pelly rules", r.Output);
        Assert.Contains("[async]", r.Output);
        // The command prints the full path in the platform's form (C:\w\AGENTS.md on Windows).
        Assert.Contains(fs.Path.Combine(fs.Path.GetFullPath("/w"), "AGENTS.md"), r.Output);
        Assert.False(fs.File.Exists("/w/AGENTS.md"));
    }

    [Fact]
    public void Init_Yes_WritesAgentsMd()
    {
        var (app, fs, _) = Build(null, interactive: false);
        var r = app.Run("rules", "init", "--yes");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("에 규칙 2개를 썼습니다. 검토한 뒤 `pelly rules check`로 확인하세요.", r.Output);
        var bytes = fs.File.ReadAllBytes("/w/AGENTS.md");
        Assert.NotEqual(0xEF, bytes[0]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("# AGENTS.md\n\n## Pelly rules\n<!-- pelly rules init 초안 (2026-10-24)", text);
        var loaded = RulesLoader.Load(fs, WorkspaceLocator.Resolve(fs, "/w/pelly-rules"));
        Assert.Empty(loaded.Errors);
        Assert.Equal(2, loaded.Rules.Count);
    }

    [Fact]
    public void Init_Confirmed_Interactive_Writes()
    {
        var (app, fs, _) = Build(null);
        Assert.Equal(0, app.Run("rules", "init").ExitCode);
        Assert.True(fs.File.Exists("/w/AGENTS.md"));
    }

    [Fact]
    public void Init_ExistingSection_NoForce_Exit4()
    {
        var original = Utf8("# A\n\n## Pelly rules\n- [*] keep\n");
        var (app, fs, _) = Build(original);
        var r = app.Run("rules", "init", "--yes");
        Assert.Equal(4, r.ExitCode);
        Assert.Contains("이미 AGENTS.md에 Pelly rules 절이 있습니다", r.Output);
        Assert.Equal(original, fs.File.ReadAllBytes("/w/AGENTS.md"));
    }

    [Fact]
    public void Init_Force_SaysHowManyExistingRulesAreReplaced()
    {
        var (app, _, _) = Build(Utf8("# A\n\n## Pelly rules\n- [*] one\n- [*] two\n- [*] three\n"));
        var r = app.Run("rules", "init", "--force", "--yes");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("기존 절의 규칙 3개를 새 초안으로 바꿉니다", r.Output);
    }

    [Fact]
    public void Init_Force_ExistingSectionWithErrors_SaysSo()
    {
        var (app, _, _) = Build(Utf8("# A\n\n## Pelly rules\n- no tags here\n"));
        var r = app.Run("rules", "init", "--force", "--yes");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("기존 절(오류 있음)을 새 초안으로 바꿉니다", r.Output);
    }

    [Fact]
    public void Init_Force_ReplacesSection_KeepsEncodingAndRest()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var original = bom.Concat(Utf8("# A\r\n\r\n## Pelly rules\r\n- [*] old\r\n\r\n## Other\r\ntail\r\n")).ToArray();
        var (app, fs, _) = Build(original);
        var r = app.Run("rules", "init", "--force", "--yes");
        Assert.Equal(0, r.ExitCode);
        var bytes = fs.File.ReadAllBytes("/w/AGENTS.md");
        Assert.Equal(bom, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("# A\r\n\r\n## Pelly rules\r\n<!-- pelly rules init", text);
        Assert.EndsWith("\r\n\r\n## Other\r\ntail\r\n", text);
        Assert.DoesNotContain("old", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void Init_ForceDryRun_PreviewsWithoutWriting()
    {
        var original = Utf8("## Pelly rules\n- [*] old\n");
        var (app, fs, _) = Build(original);
        var r = app.Run("rules", "init", "--force", "--dry-run");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("[async]", r.Output);
        Assert.Equal(original, fs.File.ReadAllBytes("/w/AGENTS.md"));
    }

    [Fact]
    public void Init_NonInteractiveWithoutYes_Exit4()
    {
        var (app, fs, _) = Build(null, interactive: false);
        var r = app.Run("rules", "init");
        Assert.Equal(4, r.ExitCode);
        Assert.Contains("비대화형 실행에서는 --yes가 필요합니다.", r.Output);
        Assert.False(fs.File.Exists("/w/AGENTS.md"));
    }

    [Fact]
    public void Init_UserDeclines_Exit2_NoWrite()
    {
        var (app, fs, _) = Build(null, answer: false);
        var r = app.Run("rules", "init");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("쓰지 않았습니다.", r.Output);
        Assert.False(fs.File.Exists("/w/AGENTS.md"));
    }

    [Fact]
    public void Init_TooLargeAgentsMd_Exit4()
    {
        var (app, _, _) = Build(new byte[RulesLoader.MaxBytes + 1]);
        var r = app.Run("rules", "init", "--yes");
        Assert.Equal(4, r.ExitCode);
        Assert.Contains("AGENTS.md가 너무 큽니다 (256 KiB 초과)", r.Output);
    }

    [Fact]
    public void Init_UndecodableAgentsMd_Exit4NamesFile()
    {
        var (app, _, _) = Build([0xEF, 0xBB, 0xBF, 0xC3, 0x28]);
        var r = app.Run("rules", "init", "--yes");
        Assert.Equal(4, r.ExitCode);
        Assert.Contains("AGENTS.md", r.Output);
    }
}
