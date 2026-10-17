using System.IO.Abstractions;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Models;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>Notices from PREFLIGHT (server list failure, mixed newlines) and REVIEW (inline SQL).</summary>
public sealed class NoticeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-notice-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();

    public NoticeTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private RunContext Ctx(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return new RunContext("r1", new RunRequest(path, null, "M", null, "do it", true, false, null), new PellyOptions());
    }

    private async Task<(PhaseOutcome Outcome, RunContext Ctx)> Preflight(byte[] bytes)
    {
        var ctx = Ctx("A.cs", bytes);
        var phase = new PreflightPhase(_fs, new FileGuard(_fs, TimeProvider.System), _backend, new LlmOptions(), []);
        var outcome = await phase.ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Lock?.Dispose();
        return (outcome, ctx);
    }

    [Fact]
    public async Task TagsFailure_ReportsListError_NotModelMissing()
    {
        _backend.Status = new LlmServerStatus(true, "0.40.0", false, false, null, TimeSpan.Zero, "HTTP 500");

        var (outcome, ctx) = await Preflight("class A { }\n"u8.ToArray());

        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Contains("서버에서 모델 목록을 확인하지 못했습니다: HTTP 500 (pelly doctor)", ctx.Messages);
        Assert.DoesNotContain(ctx.Messages, m => m.Contains("서버") && m.Contains("에 없습니다"));
    }

    [Fact]
    public async Task ModelMissing_WithoutError_KeepsModelMissingMessage()
    {
        _backend.Status = new LlmServerStatus(true, "0.40.0", false, false, null, TimeSpan.Zero, null);

        var (_, ctx) = await Preflight("class A { }\n"u8.ToArray());

        Assert.Contains(ctx.Messages, m => m.Contains("에 없습니다"));
    }

    [Fact]
    public async Task MixedNewLines_Notice()
    {
        var (outcome, ctx) = await Preflight("a\r\nb\r\nc\nd\r\n"u8.ToArray());

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Contains("파일에 CRLF와 LF가 섞여 있습니다 — 수정하면 줄바꿈이 CRLF로 통일됩니다", ctx.Messages);
    }

    [Fact]
    public async Task MixedNewLines_MajorityLf_NamesLf()
    {
        var (_, ctx) = await Preflight("a\nb\nc\r\nd\n"u8.ToArray());

        Assert.Contains("파일에 CRLF와 LF가 섞여 있습니다 — 수정하면 줄바꿈이 LF로 통일됩니다", ctx.Messages);
    }

    [Fact]
    public async Task UniformNewLines_NoNotice()
    {
        var (_, ctx) = await Preflight("a\r\nb\r\n"u8.ToArray());

        Assert.DoesNotContain(ctx.Messages, m => m.Contains("섞여"));
    }

    private const string Before =
        "namespace D;\n\npublic class Repo\n{\n    public string Load(int id)\n    {\n        return \"x\";\n    }\n}\n";

    private RunContext ReviewCtx(string after)
    {
        var ctx = Ctx("B.cs", System.Text.Encoding.UTF8.GetBytes(Before));
        ctx.FilePath = Path.Combine(_root, "B.cs");
        ctx.Original = new FileSnapshot(ctx.FilePath, System.Text.Encoding.UTF8, false, "\n", true, "", Before);
        ctx.Current = ctx.Original with { Text = after };
        ctx.Candidate = new MemberCandidate("Repo.Load(int)", "Repo", new TextSpanInfo(5, 8));
        ctx.Language = new LanguageTarget(LanguageVersion.Latest, "C# (latest)");
        return ctx;
    }

    [Fact]
    public async Task Review_ShowsInlineSqlWarning()
    {
        var ctx = ReviewCtx(Before.Replace("\"x\"", "\"SELECT Id FROM T WHERE Id = \" + id"));

        var outcome = await new ReviewPhase(_ui).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Approved, outcome);
        Assert.Contains("인라인 SQL이 바뀌었습니다 — 빌드는 통과했지만 DB로 검증되지 않았습니다 (설계 §8.15)", _ui.Warnings);
        Assert.Single(_ui.Diffs);
    }

    [Fact]
    public async Task Review_NoSqlChange_NoWarning()
    {
        var ctx = ReviewCtx(Before.Replace("\"x\"", "\"y\""));

        await new ReviewPhase(_ui).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Empty(_ui.Warnings);
    }
}
