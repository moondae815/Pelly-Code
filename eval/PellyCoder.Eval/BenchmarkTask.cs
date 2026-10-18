using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PellyCoder.Eval;

/// <summary>One <c>eval/benchmark/tasks/*.yaml</c> entry (schema: eval/benchmark/README.md).</summary>
public sealed class BenchmarkTask
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";

    /// <summary>Path relative to <c>eval/</c>, with forward slashes.</summary>
    public string File { get; set; } = "";
    public string? Method { get; set; }
    public int? Line { get; set; }
    public string? Object { get; set; }
    public string Request { get; set; } = "";

    /// <summary>Legacy app kind (<c>mvc</c>, <c>adonet</c>, <c>dapper</c>) for the composition count; not an acceptance group.</summary>
    public string? Category { get; set; }
    public BenchmarkJudge? Judge { get; set; }

    /// <summary>The one Legacy rule everywhere (runner, report): the target lives in the Legacy corpus.</summary>
    public bool IsLegacy => File.StartsWith("corpus/Legacy/", StringComparison.Ordinal);

    public static BenchmarkTask Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<BenchmarkTask>(System.IO.File.ReadAllText(path));
    }

    /// <summary>Every <c>*.yaml</c> in <paramref name="tasksDir"/>, ordered by id.</summary>
    public static IReadOnlyList<BenchmarkTask> LoadAll(string tasksDir) =>
        [.. Directory.GetFiles(tasksDir, "*.yaml").Select(Load).OrderBy(t => t.Id, StringComparer.Ordinal)];
}

public sealed class BenchmarkJudge
{
    public bool? Build { get; set; }
    public bool? SqlCompile { get; set; }
    /// <summary>Tests that must pass after the edit; searched in the judge projects (<c>eval/benchmark/judges</c>) and the corpus.</summary>
    public List<string>? Tests { get; set; }

    /// <summary>Corpus tests that pin the pre-edit behaviour (<c>*_CurrentlyCancels</c>): they must fail after the edit.</summary>
    public List<string>? FailingTests { get; set; }
    public List<string>? MustContain { get; set; }
    public List<string>? MustAdd { get; set; }
    public List<string>? MustNotContain { get; set; }
}
