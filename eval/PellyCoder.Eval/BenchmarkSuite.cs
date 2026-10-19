using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Eval;

/// <summary>One source folder of a suite, copied to <c>&lt;work&gt;/&lt;Name&gt;/</c>; a task's <c>file</c> starts with its name.</summary>
/// <param name="Path">Absolute.</param>
/// <param name="Exclude">Folder names (not paths) left out of the work copy at any depth, besides the built-in ones.</param>
public sealed record SuiteRoot(string Name, string Path, IReadOnlyList<string> Exclude);

/// <summary>A task whose <c>file</c> was resolved against a suite's roots.</summary>
/// <param name="SourceFile">The original target file (absolute).</param>
/// <param name="RelativeFile">The target inside <paramref name="Root"/>, with <c>/</c>.</param>
/// <param name="IsLegacy">A C# target whose nearest csproj is a Legacy (non-SDK) project: the one Legacy rule (restore, report).</param>
public sealed record SuiteTask(BenchmarkTask Task, SuiteRoot Root, string SourceFile, string RelativeFile, bool IsLegacy);

/// <summary>Result of <see cref="BenchmarkSuite.Load"/>: the suite, or every problem found (never both).</summary>
public sealed record SuiteLoad(BenchmarkSuite? Suite, IReadOnlyList<string> Errors);

/// <summary>
/// A benchmark suite: a folder with <c>suite.json</c> (roots, support files, results folder), <c>tasks/*.yaml</c> and
/// optional <c>judges/&lt;root&gt;/</c> projects. The repository's own 50 tasks are the suite <c>eval/benchmark</c>; field staff
/// point <c>--suite</c> at a suite describing their codebase. Paths in <c>suite.json</c> are relative to its folder or absolute.
/// </summary>
public sealed partial class BenchmarkSuite
{
    public const string FileName = "suite.json";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private BenchmarkSuite(string dir, string name, IReadOnlyList<SuiteRoot> roots, IReadOnlyList<string> supportFiles, string resultsDir)
    {
        Dir = dir;
        Name = name;
        Roots = roots;
        SupportFiles = supportFiles;
        ResultsDir = resultsDir;
    }

    /// <summary>The folder holding <c>suite.json</c> (absolute).</summary>
    public string Dir { get; }
    public string Name { get; }
    public IReadOnlyList<SuiteRoot> Roots { get; }

    /// <summary>Files copied to the top of every work folder by file name (absolute), e.g. <c>Directory.Build.props</c>, <c>global.json</c>.</summary>
    public IReadOnlyList<string> SupportFiles { get; }

    /// <summary>Default parent of a run's output folder (absolute).</summary>
    public string ResultsDir { get; }

    public string TasksDir => Path.Combine(Dir, "tasks");

    /// <summary>Judge test projects of a root, kept outside the root so its build, CI and pelly <c>--test</c> never see them.</summary>
    public string JudgesDir(SuiteRoot root) => Path.Combine(Dir, "judges", root.Name);

    /// <summary>Reads <c>&lt;dir&gt;/suite.json</c>; never throws for a bad suite, every problem goes to <see cref="SuiteLoad.Errors"/>.</summary>
    public static SuiteLoad Load(string dir)
    {
        if (FullPath(Environment.CurrentDirectory, dir) is not { } full) return new(null, [$"스위트 폴더 경로가 올바르지 않습니다: {dir}"]);
        var file = Path.Combine(full, FileName);
        if (!File.Exists(file)) return new(null, [$"{FileName}이 없습니다: {file}"]);

        SuiteFile? data;
        try
        {
            data = JsonSerializer.Deserialize<SuiteFile>(File.ReadAllText(file), Json);
        }
        catch (JsonException ex)
        {
            return new(null, [$"{file}: JSON 오류: {ex.Message}"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, [$"{file}을 읽을 수 없습니다: {ex.Message}"]);
        }
        if (data is null) return new(null, [$"{file}: 내용이 없습니다"]);

        var errors = new List<string>();
        if (data.FormatVersion != FormatVersion)
            errors.Add($"formatVersion은 {FormatVersion}이어야 합니다: {(data.FormatVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "없음")}");
        if (string.IsNullOrWhiteSpace(data.Name))
            errors.Add("name이 필요합니다");

        var roots = new List<SuiteRoot>();
        if (data.Roots is not { Count: > 0 })
            errors.Add("roots에 루트가 하나 이상 필요합니다");
        foreach (var (r, i) in (data.Roots ?? []).Select((r, i) => (r, i)))
        {
            if (r is null)
            {
                errors.Add($"roots[{i}]: 루트가 비어 있습니다(null)");
                continue;
            }
            var label = $"roots[{i}]" + (string.IsNullOrEmpty(r.Name) ? "" : $" ({r.Name})");
            var ok = true;
            if (r.Name is null || !RootName().IsMatch(r.Name))
            {
                errors.Add($"{label}: name은 영문자·숫자·_·-만 쓸 수 있습니다: {r.Name ?? "없음"}");
                ok = false;
            }
            else if (string.Equals(r.Name, "judges", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{label}: name 'judges'는 판정 프로젝트 폴더 이름이라 쓸 수 없습니다");
                ok = false;
            }
            else if (roots.Any(x => string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{label}: 루트 이름이 겹칩니다(대소문자 무시): {r.Name}");
                ok = false;
            }

            string? path = null;
            if (string.IsNullOrWhiteSpace(r.Path)) errors.Add($"{label}: path가 필요합니다");
            else if ((path = FullPath(full, r.Path)) is null) errors.Add($"{label}: path가 올바르지 않습니다: {r.Path}");
            else if (!Directory.Exists(path))
                errors.Add($"{label}: 폴더가 없습니다: {path}");
            else if (roots.FirstOrDefault(x => Overlaps(x.Path, path)) is { } other)
                errors.Add($"{label}: 루트 {other.Name}와 같은 폴더이거나 한쪽이 다른 쪽 안에 있습니다: {path}");
            else if (ok)
            {
                var exclude = r.Exclude ?? [];
                var badExclude = exclude.Where(e => string.IsNullOrWhiteSpace(e) || e.Contains('/') || e.Contains('\\')).ToList();
                if (badExclude.Count > 0)
                    errors.Add($"{label}: exclude는 폴더 이름 목록입니다(경로 불가): {string.Join(", ", badExclude)}");
                else
                    roots.Add(new SuiteRoot(r.Name!, TrimEnd(path), exclude));
            }
        }

        var support = new List<string>();
        foreach (var s in data.SupportFiles ?? [])
        {
            var path = string.IsNullOrWhiteSpace(s) ? null : FullPath(full, s);
            if (path is null || !File.Exists(path)) errors.Add($"supportFiles: 파일이 없습니다: {path ?? (string.IsNullOrWhiteSpace(s) ? "(빈 값)" : s)}");
            else if (support.Any(x => string.Equals(Path.GetFileName(x), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
                errors.Add($"supportFiles: 파일 이름이 겹칩니다(작업 폴더 한곳에 복사됨): {Path.GetFileName(path)}");
            else support.Add(path);
        }

        var results = FullPath(full, string.IsNullOrWhiteSpace(data.ResultsDir) ? "results" : data.ResultsDir);
        if (results is null) errors.Add($"resultsDir가 올바르지 않습니다: {data.ResultsDir}");
        if (errors.Count > 0) return new(null, errors);
        return new(new BenchmarkSuite(full, data.Name!, roots, support, TrimEnd(results!)), []);
    }

    /// <summary>
    /// Resolves a task's <c>file</c> (<c>&lt;root name&gt;/&lt;path in the root&gt;</c>, <c>/</c> only) to the original file.
    /// Null with <paramref name="error"/> (naming the task) for an unknown root, a path leaving the root, a missing file.
    /// </summary>
    public SuiteTask? Resolve(BenchmarkTask task, IFileSystem fs, out string? error)
    {
        error = null;
        var file = task.File ?? "";
        if (file.Contains('\0'))
            return Fail(out error, $"{task.Id}: file에 널 문자가 있습니다");
        if (file.Contains('\\'))
            return Fail(out error, $"{task.Id}: file의 경로 구분자는 /만 씁니다: {file}");
        var parts = file.Split('/');
        if (parts.Length < 2 || parts.Any(p => p is "" or "." or ".."))
            return Fail(out error, $"{task.Id}: file은 <루트 이름>/<루트 안 경로> 형식이어야 합니다(빈 칸·.·.. 불가): {file}");

        var root = Roots.FirstOrDefault(r => r.Name == parts[0]);
        if (root is null)
            return Fail(out error, $"{task.Id}: 알 수 없는 루트 '{parts[0]}' (스위트 루트: {string.Join(", ", Roots.Select(r => r.Name))}): {file}");

        var relative = string.Join('/', parts[1..]);
        var source = Path.GetFullPath(Path.Combine([root.Path, .. parts[1..]]));
        if (!source.StartsWith(root.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Fail(out error, $"{task.Id}: file이 루트 {root.Name} 밖을 가리킵니다: {file}");
        if (!fs.File.Exists(source))
            return Fail(out error, $"{task.Id}: 대상 파일이 없습니다: {source}");

        var isLegacy = task.Kind == "cs"
            && ProjectFileLocator.FindNearestCsproj(fs, source) is { } csproj
            && ProjectStyleDetector.Detect(fs, csproj) == ProjectStyle.Legacy;
        return new SuiteTask(task, root, source, relative, isLegacy);
    }

    private static SuiteTask? Fail(out string? error, string message)
    {
        error = message;
        return null;
    }

    /// <summary><paramref name="path"/> against <paramref name="baseDir"/> (or absolute), or null for a path the OS rejects (<c>\0</c>, …): Load never throws.</summary>
    private static string? FullPath(string baseDir, string path)
    {
        if (path.Contains('\0')) return null;
        try
        {
            return Path.GetFullPath(Path.Combine(baseDir, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string TrimEnd(string path) => Path.TrimEndingDirectorySeparator(path);

    /// <summary>Same folder, or one inside the other (case ignored: a suite must not depend on the file system's case rule).</summary>
    private static bool Overlaps(string a, string b)
    {
        var x = TrimEnd(a) + Path.DirectorySeparatorChar;
        var y = TrimEnd(b) + Path.DirectorySeparatorChar;
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\z")]
    private static partial Regex RootName();

    private sealed class SuiteFile
    {
        public int? FormatVersion { get; set; }
        public string? Name { get; set; }
        public List<RootEntry>? Roots { get; set; }
        public List<string>? SupportFiles { get; set; }
        public string? ResultsDir { get; set; }
    }

    private sealed class RootEntry
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public List<string>? Exclude { get; set; }
    }
}
