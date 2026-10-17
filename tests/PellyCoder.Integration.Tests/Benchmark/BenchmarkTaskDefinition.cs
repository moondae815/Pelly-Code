using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PellyCoder.Integration.Tests.Benchmark;

/// <summary>eval/benchmark/tasks/*.yaml 한 건 (스키마: eval/benchmark/README.md)</summary>
public sealed class BenchmarkTaskDefinition
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string File { get; set; } = "";
    public string? Method { get; set; }
    public int? Line { get; set; }
    public string? Object { get; set; }
    public string Request { get; set; } = "";
    public BenchmarkJudge? Judge { get; set; }

    public static BenchmarkTaskDefinition Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<BenchmarkTaskDefinition>(System.IO.File.ReadAllText(path));
    }
}

public sealed class BenchmarkJudge
{
    public bool? Build { get; set; }
    public bool? SqlCompile { get; set; }
    public List<string>? Tests { get; set; }
    public List<string>? MustContain { get; set; }
    public List<string>? MustNotContain { get; set; }
}
