using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Config;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

public class SpecCommandTests
{
    /// <summary>A CommandAppTester disposes its console after one run, so every Run builds a fresh app over the same file system.</summary>
    private sealed class Harness(MockFileSystem fs)
    {
        public MockFileSystem Fs { get; } = fs;

        public FakeFinder Finder { get; } = new();

        public CommandAppResult Run(params string[] args) => Create().Run(args);

        private CommandAppTester Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IFileSystem>(Fs);
            services.AddSingleton<IAnsiConsole>(new TestConsole().Width(200));
            services.AddSingleton<OptionsResolver>();
            services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
            services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
            services.AddSingleton<IImpactFinder>(Finder);
            var app = new CommandAppTester(new DiTypeRegistrar(services));
            app.Configure(CliApp.Configure);
            return app;
        }
    }

    private static Harness Build()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/w/.git");
        fs.AddFile("/w/src/A.sql", new MockFileData("CREATE PROCEDURE dbo.P AS SELECT 1;"));
        fs.Directory.SetCurrentDirectory("/w");
        return new Harness(fs);
    }

    private static string Spec(string tasks) =>
        "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks;

    [Fact]
    public void New_creates_template_that_parses_without_errors_except_no_tasks()
    {
        var h = Build();

        var result = h.Run("spec", "new", "demo");

        Assert.Equal(0, result.ExitCode);
        Assert.True(h.Fs.File.Exists("/w/changes/demo.md"));
        var spec = ChangeSpecParser.Parse(h.Fs.File.ReadAllText("/w/changes/demo.md").Replace("\r\n", "\n"));
        Assert.Equal("demo", spec.Id);
        Assert.Equal(["NoTasks"], spec.Issues.Select(i => i.Code).ToArray());
    }

    [Theory]
    [InlineData("Bad")]
    [InlineData("-x")]
    [InlineData("a_b")]
    [InlineData("a b")]
    public void New_rejects_bad_id(string id)
    {
        var h = Build();

        Assert.Equal(4, h.Run("spec", "new", id).ExitCode);
        Assert.False(h.Fs.Directory.Exists("/w/changes"));
    }

    [Fact]
    public void New_rejects_too_long_id_and_existing_file()
    {
        var h = Build();

        Assert.Equal(4, h.Run("spec", "new", new string('a', 65)).ExitCode);
        Assert.Equal(0, h.Run("spec", "new", "demo").ExitCode);
        h.Fs.File.WriteAllText("/w/changes/demo.md", "mine");

        Assert.Equal(4, h.Run("spec", "new", "demo").ExitCode);
        Assert.Equal("mine", h.Fs.File.ReadAllText("/w/changes/demo.md"));
    }

    [Fact]
    public void Check_exits_4_on_errors_and_0_on_warnings_only()
    {
        var h = Build();
        h.Fs.AddFile("/w/changes/bad.md", new MockFileData(Spec("- [ ] T1 sql src/missing.sql 바꾼다\n")));
        h.Fs.AddFile("/w/changes/warn.md", new MockFileData(
            "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n  test: NoSuchTest\n\n## 작업\n- [ ] T1 sql src/A.sql 바꾼다 (AC1)\n"));

        var bad = h.Run("spec", "check", "changes/bad.md");
        Assert.Equal(4, bad.ExitCode);
        Assert.Contains("FileNotFound", bad.Output);
        Assert.Contains("줄 11", bad.Output);

        var warn = h.Run("spec", "check", "changes/warn.md");
        Assert.Equal(0, warn.ExitCode);
        Assert.Contains("TestNotFound", warn.Output);
    }

    [Fact]
    public void Check_impact_searches_every_cs_task_and_only_warns()
    {
        var h = Build();
        h.Fs.AddFile("/w/src/Order.cs", new MockFileData("namespace App;\npublic class Order\n{\n    public void Cancel() { }\n    public void Ship() { }\n}\n"));
        h.Fs.AddFile("/w/changes/c.md", new MockFileData(Spec(
            "- [ ] T1 cs src/Order.cs#Order.Cancel 바꾼다\n- [ ] T2 cs src/Order.cs#Order.Ship 바꾼다\n- [ ] T3 sql src/A.sql 바꾼다\n")));
        h.Finder.Result = new ImpactResult(ImpactMode.Semantic, null, [new ImpactTarget("T1", "src/Web.cs", "Detail", "호출")], []);

        var plain = h.Run("spec", "check", "changes/c.md");
        Assert.Equal(0, plain.ExitCode);
        Assert.Empty(h.Finder.Calls); // no group, no --impact

        var result = h.Run("spec", "check", "changes/c.md", "--impact");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["T1", "T2"], Assert.Single(h.Finder.Calls).Select(s => s.TaskId));
        Assert.Contains("[ImpactMissing] 작업 T1의 영향 대상이 명세에 없습니다: src/Web.cs#Detail (호출)", result.Output);
    }

    [Fact]
    public void Check_without_impact_searches_only_group_cs_tasks()
    {
        var h = Build();
        h.Fs.AddFile("/w/src/Order.cs", new MockFileData("namespace App;\npublic class Order\n{\n    public void Cancel() { }\n    public void Ship() { }\n    public void Pack() { }\n}\n"));
        h.Fs.AddFile("/w/changes/g.md", new MockFileData(Spec(
            "- [ ] T1 cs src/Order.cs#Order.Cancel 바꾼다 [G1]\n- [ ] T2 cs src/Order.cs#Order.Ship 바꾼다 [G1]\n" +
            "- [ ] T3 cs src/Order.cs#Order.Pack 바꾼다\n- [ ] T4 sql src/A.sql 바꾼다\n")));
        h.Finder.Result = new ImpactResult(ImpactMode.Semantic, null, [new ImpactTarget("T2", "src/Web.cs", "Detail", "호출")], []);

        var result = h.Run("spec", "check", "changes/g.md");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["T1", "T2"], Assert.Single(h.Finder.Calls).Select(s => s.TaskId));
        Assert.Contains("[ImpactMissing] 작업 T2의 영향 대상이 명세에 없습니다: src/Web.cs#Detail (호출)", result.Output);
    }

    [Fact]
    public void Check_missing_file_exits_4()
    {
        Assert.Equal(4, Build().Run("spec", "check", "changes/none.md").ExitCode);
    }

    [Fact]
    public void Option_without_value_is_rejected_for_spec_branch()
    {
        Assert.Equal("--config", CliApp.FindOptionWithoutValue(["spec", "check", "changes/x.md", "--config"]));
        Assert.Null(CliApp.FindOptionWithoutValue(["spec", "check", "changes/x.md", "--config", "/w/pelly.json"]));
        Assert.Null(CliApp.FindOptionWithoutValue(["spec"]));
    }

    [Fact]
    public void Bare_spec_is_a_usage_error_4()
    {
        Assert.Equal(4, Build().Run("spec").ExitCode);
    }

    private sealed class FakeFinder : IImpactFinder
    {
        public List<IReadOnlyList<ImpactSeed>> Calls { get; } = [];
        public ImpactResult Result { get; set; } = new(ImpactMode.Semantic, null, [], []);

        public Task<ImpactResult> FindAsync(Workspace ws, IReadOnlyList<ImpactSeed> seeds, CancellationToken ct)
        {
            Calls.Add(seeds);
            return Task.FromResult(Result);
        }
    }
}
