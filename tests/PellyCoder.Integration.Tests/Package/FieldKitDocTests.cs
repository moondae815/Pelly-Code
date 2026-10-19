using System.Text.Json;
using System.Text.RegularExpressions;
using PellyCoder.Eval;

namespace PellyCoder.Integration.Tests.Package;

/// <summary>
/// The field kit's documents and templates (Task 8): the templates pass the same schema checks the runner applies,
/// every <c>pelly-eval</c> command line in the guide parses with the real argument parser, and nothing carries a secret,
/// a real host or a developer path.
/// </summary>
public sealed class FieldKitDocTests
{
    private static readonly string[] TemplateTasks = ["cs-modern-001", "cs-legacy-001", "sql-001"];

    private static readonly string[] DocFiles =
    [
        "deploy/field-guide.md",
        "deploy/field-benchmark.md",
        "deploy/field-results.md",
        "deploy/field-kit/templates/suite.json",
        "deploy/field-kit/templates/tasks/cs-modern-001.yaml",
        "deploy/field-kit/templates/tasks/cs-legacy-001.yaml",
        "deploy/field-kit/templates/tasks/sql-001.yaml",
        JudgeTemplate,
    ];

    private const string JudgeTemplate = "deploy/field-kit/templates/judges/Modern/Modern.JudgeTests/Modern.JudgeTests.csproj";

    // Edited existing docs: the Password rule cannot apply (install.md explains that key), so only developer paths are checked.
    private static readonly string[] EditedDocs = ["deploy/install.md", "deploy/server-checklist.md", "deploy/ide-external-tools.md"];

    private static readonly string[] SuiteKeys = ["formatVersion", "name", "roots", "supportFiles", "resultsDir"];
    private static readonly string[] RootKeys = ["name", "path", "exclude"];

    [Fact]
    public void Templates_PassSchemaChecks()
    {
        foreach (var id in TemplateTasks)
        {
            var path = PackageFileList.RepoFile($"deploy/field-kit/templates/tasks/{id}.yaml");
            var task = BenchmarkTask.Load(path);
            var issues = SuiteValidator.SchemaIssues(task, Path.GetFileNameWithoutExtension(path));
            Assert.True(issues.Count == 0, $"{id}: {string.Join("; ", issues.Select(i => i.Message))}");
        }
    }

    [Fact]
    public void JudgeTemplate_PassesTheJudgeContract_AndNamesTheProperty()
    {
        var path = PackageFileList.RepoFile(JudgeTemplate);
        // The template is meant to be filled in: its only issue is the unfilled placeholder warning.
        var issues = SuiteValidator.JudgeProjectIssues(new System.IO.Abstractions.FileSystem(), path);
        Assert.All(issues, i => Assert.Contains("템플릿 자리 표시가 남아 있습니다", i.Message, StringComparison.Ordinal));
        Assert.Single(issues);
        var text = File.ReadAllText(path);
        // Same runner as the built-in judges (xunit.v3.mtp-v2): the TRX path pelly reads is the Testing Platform one.
        Assert.Contains("xunit.v3.mtp-v2", text, StringComparison.Ordinal);
        Assert.Contains("<ProjectReference Include=\"$(JudgeTargetRoot)/", text, StringComparison.Ordinal);
        // The kit folder layout is judges/<root>/<project>/, as the runner expects (BenchmarkSuite.JudgesDir).
        Assert.Equal("Modern", Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))));
    }

    [Fact]
    public void TemplateSuiteJson_HasOnlyKnownKeys()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(PackageFileList.RepoFile("deploy/field-kit/templates/suite.json")));
        var top = doc.RootElement;
        foreach (var p in top.EnumerateObject()) Assert.Contains(p.Name, SuiteKeys);
        Assert.Equal(1, top.GetProperty("formatVersion").GetInt32());
        Assert.NotEmpty(top.GetProperty("roots").EnumerateArray());
        foreach (var root in top.GetProperty("roots").EnumerateArray())
        {
            foreach (var p in root.EnumerateObject()) Assert.Contains(p.Name, RootKeys);
            Assert.StartsWith(@"D:\", root.GetProperty("path").GetString());
        }
    }

    [Fact]
    public void FieldGuide_EvalCommandLines_Parse()
    {
        var text = File.ReadAllText(PackageFileList.RepoFile("deploy/field-guide.md"));
        var lines = new List<string>();
        foreach (Match block in Regex.Matches(text, @"```[a-z]*\r?\n(.*?)```", RegexOptions.Singleline))
            foreach (var raw in block.Groups[1].Value.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("pelly-eval", StringComparison.Ordinal)) lines.Add(line);
            }

        Assert.True(lines.Count >= 8, $"only {lines.Count} pelly-eval lines");
        foreach (var line in lines)
        {
            var args = Tokenize(line["pelly-eval".Length..]);
            var parsed = EvalArguments.Parse(args);
            Assert.True(parsed.Error is null, $"'{line}': {parsed.Error}");
        }
        foreach (var command in new[] { "validate", "record", "export", "compare" })
            Assert.Contains(lines, l => l.StartsWith($"pelly-eval {command}", StringComparison.Ordinal));
    }

    [Fact]
    public void FieldDocs_HaveNoSecretsOrDevPaths()
    {
        foreach (var file in DocFiles)
        {
            var text = File.ReadAllText(PackageFileList.RepoFile(file));
            Assert.True(TemplateRules.Violations(text).Count == 0, $"{file}: {string.Join(", ", TemplateRules.Violations(text))}");
        }
    }

    [Fact]
    public void EditedDocs_HaveNoDeveloperPaths()
    {
        foreach (var file in EditedDocs)
        {
            var text = File.ReadAllText(PackageFileList.RepoFile(file));
            foreach (var bad in new[] { "/Users/", @"C:\Users\", @"C:\\Users\\" })
                Assert.False(text.Contains(bad, StringComparison.OrdinalIgnoreCase), $"{file}: {bad}");
        }
    }

    // Splits a command line the way cmd/PowerShell would for these examples: whitespace separates, double quotes group.
    private static List<string> Tokenize(string commandLine)
    {
        var result = new List<string>();
        foreach (Match m in Regex.Matches(commandLine, "\"([^\"]*)\"|(\\S+)"))
            result.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return result;
    }
}
