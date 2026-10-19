using System.IO.Abstractions;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

internal static class TestPaths
{
    /// <summary>The repository's built-in suite (<c>eval/benchmark</c>).</summary>
    public static BenchmarkSuite Suite { get; } = LoadSuite();

    public static BenchmarkTask Task(string id) => BenchmarkTask.Load(Path.Combine(Suite.TasksDir, id + ".yaml"));

    public static SuiteTask Resolve(string id) =>
        Suite.Resolve(Task(id), new FileSystem(), out var error) ?? throw new InvalidDataException(error);

    private static BenchmarkSuite LoadSuite()
    {
        var dir = EvalPaths.FindSuite(AppContext.BaseDirectory) ?? throw new DirectoryNotFoundException("eval/benchmark/suite.json을 찾을 수 없습니다.");
        var load = BenchmarkSuite.Load(dir);
        return load.Suite ?? throw new InvalidDataException(string.Join("; ", load.Errors));
    }
}
