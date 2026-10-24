using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using static PellyCoder.Core.Tests.TestPaths;

namespace PellyCoder.Core.Tests.Config;

public class PellyOptionsLoaderTests
{
    private static readonly IReadOnlyDictionary<string, string?> None = new Dictionary<string, string?>();

    private static OptionsLoadResult Load(
        MockFileSystem fs,
        string? user = null, string? ws = null,
        IReadOnlyDictionary<string, string?>? env = null,
        IReadOnlyDictionary<string, string?>? cli = null) =>
        PellyOptionsLoader.Load(fs, user, ws, env ?? None, cli ?? None);

    [Fact]
    public void Defaults_MatchDesign()
    {
        var r = Load(new MockFileSystem());
        Assert.Empty(r.Errors);
        var o = r.Options!;
        Assert.Equal(4096, o.Budget.InputTotal);
        Assert.Equal(450, o.Budget.System);
        Assert.Equal(150, o.Budget.Rules);
        Assert.Equal(200, o.Budget.Request);
        Assert.Equal(3000, o.Budget.Context);
        Assert.Equal(296, o.Budget.Feedback);
        Assert.Equal(512, o.Budget.Think);
        Assert.Equal(2048, o.Budget.CodeOutput);
        Assert.Equal(8192, o.Llm.NumCtx);
        Assert.Equal("30m", o.Llm.KeepAlive);
        Assert.Equal(120, o.Llm.FirstTokenTimeoutSeconds);
        Assert.Equal(ThinkMode.Retry, o.Llm.ThinkMode);
        Assert.Equal(new SamplingOptions(0.6, 0.95, 20, 1.0, 0), o.Sampling.Think);
        Assert.Equal(new SamplingOptions(0.1, 0.9, 20, 1.05, 0), o.Sampling.Code);
        Assert.Equal(3, o.Retry.Max);
        Assert.Null(o.Build.MsbuildPath);
        Assert.Equal(120, o.Build.TimeoutSeconds);
        Assert.False(o.Build.WarnAsError);
        Assert.False(o.Build.StrictBaseline);
        Assert.Empty(o.Sql.AllowedHosts);
        Assert.Empty(o.Sql.AllowedDatabases);
        Assert.Equal(160, o.Sql.CompatLevel);
        Assert.Null(o.Sql.ConnectionString);
    }

    private const string PwdError = "sql:connectionString에 비밀번호가 있습니다 — 비밀번호가 있는 연결은 환경 변수 PELLY_SQL_CONNECTION으로만 지정하세요";

    [Fact]
    public void ConfigConnectionWithPassword_IsConfigError()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [P(@"C:\w\pelly.json")] = new("""{"sql":{"connectionString":"Server=x;Database=d;User Id=sa;Pwd=p"}}"""),
        });
        var r = Load(fs, ws: P(@"C:\w\pelly.json"));
        Assert.Null(r.Options);
        Assert.Contains(PwdError, r.Errors);
    }

    [Fact]
    public void ConfigConnectionUnparsableWithPassword_IsConfigError()
    {
        var cli = new Dictionary<string, string?> { ["sql:connectionString"] = "Server=x;Password=a;b" };
        var r = Load(new MockFileSystem(), cli: cli);
        Assert.Null(r.Options);
        Assert.Contains(PwdError, r.Errors);
    }

    [Fact]
    public void EnvConnectionWithPassword_IsAccepted_AndWins()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [P(@"C:\w\pelly.json")] = new("""{"sql":{"connectionString":"Server=file;Database=d"}}"""),
        });
        var env = new Dictionary<string, string?> { ["PELLY_SQL_CONNECTION"] = "Server=env;User Id=sa;Password=p" };
        var r = Load(fs, ws: P(@"C:\w\pelly.json"), env: env);
        Assert.Empty(r.Errors);
        Assert.Equal("Server=env;User Id=sa;Password=p", r.Options!.Sql.ConnectionString);
    }

    [Fact]
    public void GenericEnvPrefixConnection_IsTreatedAsConfig()
    {
        var env = new Dictionary<string, string?> { ["PELLY__SQL__CONNECTIONSTRING"] = "Server=x;Password=p" };
        var r = Load(new MockFileSystem(), env: env);
        Assert.Null(r.Options);
        Assert.Contains(PwdError, r.Errors);
    }

    [Fact]
    public void SqlLists_BindAsArrays()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [P(@"C:\w\pelly.json")] = new("""{"sql":{"allowedHosts":["a","b"],"allowedDatabases":["d"],"compatLevel":150}}"""),
        });
        var o = Load(fs, ws: P(@"C:\w\pelly.json")).Options!;
        Assert.Equal(new[] { "a", "b" }, o.Sql.AllowedHosts);
        Assert.Equal(new[] { "d" }, o.Sql.AllowedDatabases);
        Assert.Equal(150, o.Sql.CompatLevel);
    }

    [Fact]
    public void MergeOrder_UserThenWorkspaceThenEnvThenCli()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [P(@"C:\u\config.json")] = new("""{"llm":{"model":"user"}}"""),
            [P(@"C:\w\pelly.json")] = new("""{"llm":{"model":"ws"}}"""),
        });
        var u = P(@"C:\u\config.json");
        var w = P(@"C:\w\pelly.json");
        var env = new Dictionary<string, string?> { ["PELLY__LLM__MODEL"] = "env" };
        var cli = new Dictionary<string, string?> { ["llm:model"] = "cli" };

        Assert.Equal("cli", Load(fs, u, w, env, cli).Options!.Llm.Model);
        Assert.Equal("env", Load(fs, u, w, env).Options!.Llm.Model);
        Assert.Equal("ws", Load(fs, u, w).Options!.Llm.Model);
        Assert.Equal("user", Load(fs, u).Options!.Llm.Model);
    }

    [Fact]
    public void EnvironmentPrefix_OnlyPellyDoubleUnderscore()
    {
        var env = new Dictionary<string, string?>
        {
            ["PELLY__LLM__MODEL"] = "m1",
            ["LLM__MODEL"] = "bad",
            ["PELLYX__LLM__MODEL"] = "bad",
            ["PATH"] = "/usr/bin",
        };
        var r = Load(new MockFileSystem(), env: env);
        Assert.Empty(r.Errors);
        Assert.Equal("m1", r.Options!.Llm.Model);
    }

    [Fact]
    public void UnknownKey_IsError()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [P(@"C:\w\pelly.json")] = new("""{"llm":{"modle":"x"}}"""),
        });
        var r = Load(fs, ws: P(@"C:\w\pelly.json"));
        Assert.Null(r.Options);
        Assert.Contains(r.Errors, e => e.Contains("llm:modle"));
    }

    [Fact]
    public void InputParts_ExceedInputTotal_IsError()
    {
        var cli = new Dictionary<string, string?> { ["budget:context"] = "3500" };
        var r = Load(new MockFileSystem(), cli: cli);
        Assert.Null(r.Options);
        Assert.Contains(r.Errors, e => e.Contains("budget"));
    }

    [Fact]
    public void InputParts_RulesPushPastInputTotal_ErrorMentionsRules()
    {
        var cli = new Dictionary<string, string?> { ["budget:rules"] = "400" };
        var r = Load(new MockFileSystem(), cli: cli);
        Assert.Null(r.Options);
        Assert.Contains(r.Errors, e => e.Contains("budget") && e.Contains("rules"));
    }

    [Fact]
    public void InputPlusOutput_ExceedNumCtx_IsError()
    {
        var cli = new Dictionary<string, string?> { ["llm:numCtx"] = "6000" };
        var r = Load(new MockFileSystem(), cli: cli);
        Assert.Null(r.Options);
        Assert.Contains(r.Errors, e => e.Contains("budget") && e.Contains("numCtx"));
    }

    [Fact]
    public void ThinkMode_ParsesCaseInsensitive()
    {
        var ok = Load(new MockFileSystem(), cli: new Dictionary<string, string?> { ["llm:thinkMode"] = "always" });
        Assert.Equal(ThinkMode.Always, ok.Options!.Llm.ThinkMode);

        var bad = Load(new MockFileSystem(), cli: new Dictionary<string, string?> { ["llm:thinkMode"] = "bogus" });
        Assert.Null(bad.Options);
        Assert.Contains(bad.Errors, e => e.Contains("llm:thinkMode"));
    }

    [Fact]
    public void MissingFiles_AreIgnored()
    {
        var r = Load(new MockFileSystem(), P(@"C:\nope\config.json"), P(@"C:\nope\pelly.json"));
        Assert.Empty(r.Errors);
        Assert.NotNull(r.Options);
    }

    [Fact]
    public void FutureSections_AreKnown()
    {
        var json = """
            {"build":{"msbuildPath":null,"timeoutSeconds":120,"warnAsError":false},
             "test":{"command":null,"projects":["a.csproj"]},
             "sql":{"allowedHosts":["h"],"allowedDatabases":[],"compatLevel":160,"connectionString":null},
             "log":{"retentionDays":7},
             "llm":{"headers":{"X-Token":"t"}},
             "sampling":{"code":{"temperature":0.2}}}
            """;
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [P(@"C:\w\pelly.json")] = new(json),
        });
        var r = Load(fs, ws: P(@"C:\w\pelly.json"));
        Assert.Empty(r.Errors);
        Assert.Equal(7, r.Options!.Log.RetentionDays);
        Assert.Equal("t", r.Options.Llm.Headers["X-Token"]);
        Assert.Equal(new SamplingOptions(0.2, 0.9, 20, 1.05, 0), r.Options.Sampling.Code);
    }
}
