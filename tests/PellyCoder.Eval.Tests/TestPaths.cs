using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

internal static class TestPaths
{
    public static string EvalRoot { get; } = EvalPaths.FindRoot(AppContext.BaseDirectory)
        ?? throw new DirectoryNotFoundException("eval/ 폴더를 찾을 수 없습니다.");

    public static BenchmarkTask Task(string id) => BenchmarkTask.Load(Path.Combine(EvalPaths.TasksDir(EvalRoot), id + ".yaml"));
}
