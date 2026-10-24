using System.Diagnostics;
using System.Text.Json;

namespace S8SemanticLoad;

/// <summary>
/// s8 load &lt;sln|slnx|csproj&gt; [--repeat N] [--out file] [--detail] [--check-corpus a3] [--form name] [--once]
/// Exit: 0 measured, 2 usage, 3 an "MSBuild not found" diagnostic was seen, 1 other error.
/// </summary>
public static class Program
{
    public const string JsonStart = "--S8-JSON-START--";
    public const string JsonEnd = "--S8-JSON-END--";

    public static async Task<int> Main(string[] args)
    {
        ForceEnglishMessages();
        return await RunAsync(args);
    }

    /// <summary>
    /// Roslyn, the build host and MSBuild localize their messages (Korean on field PCs), and the issue
    /// classifier matches English words. Pin this process and its children to English.
    /// </summary>
    public static void ForceEnglishMessages()
    {
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        Environment.SetEnvironmentVariable("VSLANG", "1033");
        Environment.SetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "en");
    }

    /// <summary>A load that throws because the build host or MSBuild is missing is exit 3, like the diagnostic case.</summary>
    public static int ExitCodeFor(Exception ex) =>
        DiagnosticClassifier.Classify(ex.Message) == WorkspaceIssueKind.MsBuildNotFound ? 3 : 1;

    private sealed record Args(string Path, int Repeat, string? Out, bool Detail, bool CheckA3, string Form, bool Once);

    public static async Task<int> RunAsync(string[] args)
    {
        var parsed = Parse(args, out var error);
        if (parsed is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("사용법: s8 load <솔루션|프로젝트> [--repeat N] [--out 파일] [--detail] [--check-corpus a3] [--form 이름]");
            return 2;
        }
        try
        {
            return parsed.Once ? await OnceAsync(parsed) : await RepeatAsync(args, parsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var code = ExitCodeFor(ex);
            Console.Error.WriteLine(parsed.Detail ? ex.ToString()
                : code == 3 ? $"오류: {LoadReport.KindName(WorkspaceIssueKind.MsBuildNotFound)} (불러오기 중 예외)" : $"오류: {ex.GetType().Name}");
            return code;
        }
    }

    private static Args? Parse(string[] a, out string error)
    {
        error = "";
        if (a.Length < 2 || a[0] != "load") { error = "명령이 없습니다."; return null; }
        var path = a[1];
        if (!File.Exists(path)) { error = "경로가 없습니다."; return null; }
        int repeat = 3; string? outFile = null; bool detail = false, a3 = false, once = false; var form = "dev";
        for (var i = 2; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--repeat" when i + 1 < a.Length && int.TryParse(a[i + 1], out var n) && n > 0: repeat = n; i++; break;
                case "--out" when i + 1 < a.Length: outFile = a[++i]; break;
                case "--form" when i + 1 < a.Length: form = a[++i]; break;
                case "--check-corpus" when i + 1 < a.Length && a[i + 1] == "a3": a3 = true; i++; break;
                case "--detail": detail = true; break;
                case "--once": once = true; break;
                default: error = $"알 수 없는 옵션: {a[i]}"; return null;
            }
        }
        return new Args(Path.GetFullPath(path), repeat, outFile, detail, a3, form, once);
    }

    private static async Task<int> OnceAsync(Args a)
    {
        string? corpus = null, cases = null;
        if (a.CheckA3)
        {
            var root = FindRepoRoot(a.Path) ?? throw new InvalidOperationException("--check-corpus needs the repository (global.json) above the solution");
            corpus = Path.Combine(root, "eval", "corpus");
            cases = Path.Combine(root, "spikes", "S6-planning", "cases");
        }
        var r = await LoadRunner.RunOnceAsync(a.Path, new LoadOptions(a.Detail, a.CheckA3, a.Form, corpus, cases), CancellationToken.None);
        Console.WriteLine(JsonStart);
        Console.WriteLine(r.ToJson());
        Console.WriteLine(JsonEnd);
        return r.Issues.Any(i => i.Kind == WorkspaceIssueKind.MsBuildNotFound && i.Count > 0) ? 3 : 0;
    }

    private static async Task<int> RepeatAsync(string[] original, Args a)
    {
        var runs = new List<LoadReport>();
        var exit = 0;
        for (var i = 0; i < a.Repeat; i++)
        {
            var (code, stdout, stderr) = await RunChildAsync([.. original, "--once"]);
            var start = stdout.IndexOf(JsonStart, StringComparison.Ordinal);
            var end = stdout.IndexOf(JsonEnd, StringComparison.Ordinal);
            if (start < 0 || end < start)
            {
                Console.Error.WriteLine($"{i + 1}회: 결과 없음 (종료 {code}{(code == 3 ? ", " + LoadReport.KindName(WorkspaceIssueKind.MsBuildNotFound) : "")})");
                if (a.Out is not null)
                    await File.WriteAllTextAsync(a.Out, JsonSerializer.Serialize(new { runs = runs.Select(r => r.Sanitized()), failure = new { run = i + 1, exit = code } }, LoadReport.Json));
                if (a.Detail) Console.Error.WriteLine(stderr);
                return code == 0 ? 1 : code;
            }
            runs.Add(LoadReport.FromJson(stdout[(start + JsonStart.Length)..end]));
            if (code == 3) exit = 3;
            else if (code != 0 && exit == 0) exit = code;
        }
        var verdict = Verdict.Judge(runs);
        Console.Write(runs[^1].ToSummary());
        Console.WriteLine($"반복 {runs.Count}회 (첫 회 콜드, 나머지 웜)");
        foreach (var j in verdict)
            Console.WriteLine($"  {j.Item}: {(j.Pass is null ? "판정 안 함" : j.Pass.Value ? "통과" : "미달")} — {j.Value}");
        if (a.Out is not null)
        {
            var doc = new { runs = runs.Select(r => r.Sanitized()), verdict };
            await File.WriteAllTextAsync(a.Out, JsonSerializer.Serialize(doc, LoadReport.Json));
        }
        return exit;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunChildAsync(string[] args)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var psi = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo(self, [typeof(Program).Assembly.Location, .. args])
            : new ProcessStartInfo(self, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await outTask, await errTask);
    }

    private static string? FindRepoRoot(string from)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(from)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName;
    }
}
