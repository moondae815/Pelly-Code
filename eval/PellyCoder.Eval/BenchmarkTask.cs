using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PellyCoder.Eval;

/// <summary>One <c>eval/benchmark/tasks/*.yaml</c> entry (schema: eval/benchmark/README.md).</summary>
public sealed class BenchmarkTask
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";

    /// <summary><c>&lt;root name&gt;/&lt;path in the root&gt;</c>, with <c>/</c> only (resolved by <see cref="BenchmarkSuite.Resolve"/>).</summary>
    public string File { get; set; } = "";
    public string? Method { get; set; }
    public int? Line { get; set; }
    public string? Object { get; set; }
    public string Request { get; set; } = "";

    /// <summary>Legacy app kind (<c>mvc</c>, <c>adonet</c>, <c>dapper</c>) for the composition count; not an acceptance group.</summary>
    public string? Category { get; set; }
    public BenchmarkJudge? Judge { get; set; }

    /// <summary>The YAML file's name without extension (set by <see cref="Load"/>; <c>validate</c> compares it with <see cref="Id"/>).</summary>
    [YamlIgnore]
    public string? SourceName { get; set; }

    public static BenchmarkTask Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var task = deserializer.Deserialize<BenchmarkTask>(System.IO.File.ReadAllText(path))
            ?? throw new InvalidDataException("내용이 없습니다");
        // An empty YAML value (`id:`) deserializes to null despite the defaults; the checks and the runner expect strings.
        task.Id ??= "";
        task.Kind ??= "";
        task.File ??= "";
        task.Request ??= "";
        task.SourceName = Path.GetFileNameWithoutExtension(path);
        return task;
    }

    /// <summary>
    /// Like <see cref="LoadAll"/> but a file that cannot be read becomes an entry of <c>Errors</c>
    /// (<c>&lt;file name&gt;: YAML을 읽을 수 없음: …</c>) instead of an exception; the other files are still loaded.
    /// </summary>
    public static (IReadOnlyList<BenchmarkTask> Tasks, IReadOnlyList<string> Errors) TryLoadAll(string tasksDir)
    {
        var tasks = new List<BenchmarkTask>();
        var errors = new List<string>();
        foreach (var file in Directory.GetFiles(tasksDir, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                tasks.Add(Load(file));
            }
            catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                var detail = ex.InnerException?.Message ?? ex.Message;
                errors.Add($"{Path.GetFileName(file)}: YAML을 읽을 수 없음: {detail}");
            }
        }
        return ([.. tasks.OrderBy(t => t.Id, StringComparer.Ordinal)], errors);
    }

    /// <summary>
    /// Every <c>*.yaml</c> in <paramref name="tasksDir"/>, ordered by id; throws on a file that cannot be read. For the built-in
    /// suite's tests only: <c>run</c> and <c>validate</c> use <see cref="TryLoadAll"/> (a field suite must not crash them).
    /// </summary>
    public static IReadOnlyList<BenchmarkTask> LoadAll(string tasksDir) =>
        [.. Directory.GetFiles(tasksDir, "*.yaml").Select(Load).OrderBy(t => t.Id, StringComparer.Ordinal)];

}

public sealed class BenchmarkJudge
{
    public bool? Build { get; set; }
    public bool? SqlCompile { get; set; }
    /// <summary>Tests that must pass after the edit; searched in the suite's judge projects (<c>judges/&lt;root&gt;</c>) and the root.</summary>
    public List<string>? Tests { get; set; }

    /// <summary>Corpus tests that pin the pre-edit behaviour (<c>*_CurrentlyCancels</c>): they must fail after the edit.</summary>
    public List<string>? FailingTests { get; set; }
    public List<string>? MustContain { get; set; }
    public List<string>? MustAdd { get; set; }
    public List<string>? MustNotContain { get; set; }
}
