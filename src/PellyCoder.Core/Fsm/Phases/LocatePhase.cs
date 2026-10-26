using System.IO.Abstractions;
using System.Xml;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Verification;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Resolves the project, its C# version and the single member to edit (by line, by name, or from the request text with a
/// model pick as the last resort — design §8.4), then the build tool. Unless this is a dry run it starts the baseline build
/// of the untouched project, which runs while SLICE and EDIT proceed (design §7.3), and with <c>--test</c> the baseline
/// tests right after it. A failed lookup returns before that. A task group run resolves no build tool and starts nothing.
/// </summary>
public sealed class LocatePhase(
    IFileSystem fs, IUserInteraction ui, BuildToolResolver tools, BuildVerifier builds, ModelMemberPicker picker,
    Func<bool>? isWindows = null, TestRunner? tests = null) : IPhase
{
    /// <summary>Longest member list the model is asked to choose from (design §8.4, step 5).</summary>
    private const int MaxPickMembers = 60;

    private readonly Func<bool> _isWindows = isWindows ?? OperatingSystem.IsWindows;

    public RunPhase Phase => RunPhase.Locate;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        if (ctx.Request.Line is not null && (ctx.Request.ClassName is not null || ctx.Request.MethodName is not null))
        {
            ctx.Messages.Add("--line은 --class/--method와 함께 쓸 수 없습니다");
            return PhaseOutcome.EnvError;
        }

        var csproj = ProjectFileLocator.FindNearestCsproj(fs, ctx.FilePath);
        try
        {
            ctx.Language = LanguageVersionDetector.Detect(fs, csproj);
            ctx.ProjectStyle = ProjectStyleDetector.Detect(fs, csproj).ToString();
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            ctx.Messages.Add($"프로젝트 파일을 읽을 수 없습니다: {csproj} ({ex.Message})");
            return PhaseOutcome.EnvError;
        }

        // Baseline for VERIFY (design §8.9): only errors beyond these count. Parsed once, with the detected version.
        ctx.OriginalSyntax = SyntaxBaseline.Of(ctx.Original.Text, ctx.Language.Version, fs.Path.GetFileName(ctx.FilePath));
        if (ctx.OriginalSyntax.Errors.Count > 0)
            ctx.Messages.Add($"원본 파일에 이미 구문 오류가 {ctx.OriginalSyntax.Errors.Count}개 있습니다 — 새로 생긴 오류만 검사합니다");

        var (outcome, chosen) = ctx.Request switch
        {
            { Line: { } line } => AtLine(ctx, line),
            { MethodName: { } method } => await ChooseAsync(ctx,
                MemberLocator.FindByName(ctx.Current.Text, ctx.Language.Version, ctx.Request.ClassName, method),
                ctx.Request.ClassName is null ? method : $"{ctx.Request.ClassName}.{method}", ct),
            _ => await FromRequestAsync(ctx, ct),
        };
        if (chosen is null)
            return outcome;

        ctx.Candidate = chosen;
        ctx.OriginalTargetDisplay = chosen.Display;
        ctx.Target = new EditTarget(EditKind.CSharp, ctx.FilePath, chosen.Display, chosen.Span, csproj);
        // A group run neither builds nor tests: the group builds the affected projects once at its end (design §6).
        if (ctx.Request.Group is not null) return PhaseOutcome.Ok;
        return await StartBaselineAsync(ctx, csproj, ct);
    }

    private async Task<PhaseOutcome> StartBaselineAsync(RunContext ctx, string? csproj, CancellationToken ct)
    {
        var style = Enum.Parse<ProjectStyle>(ctx.ProjectStyle);
        IReadOnlyList<string> declaredProjects = [];
        if (ctx.Request.TestFirst)
        {
            if (ctx.Request.DryRun)
            {
                ctx.Messages.Add($"테스트 우선 검증: {string.Join(", ", ctx.Request.VerifyTests!)} (dry-run에서는 실행하지 않음)");
                ctx.TestFirst = new TestFirstState(ctx.Request.VerifyTests!, ctx.Request.ExpectBreaks ?? [], new Dictionary<string, string>());
            }
            else if (TestFirstPreconditions(ctx, style, csproj) is { } projects)
                declaredProjects = projects;
            else
                return PhaseOutcome.EnvError;
        }

        if (style == ProjectStyle.None || csproj is null)
        {
            ctx.Messages.Add("csproj가 없어 빌드 검증을 건너뜁니다 (구문 검사만 합니다)");
            return PhaseOutcome.Ok;
        }
        if (style == ProjectStyle.Legacy && !_isWindows())
        {
            // MSBuild for .NET Framework exists only on Windows: verify syntax only, like a project without csproj.
            ctx.Messages.Add("레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증합니다 — 구문 검사만 합니다");
            if (ctx.Request.RunTests) ctx.Messages.Add("레거시 테스트는 Windows에서만 실행합니다 — 테스트를 건너뜁니다");
            return PhaseOutcome.Ok;
        }

        var resolution = await tools.ResolveAsync(style, ct);
        if (resolution.Tool is not { } tool)
        {
            ctx.Messages.Add(resolution.Error!);
            ctx.FailureKind = "BuildToolUnavailable";
            return PhaseOutcome.EnvError;
        }

        ctx.BuildTool = tool;
        if (ctx.Request.DryRun) return PhaseOutcome.Ok; // a dry run never builds or tests

        // A test-first run always tests, even without --test.
        if ((ctx.Request.RunTests || ctx.Request.TestFirst) && tests is not null && FindTestProjects(ctx, csproj!, declaredProjects) is { } failed)
            return failed;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ctx.BaselineCancellation = cts;
        // Started only; APPLY awaits it. The orchestrator cancels it and waits for it when the run ends.
        var baseline = Task.Run(() => builds.BuildAsync(tool, csproj, ctx.Workspace!.Root, cts.Token), CancellationToken.None);
        ctx.Baseline = baseline;
        if (ctx.TestProjects.Count > 0)
        {
            var projects = ctx.TestProjects;
            // After the baseline build, never alongside it: both would build the target into the same obj/ folder.
            ctx.BaselineTests = Task.Run(() => RunBaselineTestsAsync(ctx, tests!, tool, projects, baseline, cts.Token), CancellationToken.None);
        }
        return PhaseOutcome.Ok;
    }

    /// <summary>
    /// Sets <see cref="RunContext.TestProjects"/>: the target's test projects, then the projects declaring the named tests that
    /// are not among them (design §5.2a). Null to go on, or the outcome when the configured list is wrong.
    /// </summary>
    private PhaseOutcome? FindTestProjects(RunContext ctx, string csproj, IReadOnlyList<string> declaredProjects)
    {
        try
        {
            var found = TestProjectLocator.Find(fs, ctx.Workspace!.Root, csproj, ctx.Options.Test);
            ctx.TestProjects = found.Concat(declaredProjects.Select(fs.Path.GetFullPath)).Distinct(PathComparers.ForOs).ToList();
        }
        catch (FileNotFoundException ex)
        {
            ctx.Messages.Add(ex.Message);
            return PhaseOutcome.EnvError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Messages.Add($"테스트 프로젝트를 찾는 중 파일을 읽을 수 없습니다 ({ex.Message})");
            return PhaseOutcome.EnvError;
        }
        if (ctx.TestProjects.Count == 0)
            ctx.Messages.Add("테스트 프로젝트를 찾지 못해 테스트를 건너뜁니다 (빌드 결과로만 판정)");
        return null;
    }

    /// <summary>
    /// Checks a test-first run before anything starts (design §6) and records its <see cref="TestFirstState"/>. Returns the
    /// projects declaring the named tests, or null after adding the message and failure kind (the run ends with exit 4).
    /// </summary>
    private IReadOnlyList<string>? TestFirstPreconditions(RunContext ctx, ProjectStyle style, string? csproj)
    {
        IReadOnlyList<string>? Fail(string message, string kind)
        {
            ctx.Messages.Add(message);
            ctx.FailureKind = kind;
            return null;
        }

        if (style == ProjectStyle.None || csproj is null)
            return Fail("csproj가 없어 테스트 우선 검증을 할 수 없습니다", "TestUnverifiable");
        if (style == ProjectStyle.Legacy && !_isWindows())
            return Fail("레거시 테스트는 Windows에서만 실행합니다 — 테스트 우선 검증을 할 수 없습니다", "TestUnverifiable");

        var targetName = MemberLocator.NameOf(ctx.Candidate!.Display);
        var declarations = new List<TestMethodDecl>();
        foreach (var name in ctx.Request.VerifyTests!)
        {
            IReadOnlyList<TestMethodDecl> found;
            try
            {
                found = TestMethodIndex.Find(fs, ctx.Workspace!.Root, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ctx.Messages.Add($"테스트 메서드를 찾는 중 파일을 읽을 수 없습니다 ({ex.Message})");
                return null;
            }

            var fullNames = found.Select(d => d.FullName).Distinct(StringComparer.Ordinal).ToList();
            if (fullNames.Count == 0)
                return Fail($"테스트 {name}을(를) 테스트 실행 결과에서 찾지 못했습니다 (test.projects 설정을 확인하세요)", "TestNotFound");
            if (fullNames.Count > 1)
                return Fail($"테스트 이름 {name}이(가) 여러 테스트와 맞습니다: {string.Join(", ", fullNames)}", "TestAmbiguous");
            foreach (var decl in found)
            {
                var method = decl.FullName[(decl.FullName.LastIndexOf('.') + 1)..];
                if (PathComparers.ForOs.Equals(fs.Path.GetFullPath(decl.File), ctx.FilePath) && method == targetName)
                    return Fail("--verify-test가 수정 대상 멤버 자신을 가리킵니다", "TestUnverifiable");
                if (decl.Csproj is null)
                    return Fail($"테스트 {name}이(가) 어떤 테스트 프로젝트에도 속하지 않습니다", "TestUnverifiable");
            }
            declarations.AddRange(found);
        }

        // Hashed now, before the first write, so the end of the run can tell whether the tests changed (design §5.6).
        var hashes = new Dictionary<string, string>(PathComparers.ForOs);
        try
        {
            foreach (var file in declarations.GroupBy(d => fs.Path.GetFullPath(d.File), PathComparers.ForOs))
                hashes[file.Key] = TestFirstState.HashOf(fs, file.Key, ctx.FilePath, file.Select(d => d.FullName).ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Messages.Add($"테스트 파일을 읽을 수 없습니다 ({ex.Message})");
            return null;
        }

        ctx.TestFirst = new TestFirstState(ctx.Request.VerifyTests!, ctx.Request.ExpectBreaks ?? [], hashes)
        {
            DeclaredNames = declarations.Select(d => d.FullName).ToHashSet(StringComparer.Ordinal),
        };
        return declarations.Select(d => d.Csproj!).ToList();
    }

    private static async Task<IReadOnlyList<TestRun>> RunBaselineTestsAsync(RunContext ctx, TestRunner runner, BuildTool tool,
        IReadOnlyList<string> projects, Task<BuildRun> baseline, CancellationToken ct)
    {
        try
        {
            await baseline;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return []; // APPLY reports the failed baseline build and ends the run; these results would never be used
        }

        var runs = new List<TestRun>();
        for (var i = 0; i < projects.Count; i++)
            runs.Add(await runner.RunAsync(tool, projects[i], ctx.Workspace!.Root, TestStep.TrxPath(ctx, $"baseline-{i + 1}"), ct));
        return runs;
    }

    private static (PhaseOutcome, MemberCandidate?) AtLine(RunContext ctx, int line)
    {
        if (MemberLocator.FindAtLine(ctx.Current.Text, ctx.Language!.Version, line) is { } found)
            return (PhaseOutcome.Ok, found);
        ctx.Messages.Add($"{line}번 줄은 메서드 안이 아닙니다. 메서드 안에 커서를 두고 실행하세요");
        ctx.FailureKind = "TargetNotFound";
        return (PhaseOutcome.NotFound, null);
    }

    /// <summary>Members named in the request; with none, the model picks from the member list (design §8.4, steps 4-5).</summary>
    private async Task<(PhaseOutcome, MemberCandidate?)> FromRequestAsync(RunContext ctx, CancellationToken ct)
    {
        var members = MemberLocator.AllMembers(ctx.Current.Text, ctx.Language!.Version)
            .Where(m => ctx.Request.ClassName is null || InnermostType(m) == ctx.Request.ClassName)
            .ToList();
        var identifiers = IdentifierExtractor.Candidates(ctx.Request.Request).ToHashSet(StringComparer.Ordinal);
        var named = members.Where(m => identifiers.Contains(MemberLocator.NameOf(m.Display))).ToList();
        if (named.Count == 1)
        {
            ctx.Messages.Add($"대상: {named[0].Display} (요청문에서 찾음)");
            return (PhaseOutcome.Ok, named[0]);
        }
        if (named.Count > 1)
            return await ChooseAsync(ctx, named, string.Join(", ", named.Select(m => MemberLocator.NameOf(m.Display)).Distinct()), ct);

        if (members.Count > MaxPickMembers)
            return NotFound(ctx, $"멤버가 {MaxPickMembers}개를 넘어 자동으로 고를 수 없습니다. --method나 --line을 쓰세요");

        MemberCandidate? picked = null;
        if (members.Count > 0)
        {
            try
            {
                picked = await picker.PickAsync(members, ctx.Request.Request, ct);
            }
            catch (LlmUnavailableException ex)
            {
                ctx.Messages.Add($"LLM 서버를 사용할 수 없습니다: {ex.Message} (`pelly doctor`로 확인하세요)");
                return (PhaseOutcome.EnvError, null);
            }
        }
        if (picked is null)
            return NotFound(ctx, "요청문에서 대상 멤버를 찾지 못했습니다. --method나 --line을 쓰세요");

        ctx.Messages.Add($"대상: {picked.Display} (모델이 선택)");
        return (PhaseOutcome.Ok, picked);
    }

    /// <summary>One candidate is the target; several go to the selection UI, or fail as ambiguous when not interactive.</summary>
    private async Task<(PhaseOutcome, MemberCandidate?)> ChooseAsync(RunContext ctx, IReadOnlyList<MemberCandidate> candidates, string name,
        CancellationToken ct)
    {
        switch (candidates.Count)
        {
            case 0:
                return NotFound(ctx, $"대상 멤버 '{name}'을(를) 찾을 수 없습니다: {ctx.FilePath}");
            case 1:
                return (PhaseOutcome.Ok, candidates[0]);
            default:
                if (!ui.IsInteractive)
                {
                    ctx.Messages.Add($"'{name}'과(와) 일치하는 멤버가 {candidates.Count}개입니다. --class로 좁히거나 대화형으로 실행하세요: "
                        + string.Join(", ", candidates.Select(Label)));
                    ctx.FailureKind = "TargetAmbiguous";
                    return (PhaseOutcome.Ambiguous, null);
                }
                return (PhaseOutcome.Ok, await ui.SelectAsync("수정할 멤버를 고르세요", candidates, Label, ct));
        }
    }

    private static (PhaseOutcome, MemberCandidate?) NotFound(RunContext ctx, string message)
    {
        ctx.Messages.Add(message);
        ctx.FailureKind = "TargetNotFound";
        return (PhaseOutcome.NotFound, null);
    }

    private static string InnermostType(MemberCandidate m) => m.ContainingType[(m.ContainingType.LastIndexOf('.') + 1)..];

    private static string Label(MemberCandidate c) => $"{c.Display} (줄 {c.Span.StartLine}-{c.Span.EndLine})";
}
