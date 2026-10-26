using System.IO.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;

namespace PellyCoder.Cli.Commands;

/// <summary>A target file that exists plus the resolved configuration.</summary>
public sealed record PreparedRun(string File, PellyOptions Options);

/// <summary>Argument validation and request building shared by <c>pelly cs|sql</c> and <c>pelly ui cs|sql</c>.</summary>
public static class RunArguments
{
    /// <summary>Option-combination errors in the order they are checked; empty when valid.</summary>
    public static IReadOnlyList<string> ValidateCs(CsSettings s)
    {
        var errors = new List<string>();
        if (s.Line is not null && (s.Class is not null || s.Method is not null))
            errors.Add("--line은 --class/--method와 함께 쓸 수 없습니다");
        if (s.Line is <= 0)
            errors.Add("--line은 1 이상이어야 합니다");
        if (s.MaxRetries is < 0)
            errors.Add("--max-retries는 0 이상이어야 합니다");

        var verifyTests = Distinct(s.VerifyTest);
        var expectBreaks = Distinct(s.ExpectBreak);
        foreach (var name in verifyTests.Concat(expectBreaks))
            if (!TestNames.IsValid(name))
                errors.Add($"테스트 이름 형식이 아닙니다: {name}");
        if (expectBreaks.Count > 0 && verifyTests.Count == 0)
            errors.Add("--expect-break는 --verify-test와 함께 써야 합니다");
        if (verifyTests.Intersect(expectBreaks, StringComparer.Ordinal).FirstOrDefault() is { } both)
            errors.Add($"같은 테스트를 --verify-test와 --expect-break에 함께 줄 수 없습니다: {both}");
        return errors;
    }

    public static IReadOnlyList<string> ValidateSql(SqlSettings s)
    {
        var errors = new List<string>();
        if (s.Line is not null && s.Object is not null)
            errors.Add("--line은 --object와 함께 쓸 수 없습니다");
        if (s.Line is <= 0)
            errors.Add("--line은 1 이상이어야 합니다");
        if (s.MaxRetries is < 0)
            errors.Add("--max-retries는 0 이상이어야 합니다");
        return errors;
    }

    public static (PreparedRun? Run, IReadOnlyList<string> Errors) PrepareCs(CsSettings s, IFileSystem fs, OptionsResolver resolver) =>
        Prepare(s.File, fs, file => resolver.Resolve(
            file, s.Config, s.Think, s.Model, s.Endpoint, s.StrictBaseline, s.WarnAsError));

    public static (PreparedRun? Run, IReadOnlyList<string> Errors) PrepareSql(SqlSettings s, IFileSystem fs, OptionsResolver resolver) =>
        Prepare(s.File, fs, file => resolver.Resolve(file, s.Config, s.Think, s.Model, s.Endpoint));

    public static RunRequest CsRequest(CsSettings s, string file, string requestText)
    {
        var verifyTests = Distinct(s.VerifyTest);
        var expectBreaks = Distinct(s.ExpectBreak);
        return new RunRequest(
            file, s.Class, s.Method, s.Line, requestText, s.Yes, s.DryRun, s.MaxRetries,
            Verbose: s.Verbose, RunTests: s.Test,
            VerifyTests: verifyTests.Count > 0 ? verifyTests : null, ExpectBreaks: expectBreaks.Count > 0 ? expectBreaks : null);
    }

    public static RunRequest SqlRequest(SqlSettings s, string file, string requestText) =>
        new(file, null, null, s.Line, requestText, s.Yes, s.DryRun, s.MaxRetries,
            ObjectName: s.Object, Kind: EditKind.Sql, Verbose: s.Verbose);

    private static (PreparedRun? Run, IReadOnlyList<string> Errors) Prepare(
        string path, IFileSystem fs, Func<string, OptionsLoadResult> resolve)
    {
        var file = fs.Path.GetFullPath(path);
        if (!fs.File.Exists(file))
            return (null, [$"파일을 찾을 수 없습니다: {file}"]);
        var loaded = resolve(file);
        return loaded.Options is null ? (null, loaded.Errors) : (new PreparedRun(file, loaded.Options), []);
    }

    // Distinct, in the order given; an empty list means the option was absent.
    private static List<string> Distinct(string[]? values) => values?.Distinct(StringComparer.Ordinal).ToList() ?? [];
}
