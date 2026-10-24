using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

public sealed class FsmTaskExecutorTests
{
    private static readonly Workspace Ws = new(Path.GetFullPath("/ws"), Path.GetFullPath("/ws/.pelly"));
    private static readonly TaskRunOptions Opts = new(true, false, 2, true);

    private static SpecTask Task(TaskKind kind, string path, string? symbol) =>
        new("T1", kind, path, symbol, "요청", [], null, false, null, null, 1);

    private static async Task<RunRequest> Run(SpecTask task, TaskRunOptions? options = null)
    {
        RunRequest? seen = null;
        var exec = new FsmTaskExecutor((r, _) =>
        {
            seen = r;
            return System.Threading.Tasks.Task.FromResult(new RunResult(0, "R", r.FilePath, null, null, null));
        });
        await exec.ExecuteAsync(task, Ws, options ?? Opts, CancellationToken.None);
        return seen!;
    }

    [Fact]
    public async Task Cs_task_maps_type_and_member()
    {
        var r = await Run(Task(TaskKind.CSharp, "src/A.cs", "OrderService.Cancel"));
        Assert.Equal("OrderService", r.ClassName);
        Assert.Equal("Cancel", r.MethodName);
        Assert.Equal(EditKind.CSharp, r.Kind);
        Assert.Equal("요청", r.Request);
        Assert.True(r.Yes);
        Assert.Equal(2, r.MaxRetries);
        Assert.True(r.Verbose);
        Assert.Null(r.Group);
    }

    [Fact]
    public async Task Group_and_member_override_reach_the_request()
    {
        var feedback = new Feedback("BUILD FAILED (1 new error)", 5);
        var r = await Run(Task(TaskKind.CSharp, "src/A.cs", "OrderService.GetOrder"),
            Opts with { Group = new GroupRun(feedback), MemberOverride = "FindOrder" });
        Assert.Equal("OrderService", r.ClassName);
        Assert.Equal("FindOrder", r.MethodName);
        Assert.Same(feedback, r.Group!.InitialFeedback);
    }

    [Fact]
    public async Task Sql_task_maps_object_name_and_kind()
    {
        var r = await Run(Task(TaskKind.Sql, "db/a.sql", "dbo.usp_X"));
        Assert.Equal(EditKind.Sql, r.Kind);
        Assert.Equal("dbo.usp_X", r.ObjectName);
        Assert.Null(r.ClassName);
        Assert.Null(r.MethodName);
    }

    [Fact]
    public async Task Backslash_path_is_normalized_and_host_is_set()
    {
        var r = await Run(Task(TaskKind.CSharp, @"src\Sub\A.cs", null));
        Assert.Equal(Path.Combine(Ws.Root, "src", "Sub", "A.cs"), r.FilePath);
        Assert.Equal(Ws, r.Host!.Workspace);
        Assert.Null(r.MethodName);
    }
}
