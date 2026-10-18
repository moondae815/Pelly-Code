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

    [Fact]
    public async Task JudgeTests_FailOnOriginal_And_FailingTestsPassOnOriginal()
    {
        var ct = TestContext.Current.CancellationToken;
        var tasks = BenchmarkTask.LoadAll(Path.Combine(EvalRoot, "benchmark", "tasks"))
            .Where(t => (t.Judge?.Tests?.Count ?? 0) + (t.Judge?.FailingTests?.Count ?? 0) > 0)
            .ToList();
        Assert.NotEmpty(tasks);
        // R1: 동작 판정은 Modern C# 작업에만 둔다.
        Assert.All(tasks, t => Assert.StartsWith("corpus/Modern/", t.File, StringComparison.Ordinal));

        var created = Path.Combine(Path.GetTempPath(), "pelly-judge-def-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(created);
        // Link-free root: through the macOS /var -> /private/var link the restore skips the transitive ProjectReference.
        var workRoot = Path.Combine(TestRunnerSdkTests.RealPath(created), "work");
        try
        {
            WorkCopy.Prepare(EvalRoot, tasks[0], workRoot);
            var corpusRoot = Path.Combine(workRoot, "Modern");
            var processes = new SystemProcessRunner(TimeProvider.System);
            var restore = await processes.RunAsync(new ProcessSpec("dotnet",
                ["restore", Path.Combine(corpusRoot, "Modern.sln"), "--nologo", "--disable-parallel"], corpusRoot, TimeSpan.FromMinutes(5)), ct);
            Assert.True(restore.ExitCode == 0, restore.StdOut + restore.StdErr);

            var tests = tasks.SelectMany(t => t.Judge!.Tests ?? []).Distinct().ToList();
            var failing = tasks.SelectMany(t => t.Judge!.FailingTests ?? []).Distinct().ToList();
            var run = await new IndependentVerifier(new FileSystem(), processes, new PellyOptions(), null, evalRoot: EvalRoot)
                .RunJudgeTestsAsync(tests, failing, corpusRoot, ct);

            Assert.Null(run.Error);
            var problems = new List<string>();
            foreach (var t in tasks)
            {
                foreach (var name in t.Judge!.Tests ?? [])
                    if (!run.Results.TryGetValue(name, out var r)) problems.Add($"{t.Id}: 판정 테스트가 결과에 없음 {name}");
                    else if (r.Passed) problems.Add($"{t.Id}: 판정 테스트가 원본에서 통과함 {name}");
                foreach (var name in t.Judge.FailingTests ?? [])
                    if (!run.Results.TryGetValue(name, out var r)) problems.Add($"{t.Id}: failingTests가 결과에 없음 {name}");
                    else if (!r.Passed) problems.Add($"{t.Id}: failingTests가 원본에서 실패함 {name} ({r.Message})");
            }
            Assert.Empty(problems);
            // The judge project was built from the work copy, never in place.
            Assert.True(File.Exists(Path.Combine(workRoot, "judges", "Modern", "Shop.Api.JudgeTests", "Shop.Api.JudgeTests.csproj")));
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
        var projects = Directory.GetFiles(Path.Combine(EvalRoot, "benchmark", "judges", "Modern"), "*.csproj", SearchOption.AllDirectories)
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
