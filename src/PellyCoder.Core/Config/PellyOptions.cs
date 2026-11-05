using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Config;

/// <summary>How the THINK phase is used (design D21).</summary>
public enum ThinkMode { Off, Retry, Always }

public sealed class PellyOptions
{
    public LlmOptions Llm { get; set; } = new();
    public BudgetOptions Budget { get; set; } = new();
    public SamplingSet Sampling { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
    public LogOptions Log { get; set; } = new();
    public BuildOptions Build { get; set; } = new();
    public SqlOptions Sql { get; set; } = new();
    public TestOptions Test { get; set; } = new();
    public AspOptions Asp { get; set; } = new();
    public WebOptions Web { get; set; } = new();

    /// <summary>A shallow copy with <paramref name="test"/> as the test section; every other section is shared.</summary>
    public PellyOptions WithTest(TestOptions test)
    {
        var copy = (PellyOptions)MemberwiseClone();
        copy.Test = test;
        return copy;
    }
}

public sealed class LlmOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3.6:35b-a3b";
    public string KeepAlive { get; set; } = "30m";
    public int NumCtx { get; set; } = 8192;
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>Whole-request timeout for status and diagnostic calls (not for generate).</summary>
    public int ConnectTimeoutSeconds { get; set; } = 3;
    /// <summary>Max wait from sending a generate request to the first NDJSON line (covers cold model load, S1: ~26 s).</summary>
    public int FirstTokenTimeoutSeconds { get; set; } = 120;
    public int StreamIdleTimeoutSeconds { get; set; } = 10;
    public string? Template { get; set; }
    public ThinkMode ThinkMode { get; set; } = ThinkMode.Retry;
}

public sealed class BudgetOptions
{
    public int InputTotal { get; set; } = 4096;
    public int System { get; set; } = 450;
    public int Rules { get; set; } = 150;
    public int Request { get; set; } = 200;
    public int Context { get; set; } = 3000;
    public int Feedback { get; set; } = 296;
    public int Think { get; set; } = 512;
    public int CodeOutput { get; set; } = 2048;
}

public sealed class SamplingSet
{
    public SamplingOptions Think { get; set; } = new(0.6, 0.95, 20, 1.0, 0.0);
    public SamplingOptions Code { get; set; } = new(0.1, 0.9, 20, 1.05, 0.0);
}

public sealed class RetryOptions
{
    public int Max { get; set; } = 3;
}

public sealed class LogOptions
{
    public int RetentionDays { get; set; } = 30;
}

public sealed class BuildOptions
{
    public string? MsbuildPath { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public bool WarnAsError { get; set; }
    public bool StrictBaseline { get; set; }
}

public sealed class SqlOptions
{
    public List<string> AllowedHosts { get; set; } = new();
    public List<string> AllowedDatabases { get; set; } = new();
    public int CompatLevel { get; set; } = 160;
    public string? ConnectionString { get; set; }
    /// <summary>Read-only production connection used for SQL impact checks and pulls (V2-1d).</summary>
    public SqlProductionOptions Production { get; set; } = new();
    /// <summary>Workspace-relative directory that pulled stored procedures are written to.</summary>
    public string PullDirectory { get; set; } = "db/pulled";
}

public sealed class SqlProductionOptions
{
    public List<string> AllowedHosts { get; set; } = new();
    public List<string> AllowedDatabases { get; set; } = new();
    public string? ConnectionString { get; set; }
}

public sealed class TestOptions
{
    /// <summary>Legacy (.NET Framework) test command; placeholders {project}, {dll}, {trx}. Not used for SDK projects.</summary>
    public string? Command { get; set; }
    /// <summary>Explicit test projects, relative to the workspace root. Empty means discover them (design D18).</summary>
    public List<string> Projects { get; set; } = new();
    /// <summary>Time limit of one test run, separate from the build limit (R11).</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>A copy with <paramref name="projects"/> as <see cref="Projects"/>; every other setting is kept.</summary>
    public TestOptions WithProjects(List<string> projects)
    {
        var copy = (TestOptions)MemberwiseClone();
        copy.Projects = projects;
        return copy;
    }
}

/// <summary>Classic ASP edits (<c>pelly asp</c>).</summary>
public sealed class AspOptions
{
    /// <summary>True when the config has an <c>asp</c> section (for doctor). Computed by the loader, not a config key.</summary>
    public bool Configured { get; set; }
    /// <summary>Site root for <c>include virtual=</c>; null means the workspace root.</summary>
    public string? SiteRoot { get; set; }
    /// <summary>Encoding of pure ASCII files: <c>cp949</c> or <c>utf-8</c>.</summary>
    public string Encoding { get; set; } = "cp949";
    public string[] Extensions { get; set; } = [".asp", ".inc"];
    /// <summary>Site functions that make a value safe inside a SQL string.</summary>
    public string[] SqlSanitizers { get; set; } = [];
    public AspSpCallFunction[] SpCallFunctions { get; set; } = [];
    /// <summary>ADO constants are defined site-wide (e.g. a typelib the name check cannot see).</summary>
    public bool AdoConstants { get; set; }
}

/// <summary>Client JavaScript/CSS edits (<c>pelly js</c>, <c>pelly css</c>).</summary>
public sealed class WebOptions
{
    /// <summary>True when the config has a <c>web</c> section (for doctor). Computed by the loader, not a config key.</summary>
    public bool Configured { get; set; }
    /// <summary><c>auto</c>, <c>es5</c>, <c>es2015</c>~<c>es2026</c> or <c>latest</c>.</summary>
    public string JsTarget { get; set; } = "auto";
    /// <summary>jQuery version the site uses, e.g. <c>1.8.3</c>; null when unknown.</summary>
    public string? JqueryVersion { get; set; }
    /// <summary>Extra global names the site defines.</summary>
    public string[] Globals { get; set; } = [];
    /// <summary>Glob patterns (site-root relative, <c>/</c> separated, case-insensitive) of vendor files that are never edited.</summary>
    public string[] VendorPaths { get; set; } = ["**/lib/**", "**/plugin*/**", "**/vendor/**", "**/jquery*.js", "**/jquery*.css", "**/bootstrap*"];
}

/// <summary>A site wrapper function that calls a stored procedure, e.g. <c>ExecSP "usp_X", …</c>.</summary>
public sealed class AspSpCallFunction
{
    public string Name { get; set; } = "";
    /// <summary>1-based position of the stored procedure name argument.</summary>
    public int Argument { get; set; } = 1;
}
