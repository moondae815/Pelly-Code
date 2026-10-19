using System.IO.Abstractions;
using System.Xml.Linq;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;
using PellyCoder.Eval;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.Testing;

namespace PellyCoder.Integration.Tests.Benchmark;

/// <summary>
/// 동작 판정 정의 검사(Review Focus 1): 판정 테스트는 원본 코퍼스에서 실패하고(아무것도 고치지 않은 패치가 통과하면 안 된다),
/// <c>failingTests</c>는 원본에서 통과해야 한다(수정 전 동작을 실제로 고정하고 있다). 저장소의 eval/corpus를 건드리지 않도록
/// 임시 폴더에 작업 복사본을 만들고, 실행기와 같은 경로(<see cref="IndependentVerifier.RunJudgeTestsAsync"/>)로 실행한다.
/// </summary>
public class JudgeTestDefinitionTests
{
    private static readonly string EvalRoot = Directory.GetParent(CorpusPaths.Root)!.FullName;
    private static readonly BenchmarkSuite Suite = BenchmarkSuite.Load(Path.Combine(EvalRoot, "benchmark")).Suite
        ?? throw new InvalidDataException("내장 스위트(eval/benchmark/suite.json)를 읽을 수 없습니다");

    [Fact]
    public async Task JudgeTests_FailOnOriginal_And_FailingTestsPassOnOriginal()
    {
        var ct = TestContext.Current.CancellationToken;
        var tasks = BenchmarkTask.LoadAll(Suite.TasksDir)
            .Where(t => (t.Judge?.Tests?.Count ?? 0) + (t.Judge?.FailingTests?.Count ?? 0) > 0)
            .ToList();
        Assert.NotEmpty(tasks);
        // R1: 동작 판정은 Modern C# 작업에만 둔다.
        Assert.All(tasks, t => Assert.StartsWith("Modern/", t.File, StringComparison.Ordinal));

        var created = Path.Combine(Path.GetTempPath(), "pelly-judge-def-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(created);
        // Link-free root: through the macOS /var -> /private/var link the restore skips the transitive ProjectReference.
        var scratch = Path.Combine(TestRunnerSdkTests.RealPath(created), "work");
        try
        {
            var fs = new FileSystem();
            var resolved = tasks.Select(t => Suite.Resolve(t, fs, out var error) ?? throw new InvalidDataException(error)).ToList();
            var processes = new SystemProcessRunner(TimeProvider.System);
            var verifier = new IndependentVerifier(fs, processes, new PellyOptions(), null, suite: Suite);

            var issues = await SuiteValidator.ValidateJudgesAsync(Suite, resolved, verifier.RunJudgeTestsAsync, processes, scratch, ct);

            Assert.Empty(issues.Select(i => $"{i.Level} {i.TaskId}: {i.Message}"));
        }
        finally
        {
            try { Directory.Delete(created, recursive: true); }
            catch (IOException) { /* best effort: MSBuild node reuse may still hold files */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// R2: 판정 프로젝트는 솔루션에 들어가지 않고(원본에서 실패하는 테스트라 CI와 pelly <c>--test</c>를 깨뜨린다),
    /// <c>JudgeTargetRoot</c> 기본값은 저장소의 eval/corpus/Modern을 가리킨다.
    /// </summary>
    [Fact]
    public void JudgeProject_NotInSolutions_DefaultTargetIsTheCorpus()
    {
        var repoRoot = Directory.GetParent(EvalRoot)!.FullName;
        foreach (var sln in Directory.GetFiles(repoRoot, "*.sln").Concat(Directory.GetFiles(CorpusPaths.Root, "*.sln", SearchOption.AllDirectories)))
            Assert.DoesNotContain("JudgeTests", File.ReadAllText(sln), StringComparison.Ordinal);

        // Every judge project (Shop.Api.JudgeTests, Shop.Data.JudgeTests, …) has the same default and its own props.
        var projects = Directory.GetFiles(Suite.JudgesDir(Suite.Roots.Single(r => r.Name == "Modern")), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["Shop.Api.JudgeTests", "Shop.Data.JudgeTests"], projects.Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal));
        foreach (var project in projects)
        {
            var dir = Path.GetDirectoryName(project)!;
            var fallback = XDocument.Load(project).Descendants("JudgeTargetRoot").Single().Value
                .Replace("$(MSBuildThisFileDirectory)", dir + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(CorpusPaths.Get("Modern")), Path.GetFullPath(fallback));
            Assert.True(File.Exists(Path.Combine(dir, "Directory.Build.props")), project);
            Assert.True(File.Exists(Path.Combine(dir, "Directory.Packages.props")), project);
        }
    }
}
