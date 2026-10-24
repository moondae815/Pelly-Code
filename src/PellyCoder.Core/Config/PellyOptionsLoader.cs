using System.IO.Abstractions;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Config;

public sealed record OptionsLoadResult(PellyOptions? Options, IReadOnlyList<string> Errors);

public static class PellyPaths
{
    public static string UserConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pelly", "config.json");
}

/// <summary>
/// Merges built-in defaults, user config, workspace config, environment and CLI values (design 8.1)
/// and validates the result. Problems are reported as strings, never thrown.
/// </summary>
public static class PellyOptionsLoader
{
    private const string EnvPrefix = "PELLY__";

    // Keys of sections not consumed in M1 (design 8.1): accepted but not bound.
    private static readonly string[] FutureKeys = [];

    private static readonly string[] KnownKeys = BuildKnownKeys();

    public static OptionsLoadResult Load(
        IFileSystem fs,
        string? userConfigPath,
        string? workspaceConfigPath,
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyDictionary<string, string?> cliOverrides)
    {
        var errors = new List<string>();
        var builder = new ConfigurationBuilder();

        AddJsonFile(builder, fs, userConfigPath, errors);
        AddJsonFile(builder, fs, workspaceConfigPath, errors);
        builder.AddInMemoryCollection(ConvertEnvironment(environment));
        builder.AddInMemoryCollection(cliOverrides);

        IConfigurationRoot config;
        try
        {
            config = builder.Build();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException)
        {
            errors.Add($"config: {ex.Message}");
            return new OptionsLoadResult(null, errors);
        }

        foreach (var key in config.AsEnumerable().Select(kv => kv.Key).Where(k => !IsKnown(k)))
            errors.Add($"unknown config key: {key}");

        var options = new PellyOptions();
        Bind(config, options, errors);
        if (!string.IsNullOrEmpty(options.Sql.ConnectionString) && SqlConnectionText.HasPassword(options.Sql.ConnectionString))
            errors.Add("sql:connectionString에 비밀번호가 있습니다 — 비밀번호가 있는 연결은 환경 변수 PELLY_SQL_CONNECTION으로만 지정하세요");
        if (environment.TryGetValue("PELLY_SQL_CONNECTION", out var envConn) && !string.IsNullOrWhiteSpace(envConn))
            options.Sql.ConnectionString = envConn;
        Validate(options, errors);

        return errors.Count == 0 ? new OptionsLoadResult(options, errors) : new OptionsLoadResult(null, errors);
    }

    private static void AddJsonFile(ConfigurationBuilder builder, IFileSystem fs, string? path, List<string> errors)
    {
        if (string.IsNullOrEmpty(path) || !fs.File.Exists(path)) return;
        try
        {
            // Read fully so the stream is not held open and parse errors name the file.
            var bytes = fs.File.ReadAllBytes(path);
            var probe = new ConfigurationBuilder().AddJsonStream(new MemoryStream(bytes)).Build();
            _ = probe.AsEnumerable().ToList();
            builder.AddJsonStream(new MemoryStream(bytes));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"config file {path}: {ex.Message}");
        }
    }

    private static Dictionary<string, string?> ConvertEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in environment)
        {
            if (!name.StartsWith(EnvPrefix, StringComparison.Ordinal)) continue;
            var key = name[EnvPrefix.Length..].Replace("__", ":");
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }

    private static string[] BuildKnownKeys()
    {
        var keys = new List<string>(FutureKeys);
        foreach (var (section, type) in new (string, Type)[]
        {
            ("llm", typeof(LlmOptions)), ("budget", typeof(BudgetOptions)),
            ("retry", typeof(RetryOptions)), ("log", typeof(LogOptions)), ("build", typeof(BuildOptions)), ("sql", typeof(SqlOptions)), ("test", typeof(TestOptions)),
        })
        {
            foreach (var p in type.GetProperties())
                keys.Add($"{section}:{p.Name}");
        }
        foreach (var set in new[] { "think", "code" })
            foreach (var p in typeof(SamplingOptions).GetProperties().Where(p => p.CanRead && p.DeclaringType == typeof(SamplingOptions) && p.Name != "EqualityContract"))
                keys.Add($"sampling:{set}:{p.Name}");
        return keys.ToArray();
    }

    private static bool IsKnown(string key)
    {
        foreach (var known in KnownKeys)
        {
            if (string.Equals(key, known, StringComparison.OrdinalIgnoreCase)) return true;
            // Section/parent keys and children of dictionary or array values.
            if (known.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith(known + ":", StringComparison.OrdinalIgnoreCase) && IsOpenEnded(known)) return true;
        }
        return false;
    }

    private static bool IsOpenEnded(string known) =>
        known is "llm:Headers" or "test:Projects" or "sql:AllowedHosts" or "sql:AllowedDatabases";

    private static void Bind(IConfigurationRoot config, PellyOptions options, List<string> errors)
    {
        try
        {
            config.GetSection("llm").Bind(options.Llm);
            config.GetSection("budget").Bind(options.Budget);
            config.GetSection("retry").Bind(options.Retry);
            config.GetSection("log").Bind(options.Log);
            config.GetSection("build").Bind(options.Build);
            options.Sql.AllowedHosts.Clear();
            options.Sql.AllowedDatabases.Clear();
            config.GetSection("sql").Bind(options.Sql);
            options.Test.Projects.Clear();
            config.GetSection("test").Bind(options.Test);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"config value: {ex.InnerException?.Message ?? ex.Message}");
        }

        var thinkMode = config["llm:thinkMode"];
        if (thinkMode is not null)
        {
            if (Enum.TryParse<ThinkMode>(thinkMode, ignoreCase: true, out var mode)
                && !int.TryParse(thinkMode, out _))
                options.Llm.ThinkMode = mode;
            else
                errors.Add($"llm:thinkMode: '{thinkMode}' is not one of off, retry, always");
        }

        options.Sampling.Think = BindSampling(config.GetSection("sampling:think"), options.Sampling.Think, "sampling:think", errors);
        options.Sampling.Code = BindSampling(config.GetSection("sampling:code"), options.Sampling.Code, "sampling:code", errors);
    }

    private static SamplingOptions BindSampling(IConfigurationSection s, SamplingOptions d, string path, List<string> errors) =>
        new(Num(s, "temperature", d.Temperature, path, errors),
            Num(s, "topP", d.TopP, path, errors),
            (int)Num(s, "topK", d.TopK, path, errors),
            Num(s, "repeatPenalty", d.RepeatPenalty, path, errors),
            Num(s, "presencePenalty", d.PresencePenalty, path, errors));

    private static double Num(IConfigurationSection s, string key, double fallback, string path, List<string> errors)
    {
        var raw = s[key];
        if (raw is null) return fallback;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        errors.Add($"{path}:{key}: '{raw}' is not a number");
        return fallback;
    }

    private static void Validate(PellyOptions o, List<string> errors)
    {
        AtLeast(errors, "llm:numCtx", o.Llm.NumCtx, 1);
        AtLeast(errors, "llm:connectTimeoutSeconds", o.Llm.ConnectTimeoutSeconds, 1);
        AtLeast(errors, "llm:firstTokenTimeoutSeconds", o.Llm.FirstTokenTimeoutSeconds, 1);
        AtLeast(errors, "llm:streamIdleTimeoutSeconds", o.Llm.StreamIdleTimeoutSeconds, 1);
        foreach (var p in typeof(BudgetOptions).GetProperties().Where(p => p.PropertyType == typeof(int)))
            AtLeast(errors, $"budget:{char.ToLowerInvariant(p.Name[0])}{p.Name[1..]}", (int)p.GetValue(o.Budget)!, 1);
        AtLeast(errors, "build:timeoutSeconds", o.Build.TimeoutSeconds, 1);
        AtLeast(errors, "test:timeoutSeconds", o.Test.TimeoutSeconds, 1);
        AtLeast(errors, "log:retentionDays", o.Log.RetentionDays, 1);
        AtLeast(errors, "retry:max", o.Retry.Max, 0);
        AtLeast(errors, "sampling:think:topK", o.Sampling.Think.TopK, 1);
        AtLeast(errors, "sampling:code:topK", o.Sampling.Code.TopK, 1);

        if (!SqlParsers.SupportedLevels.Contains(o.Sql.CompatLevel))
            errors.Add($"sql:compatLevel 값은 90, 100, …, 170 중 하나여야 합니다: {o.Sql.CompatLevel}");

        var b = o.Budget;
        var parts = (long)b.System + b.Rules + b.Request + b.Context + b.Feedback;
        if (parts > b.InputTotal)
            errors.Add($"budget: system+rules+request+context+feedback ({parts}) exceeds budget:inputTotal ({b.InputTotal})");
        var total = (long)b.InputTotal + b.Think + b.CodeOutput;
        if (total > o.Llm.NumCtx)
            errors.Add($"budget: inputTotal+think+codeOutput ({total}) exceeds llm:numCtx ({o.Llm.NumCtx})");
    }

    private static void AtLeast(List<string> errors, string key, int value, int min)
    {
        if (value < min)
            errors.Add($"{key} 값은 {min} 이상이어야 합니다: {value}");
    }
}
