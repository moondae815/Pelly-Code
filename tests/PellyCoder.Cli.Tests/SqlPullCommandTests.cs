using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Sql;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

public class SqlPullCommandTests
{
    private const string Prod = "Server=proddb01;Database=Shop;Integrated Security=true";
    private const string Dev = "Server=devdb01;Database=ShopDev;Integrated Security=true";
    private const string Definition = "CREATE PROCEDURE [dbo].[usp_X]\nAS\nSELECT 1\n";

    private sealed class FakeReader(string cs) : ISqlDefinitionReader
    {
        public string ConnectionString { get; } = cs;
        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => Task.FromResult(new SqlServerProbe("16", "x", true));

        public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct) =>
            name.Name == "usp_Bad"
                ? throw new QueryError("The SELECT permission was denied on the object 'sql_modules'.")
                : Task.FromResult<SqlModuleDefinition?>(new SqlModuleDefinition(new SqlObjectName("dbo", name.Name), "P",
                    ConnectionString == Dev ? Definition + "-- dev\n" : Definition, true, true));

        public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Stands in for a query-level SqlException (no public constructor).</summary>
    private sealed class QueryError(string message) : System.Data.Common.DbException(message);

    private sealed class FakeGit(int exitCode, bool missing = false) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            if (missing) throw new ProcessStartException(spec.FileName, new InvalidOperationException());
            return Task.FromResult(new ProcessResult(exitCode, "", "", [], TimeSpan.Zero, false));
        }
    }

    private sealed class Fixture(string? config, FakeGit git)
    {
        public MockFileSystem Fs { get; } = Create(config);
        public List<string> Connections { get; } = [];
        public FakeGit Git { get; } = git;

        private static MockFileSystem Create(string? config)
        {
            var fs = new MockFileSystem();
            fs.AddDirectory("/w/.git");
            if (config is not null) fs.AddFile("/w/pelly.json", new MockFileData(config));
            fs.Directory.SetCurrentDirectory("/w");
            return fs;
        }

        public (int Exit, string Output) Run(params string[] args)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IFileSystem>(Fs);
            services.AddSingleton<IAnsiConsole>(new TestConsole().Width(300));
            services.AddSingleton<OptionsResolver>();
            services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
            services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IProcessRunner>(Git);
            Func<string, ISqlDefinitionReader> factory = cs => { Connections.Add(cs); return new FakeReader(cs); };
            services.AddSingleton(factory);
            var app = new CommandAppTester(new DiTypeRegistrar(services));
            app.Configure(CliApp.Configure);
            var result = app.Run(args);
            return (result.ExitCode, result.Output);
        }
    }

    private static string Config(bool prod = true, bool dev = true) =>
        "{ \"sql\": {" +
        (dev ? $"\"connectionString\": \"{Dev}\", \"allowedHosts\": [\"devdb01\"], \"allowedDatabases\": [\"ShopDev\"]" : "\"allowedHosts\": []") +
        (prod ? $", \"production\": {{ \"connectionString\": \"{Prod}\", \"allowedHosts\": [\"proddb01\"], \"allowedDatabases\": [\"Shop\"] }}" : "") +
        "} }";

    [Fact]
    public void Prod_not_configured_exits_4_with_guidance()
    {
        var f = new Fixture(Config(prod: false), new FakeGit(0));

        var (exit, output) = f.Run("sql", "pull", "dbo.usp_X");

        Assert.Equal(4, exit);
        Assert.Contains("sql.production", output);
        Assert.Empty(f.Connections);
    }

    [Fact]
    public void Query_error_fails_that_object_others_continue_exit_4()
    {
        var f = new Fixture(Config(), new FakeGit(0));

        var (exit, output) = f.Run("sql", "pull", "dbo.usp_Bad", "dbo.usp_X");

        Assert.Equal(4, exit);
        Assert.Contains("실패: dbo.usp_Bad의 정의를 읽지 못했습니다: The SELECT permission was denied", output);
        Assert.True(f.Fs.File.Exists("/w/db/pulled/dbo.usp_X.sql"), output);
        Assert.Contains("1개 받음, 1개 실패", output);
    }

    [Fact]
    public void Prod_pull_writes_file_in_default_folder()
    {
        var f = new Fixture(Config(), new FakeGit(0));

        var (exit, output) = f.Run("sql", "pull", "dbo.usp_X");

        Assert.Equal(0, exit);
        Assert.True(f.Fs.File.Exists("/w/db/pulled/dbo.usp_X.sql"), output);
        Assert.Contains(Prod, f.Connections);
        Assert.Contains("source=prod server=proddb01 database=Shop", f.Fs.File.ReadAllText("/w/db/pulled/dbo.usp_X.sql"));
    }

    [Fact]
    public void From_dev_uses_dev_connection()
    {
        var f = new Fixture(Config(prod: false), new FakeGit(0));

        var (exit, _) = f.Run("sql", "pull", "dbo.usp_X", "--from", "DEV");

        Assert.Equal(0, exit);
        Assert.Equal([Dev], f.Connections);
        Assert.Contains("source=dev server=devdb01 database=ShopDev", f.Fs.File.ReadAllText("/w/db/pulled/dbo.usp_X.sql"));
    }

    [Fact]
    public void Unknown_from_value_exits_4_before_connecting()
    {
        var f = new Fixture(Config(), new FakeGit(0));

        var (exit, output) = f.Run("sql", "pull", "dbo.usp_X", "--from", "staging");

        Assert.Equal(4, exit);
        Assert.Contains("--from", output);
        Assert.Empty(f.Connections);
    }

    [Fact]
    public void Out_outside_workspace_exits_4_before_connecting()
    {
        var f = new Fixture(Config(), new FakeGit(0));

        var (exit, output) = f.Run("sql", "pull", "dbo.usp_X", "--out", "/elsewhere/pulled");

        Assert.Equal(4, exit);
        Assert.Contains("/elsewhere/pulled", output);
        Assert.Empty(f.Connections);
    }

    [Fact]
    public void Pull_directory_outside_workspace_exits_4()
    {
        var f = new Fixture("{ \"sql\": { \"pullDirectory\": \"../outside\" } }", new FakeGit(0));

        var (exit, output) = f.Run("sql", "pull", "dbo.usp_X", "--from", "dev");

        Assert.Equal(4, exit);
        Assert.Contains("작업 공간 밖", output);
    }

    [Fact]
    public void Existing_file_makes_exit_4()
    {
        var f = new Fixture(Config(), new FakeGit(0));
        f.Fs.AddFile("/w/db/pulled/dbo.usp_X.sql", new MockFileData("mine"));

        var (exit, _) = f.Run("sql", "pull", "dbo.usp_X");

        Assert.Equal(4, exit);
        Assert.Equal("mine", f.Fs.File.ReadAllText("/w/db/pulled/dbo.usp_X.sql"));
    }

    [Fact]
    public void Not_ignored_folder_shows_notice()
    {
        var git = new FakeGit(1);
        var f = new Fixture(Config(), git);

        var (exit, output) = f.Run("sql", "pull", "dbo.usp_X");

        Assert.Equal(0, exit);
        Assert.Contains("db/pulled/가 .gitignore에 없습니다 — 받은 파일이 커밋될 수 있습니다", output);
        var call = Assert.Single(git.Calls);
        Assert.Equal("git", call.FileName);
        Assert.Equal(["check-ignore", "-q"], call.Arguments.Take(2));
        Assert.EndsWith("dbo.usp_X.sql", call.Arguments[2]);
        Assert.Equal(f.Fs.Path.GetFullPath("/w"), f.Fs.Path.GetFullPath(call.WorkingDirectory));
    }

    [Fact]
    public void Ignored_folder_shows_no_notice()
    {
        var f = new Fixture(Config(), new FakeGit(0));

        var (_, output) = f.Run("sql", "pull", "dbo.usp_X");

        Assert.DoesNotContain(".gitignore", output);
    }

    [Fact]
    public void No_git_skips_ignore_check()
    {
        var f = new Fixture(Config(), new FakeGit(0, missing: true));

        var (exit, output) = f.Run("sql", "pull", "dbo.usp_X");

        Assert.Equal(0, exit);
        Assert.DoesNotContain(".gitignore", output);
    }

    [Fact]
    public void Not_a_git_repo_skips_ignore_check()
    {
        var f = new Fixture(Config(), new FakeGit(128));

        var (_, output) = f.Run("sql", "pull", "dbo.usp_X");

        Assert.DoesNotContain(".gitignore", output);
    }

    [Fact]
    public void Prod_pull_compares_with_dev()
    {
        var f = new Fixture(Config(), new FakeGit(0));

        var (exit, output) = f.Run("sql", "pull", "dbo.usp_X");

        Assert.Equal(0, exit);
        Assert.Contains(Dev, f.Connections);
        Assert.Contains("-- pelly pull: dev differs", f.Fs.File.ReadAllText("/w/db/pulled/dbo.usp_X.sql"));
        Assert.Contains("개발 DB", output);
    }

    [Fact]
    public void Dev_connection_refused_by_allow_list_notes_not_compared()
    {
        var f = new Fixture(Config().Replace("\"devdb01\"", "\"other\""), new FakeGit(0));

        var (exit, output) = f.Run("sql", "pull", "dbo.usp_X");

        Assert.Equal(0, exit);
        Assert.True(f.Fs.File.Exists("/w/db/pulled/dbo.usp_X.sql"));
        Assert.Contains("개발 DB와 비교하지 못함", output);
        Assert.DoesNotContain(Dev, f.Connections);
    }

    [Fact]
    public void Dev_not_configured_is_silent()
    {
        var f = new Fixture(Config(dev: false), new FakeGit(0));

        var (_, output) = f.Run("sql", "pull", "dbo.usp_X");

        Assert.DoesNotContain("개발 DB", output);
    }
}
