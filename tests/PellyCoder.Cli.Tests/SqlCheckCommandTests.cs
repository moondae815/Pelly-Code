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

public class SqlCheckCommandTests
{
    private const string Prod = "Server=proddb01;Database=Shop;Integrated Security=true";
    private const string ProdDef = "CREATE PROCEDURE [dbo].[usp_X]\nAS\nSELECT 1\n";
    private const string OldDef = "CREATE PROCEDURE [dbo].[usp_X]\nAS\nSELECT 0\n";
    private const string Same = "ALTER PROCEDURE [dbo].[usp_X]\nAS\nSELECT 1\n";
    private const string Edited = "ALTER PROCEDURE [dbo].[usp_X]\nAS\nSELECT 2\n";
    private const string Config =
        "{ \"sql\": { \"production\": { \"connectionString\": \"" + Prod + "\", \"allowedHosts\": [\"proddb01\"], \"allowedDatabases\": [\"Shop\"] } } }";

    private sealed class FakeReader(string? definition, bool exists) : ISqlDefinitionReader
    {
        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct) =>
            // Production holds each object under its own name, so a file's two objects are compared with different definitions.
            Task.FromResult<SqlModuleDefinition?>(exists
                ? new SqlModuleDefinition(new SqlObjectName("dbo", name.Name), "P", definition?.Replace("usp_X", name.Name, StringComparison.Ordinal), true, true)
                : null);

        public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CountingRunner : IProcessRunner
    {
        public int Calls { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("sql check must not start processes");
        }
    }

    private static string Record(string definition) =>
        $"-- pelly pull: source=prod server=proddb01 database=Shop object=[dbo].[usp_X] sha256={SqlDefinitionText.Hash(definition, 160)} at=2026-10-26T09:12:33Z\n";

    private static (int Exit, string Output, MockFileSystem Fs, CountingRunner Runner) Run(string? config, string? fileText, string? prodDef, bool inProd, params string[] extra)
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/w/.git");
        if (config is not null) fs.AddFile("/w/pelly.json", new MockFileData(config));
        if (fileText is not null) fs.AddFile("/w/x.sql", new MockFileData(fileText));
        fs.Directory.SetCurrentDirectory("/w");
        var runner = new CountingRunner();
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<IAnsiConsole>(new TestConsole().Width(300));
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProcessRunner>(runner);
        Func<string, ISqlDefinitionReader> factory = _ => new FakeReader(prodDef, inProd);
        services.AddSingleton(factory);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        var result = app.Run(["sql", "check", "x.sql", .. extra]);
        return (result.ExitCode, result.Output, fs, runner);
    }

    [Fact]
    public void Same_as_prod_exits_0()
    {
        var (exit, output, fs, runner) = Run(Config, Same, ProdDef, true);
        Assert.Equal(0, exit);
        Assert.Contains("운영과 같음 (이미 반영됨 또는 아직 고치지 않음)", output);
        Assert.Equal(Same, fs.File.ReadAllText("/w/x.sql"));
        Assert.Equal(0, runner.Calls);
        Assert.DoesNotContain("받은 기록이 운영보다 오래되었습니다", output);
    }

    [Fact]
    public void Same_as_prod_with_stale_record_notes_it_and_exits_0()
    {
        var text = Record(OldDef) + "SET ANSI_NULLS ON\nGO\n" + Same;   // a pulled file's shape, then deployed
        var (exit, output, fs, _) = Run(Config, text, ProdDef, true);
        Assert.Equal(0, exit);
        Assert.Contains("운영과 같음 (이미 반영됨 또는 아직 고치지 않음)", output);
        Assert.Contains("받은 기록이 운영보다 오래되었습니다 — 다음 pelly sql 실행 때 갱신합니다", output);
        Assert.DoesNotContain("다시 받으십시오", output);
        Assert.Equal(text, fs.File.ReadAllText("/w/x.sql"));   // check stays read-only: the refresh is pelly sql's
    }

    [Fact]
    public void Unchanged_exits_0()
    {
        var (exit, output, _, _) = Run(Config, Record(ProdDef) + Edited, ProdDef, true);
        Assert.Equal(0, exit);
        Assert.Contains("받은 뒤 운영이 바뀌지 않음", output);
    }

    [Fact]
    public void Changed_exits_1()
    {
        var (exit, output, _, _) = Run(Config, Record(OldDef) + Edited, ProdDef, true);
        Assert.Equal(1, exit);
        Assert.Contains("받은 뒤 운영에서", output);
        Assert.Contains("pelly sql pull", output);
    }

    [Fact]
    public void No_record_and_differs_exits_4()
    {
        var (exit, output, _, _) = Run(Config, Edited, ProdDef, true);
        Assert.Equal(4, exit);
        Assert.Contains("받은 기록 없음 — 운영 변경 여부를 확인할 수 없습니다. 파일이 운영과 다릅니다", output);
    }

    [Fact]
    public void Not_in_prod_exits_0()
    {
        var (exit, output, _, _) = Run(Config, Edited, null, false);
        Assert.Equal(0, exit);
        Assert.Contains("운영에 없음 (새 객체)", output);
    }

    [Fact]
    public void Not_configured_exits_4()
    {
        var (exit, output, _, _) = Run("{ }", Edited, ProdDef, true);
        Assert.Equal(4, exit);
        Assert.Contains("sql.production", output);
    }

    [Fact]
    public void Two_objects_without_object_option_exits_3()
    {
        var (exit, output, _, _) = Run(Config, Edited + "GO\n" + Same.Replace("usp_X", "usp_Y"), ProdDef, true);
        Assert.Equal(3, exit);
        Assert.Contains("--object", output);
    }

    [Theory]
    [InlineData("dbo.usp_X", 4)]   // edited: differs from production, no record
    [InlineData("dbo.usp_Y", 0)]   // unchanged: same as production
    public void Object_option_picks_one_of_two(string obj, int expected)
    {
        var (exit, output, _, _) = Run(Config, Edited + "GO\n" + Same.Replace("usp_X", "usp_Y"), ProdDef, true, "--object", obj);
        Assert.Equal(expected, exit);
        Assert.Equal(expected == 0, output.Contains("운영과 같음", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_file_exits_4()
    {
        var (exit, output, _, _) = Run(Config, null, ProdDef, true);
        Assert.Equal(4, exit);
        Assert.Contains("x.sql", output);
    }

    [Fact]
    public void Unknown_object_exits_4()
    {
        var (exit, _, _, _) = Run(Config, Edited, ProdDef, true, "--object", "dbo.usp_Nope");
        Assert.Equal(4, exit);
    }
}
