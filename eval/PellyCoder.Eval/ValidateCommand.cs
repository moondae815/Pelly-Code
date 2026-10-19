using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;

namespace PellyCoder.Eval;

/// <summary>
/// <c>validate [--suite &lt;folder&gt;] [--config &lt;pelly.json&gt;]</c>: <see cref="SuiteValidator"/> over a suite, printed as a header line,
/// a Markdown table of the issues (errors first, then warnings, each by task id) and a totals line. Exit 0 without errors
/// (warnings allowed), 4 with errors or when the suite cannot be read. Never touches the LLM or SQL; <c>--config</c> only supplies <c>sql.compatLevel</c>.
/// </summary>
public static class ValidateCommand
{
    public static int Run(EvalArguments args, TextWriter output, TextWriter error, IFileSystem? fs = null, IProcessRunner? processes = null)
    {
        fs ??= new FileSystem();
        var suiteDir = args.Suite ?? EvalPaths.FindSuite();
        if (suiteDir is null)
        {
            error.WriteLine($"eval/benchmark/{BenchmarkSuite.FileName}을 찾을 수 없습니다 — 저장소 안에서 실행하거나 --suite <폴더>를 지정하세요.");
            return 4;
        }
        var load = BenchmarkSuite.Load(suiteDir);
        if (load.Suite is not { } suite)
        {
            error.WriteLine("스위트 오류:\n  " + string.Join("\n  ", load.Errors));
            return 4;
        }
        if (!Directory.Exists(suite.TasksDir))
        {
            error.WriteLine($"작업 폴더가 없습니다: {suite.TasksDir}");
            return 4;
        }

        if (args.Config is not null && !File.Exists(args.Config))
        {
            error.WriteLine($"설정 파일이 없습니다: {args.Config}");
            return 4;
        }
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        var loaded = PellyOptionsLoader.Load(fs, PellyPaths.UserConfigPath(), args.Config, environment, new Dictionary<string, string?>());
        if (loaded.Options is not { } options)
        {
            error.WriteLine("설정 오류:\n  " + string.Join("\n  ", loaded.Errors));
            return 4;
        }

        var (tasks, loadErrors) = BenchmarkTask.TryLoadAll(suite.TasksDir);
        var issues = SuiteValidator.Validate(suite, tasks, loadErrors, fs, options.Sql.CompatLevel).ToList();
        var deepSkipped = false;
        if (args.Deep)
        {
            if (issues.Any(i => i.Level == IssueLevel.Error)) deepSkipped = true;
            else
            {
                ArgumentNullException.ThrowIfNull(processes);
                var resolved = tasks.Select(t => SuiteValidator.TryResolve(suite, t, fs, out _)).OfType<SuiteTask>().ToList();
                var verifier = new IndependentVerifier(fs, processes, options, null, suite: suite);
                var scratch = Path.Combine(suite.ResultsDir, "validate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
                output.WriteLine($"깊은 검사: 판정 테스트를 원본 복사본에서 실행합니다 ({scratch})");
                issues.AddRange(SuiteValidator.ValidateJudgesAsync(suite, resolved, verifier.RunJudgeTestsAsync, processes, scratch, CancellationToken.None).GetAwaiter().GetResult());
            }
        }

        int Count(string prefix) => tasks.Count(t => (t.Id ?? "").StartsWith(prefix, StringComparison.Ordinal));
        output.WriteLine($"스위트 {suite.Name} ({suite.Dir}) · 작업 {tasks.Count}건 · cs-legacy {Count("cs-legacy-")} · cs-modern {Count("cs-modern-")} · sql {Count("sql-")}");

        var ordered = issues.Where(i => i.Level == IssueLevel.Error).OrderBy(i => i.TaskId, StringComparer.Ordinal)
            .Concat(issues.Where(i => i.Level == IssueLevel.Warning).OrderBy(i => i.TaskId, StringComparer.Ordinal)).ToList();
        if (ordered.Count > 0)
        {
            var table = new StringBuilder();
            table.AppendLine();
            table.AppendLine("| 레벨 | 작업 | 내용 |");
            table.AppendLine("| --- | --- | --- |");
            foreach (var i in ordered)
                table.AppendLine($"| {(i.Level == IssueLevel.Error ? "오류" : "경고")} | {i.TaskId ?? "-"} | {Cell(i.Message)} |");
            output.Write(table.ToString());
            output.WriteLine();
        }

        var errors = ordered.Count(i => i.Level == IssueLevel.Error);
        if (deepSkipped) output.WriteLine("정적 검사에 오류가 있어 깊은 검사(--deep)는 하지 않았습니다.");
        output.WriteLine($"오류 {errors}건 · 경고 {ordered.Count - errors}건");
        return errors == 0 ? 0 : 4;
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
