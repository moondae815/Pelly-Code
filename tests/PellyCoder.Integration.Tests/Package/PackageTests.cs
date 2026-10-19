using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Package;

/// <summary>One built zip: the variable that names it, its allow-list and the files the brief fixes for it.</summary>
public sealed record PackageKind(string Name, string ZipVariable, string ListFile, string Exe, string[] ExpectedFiles)
{
    // The brief's allow-list (Task 9). deploy/package-files.txt must say the same; the zip must contain exactly this.
    public static readonly PackageKind Pelly = new("pelly", "PELLY_TEST_PACKAGE", "deploy/package-files.txt", "pelly.exe",
    [
        "pelly.exe",
        "LICENSE",
        "pelly.json.template",
        "docs/install.md",
        "docs/usage.md",
        "docs/ide-external-tools.md",
        "docs/server-checklist.md",
        "docs/no-network.md",
        "sbom.cdx.json",
        "THIRD-PARTY-NOTICES.md",
        "licenses/Microsoft.Data.SqlClient.SNI.runtime.LICENSE.txt",
        "version-record.json",
        "benchmark-summary.md",
        "SHA256SUMS.txt",
    ]);

    // M4 Task 9: the field kit (pelly-eval.exe, its guides and suite templates). deploy/field-kit-files.txt must say the same.
    public static readonly PackageKind FieldKit = new("field-kit", "PELLY_TEST_FIELD_KIT", "deploy/field-kit-files.txt", "pelly-eval.exe",
    [
        "pelly-eval.exe",
        "LICENSE",
        "docs/field-guide.md",
        "docs/field-benchmark.md",
        "docs/field-results.md",
        "templates/suite.json",
        "templates/tasks/cs-modern-001.yaml",
        "templates/tasks/cs-legacy-001.yaml",
        "templates/tasks/sql-001.yaml",
        "templates/judges/Modern/Modern.JudgeTests/Modern.JudgeTests.csproj",
        "sbom.cdx.json",
        "THIRD-PARTY-NOTICES.md",
        "licenses/Microsoft.Data.SqlClient.SNI.runtime.LICENSE.txt",
        "SHA256SUMS.txt",
    ]);

    public static PackageKind ByName(string name) => name switch
    {
        "pelly" => Pelly,
        "field-kit" => FieldKit,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown package"),
    };
}

/// <summary>
/// Checks the zips built by <c>deploy/build-package.ps1</c> (Task 9): the air-gap zip (<c>PELLY_TEST_PACKAGE</c>) and the
/// field kit (<c>PELLY_TEST_FIELD_KIT</c>, <c>-Package field-kit</c>). The CI <c>package</c> job sets both and runs only this class.
/// A missing variable on Windows is a failure, not a skip: the job must not pass without looking at the zips.
/// </summary>
public sealed class PackageTests
{
    public const string ZipVariable = "PELLY_TEST_PACKAGE";
    public const string FieldKitVariable = "PELLY_TEST_FIELD_KIT";

    public static TheoryData<string> Packages => new("pelly", "field-kit");

    private static readonly Dictionary<string, Lazy<string>> Extracted = new(StringComparer.Ordinal)
    {
        ["pelly"] = new(() => Extract(PackageKind.Pelly)),
        ["field-kit"] = new(() => Extract(PackageKind.FieldKit)),
    };

    private static readonly Regex SumLine = new(@"\A([0-9a-f]{64})  ([^ ].*)\z");

    [WindowsTheory]
    [MemberData(nameof(Packages))]
    public void Package_ContainsExactlyAllowedFiles(string package)
    {
        var kind = PackageKind.ByName(package);
        var root = Extracted[package].Value;
        var actual = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(kind.ExpectedFiles.Order(StringComparer.Ordinal), actual);
        Assert.Equal(kind.ExpectedFiles.Order(StringComparer.Ordinal), PackageFileList.ZipPaths(kind.ListFile).Order(StringComparer.Ordinal));
    }

    [WindowsTheory]
    [MemberData(nameof(Packages))]
    public void Package_ChecksumsMatch(string package)
    {
        var kind = PackageKind.ByName(package);
        var root = Extracted[package].Value;
        var lines = File.ReadAllLines(Path.Combine(root, "SHA256SUMS.txt"));
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var m = SumLine.Match(line);
            Assert.True(m.Success, $"bad SHA256SUMS line: '{line}'");
            Assert.True(listed.TryAdd(m.Groups[2].Value, m.Groups[1].Value), $"duplicate entry: {m.Groups[2].Value}");
        }

        var files = kind.ExpectedFiles.Where(f => f != "SHA256SUMS.txt").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(files, listed.Keys.Order(StringComparer.Ordinal));
        foreach (var file in files)
        {
            using var stream = File.OpenRead(Path.Combine(root, file));
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            Assert.True(hash == listed[file], $"{file}: SHA256SUMS says {listed[file]}, file is {hash}");
        }
    }

    [WindowsFact]
    public void Template_HasNoSecretsOrRealHosts()
    {
        var text = File.ReadAllText(Path.Combine(Extracted["pelly"].Value, "pelly.json.template"));
        Assert.Empty(TemplateRules.Violations(text));
    }

    [WindowsTheory]
    [MemberData(nameof(Packages))]
    public void TextFiles_HaveNoDeveloperPaths(string package)
    {
        var kind = PackageKind.ByName(package);
        var root = Extracted[package].Value;
        var problems = new List<string>();
        foreach (var file in kind.ExpectedFiles.Where(f => f != kind.Exe))
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            // localhost is not checked here: the docs name it as the built-in default (no-network.md).
            foreach (var bad in new[] { "/Users/", @"C:\Users\", @"C:\\Users\\" })
                if (text.Contains(bad, StringComparison.OrdinalIgnoreCase)) problems.Add($"{file}: {bad}");
        }
        Assert.Empty(problems);
    }

    [WindowsTheory]
    [MemberData(nameof(Packages))]
    public void Pdb_NotShipped(string package)
    {
        var kind = PackageKind.ByName(package);
        Assert.Empty(Directory.GetFiles(Extracted[package].Value, "*.pdb", SearchOption.AllDirectories));
        using var zip = ZipFile.OpenRead(ZipPath(kind));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
    }

    [WindowsTheory]
    [MemberData(nameof(Packages))]
    public async Task Packaged_SelfTest_Exit0(string package)
    {
        // Both exes have the hidden offline self-test (the same Core rows): it proves the single file's bundled native SNI,
        // embedded prompts and tokenizer work without a server — the run path of pelly-eval, not only its --help.
        var kind = PackageKind.ByName(package);
        string[] args = kind.Name == "pelly" ? ["doctor", "--offline-selftest"] : ["--offline-selftest"];
        var result = await Run(kind, args);

        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}\n{result.StdOut}\n{result.StdErr}");
        foreach (var row in new[] { "토크나이저", "Roslyn", "ScriptDom", "인코딩", "리소스", "SqlClient" })
            Assert.Contains(row, result.StdOut, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task FieldKit_ValidateTemplates_Exit4AndNamesUnresolvedRoot()
    {
        // The templates hold placeholder paths (D:\src\<...>), so the suite cannot resolve: exit 4. suite.json itself must be
        // found and read (the error is about its roots, not a missing file), which shows the template folder layout works.
        var root = Extracted["field-kit"].Value;
        var result = await Run(PackageKind.FieldKit, ["validate", "--suite", Path.Combine(root, "templates")]);
        var output = result.StdOut + result.StdErr;

        Assert.True(result.ExitCode == 4, $"exit {result.ExitCode}\n{output}");
        Assert.Contains("스위트 오류", output, StringComparison.Ordinal);
        Assert.Contains("roots[0] (Modern)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("suite.json이 없습니다", output, StringComparison.Ordinal);
    }

    private static Task<ProcessResult> Run(PackageKind kind, string[] args)
    {
        var root = Extracted[kind.Name].Value;
        return new SystemProcessRunner(TimeProvider.System).RunAsync(
            new ProcessSpec(Path.Combine(root, kind.Exe), args, root, TimeSpan.FromMinutes(2)),
            TestContext.Current.CancellationToken);
    }

    private static string ZipPath(PackageKind kind)
    {
        var path = Environment.GetEnvironmentVariable(kind.ZipVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"{kind.ZipVariable} must name the built {kind.Name} zip (was '{path}')");
        return path;
    }

    private static string Extract(PackageKind kind)
    {
        var dir = Directory.CreateTempSubdirectory($"pelly-{kind.Name}-").FullName;
        ZipFile.ExtractToDirectory(ZipPath(kind), dir);
        return dir;
    }
}

/// <summary>Checks on the committed files that do not need the zip; these run on every OS.</summary>
public sealed class PackageSourceTests
{
    [Theory]
    [InlineData("xunit.runner.visualstudio 패키지", false)]
    [InlineData(@"D:\home\runner\work", true)]
    [InlineData("/home/runner/work", true)]
    [InlineData("checkout at /home/runner", true)]
    public void DeveloperUserName_CountsOnlyAsAPathSegment(string text, bool flagged) =>
        Assert.Equal(flagged, TemplateRules.Violations(text, "runner").Any(v => v.StartsWith("user name", StringComparison.Ordinal)));

    [Fact]
    public void RepoTemplate_HasNoSecretsOrRealHosts()
        => Assert.Empty(TemplateRules.Violations(File.ReadAllText(PackageFileList.RepoFile("deploy/pelly.json.template"))));

    [Fact]
    public void RepoTemplate_LoadsWithoutConfigErrors()
    {
        var result = PellyOptionsLoader.Load(new FileSystem(), PackageFileList.RepoFile("deploy/pelly.json.template"), null,
            new Dictionary<string, string?>(), new Dictionary<string, string?>());

        Assert.Empty(result.Errors);
        Assert.Equal("http://ollama.internal:11434", result.Options!.Llm.Endpoint);
        Assert.Empty(result.Options.Sql.AllowedHosts);
        Assert.Empty(result.Options.Sql.AllowedDatabases);
    }

    [Fact]
    public void FileList_MatchesBriefAndSourcesExist() => AssertListMatchesAndSourcesExist(PackageKind.Pelly);

    [Fact]
    public void FieldKitFileList_SourcesExist() => AssertListMatchesAndSourcesExist(PackageKind.FieldKit);

    private static void AssertListMatchesAndSourcesExist(PackageKind kind)
    {
        var entries = PackageFileList.Read(kind.ListFile);
        Assert.Equal(kind.ExpectedFiles.Order(StringComparer.Ordinal), entries.Select(e => e.ZipPath).Order(StringComparer.Ordinal));
        Assert.Contains(entries, e => e.ZipPath == kind.Exe && e.Source == "publish:" + kind.Exe);
        foreach (var (zipPath, source) in entries)
        {
            if (source is "generated" || source.StartsWith("publish:", StringComparison.Ordinal)) continue;
            Assert.True(File.Exists(PackageFileList.RepoFile(source)), $"{zipPath}: source {source} missing");
        }
    }
}

public sealed class VersionRecordTests
{
    private static readonly Regex Hex64 = new(@"\A[0-9a-f]{64}\z");

    [Fact]
    public void VersionRecord_HasModelIdentityAndNoAddress()
    {
        var text = File.ReadAllText(PackageFileList.RepoFile("deploy/version-record.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var root = doc.RootElement;
        string[] keys = ["pellyVersion", "ollamaVersion", "model", "modelDigest", "modelFormat", "modelQuantization", "modelGgufSha256", "recordedAt"];
        Assert.Equal(keys.Order(StringComparer.Ordinal), root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(typeof(PackageTests).Assembly.GetName().Version!.ToString(3), root.GetProperty("pellyVersion").GetString());
        Assert.Matches(Hex64, root.GetProperty("modelDigest").GetString()!);
        var gguf = root.GetProperty("modelGgufSha256");
        if (gguf.ValueKind != System.Text.Json.JsonValueKind.Null) Assert.Matches(Hex64, gguf.GetString()!);
        foreach (var bad in new[] { "http", "localhost", "127.0.0.1", "/Users/", @"C:\\Users" })
            Assert.DoesNotContain(bad, text, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class TemplateRules
{
    public static IReadOnlyList<string> Violations(string text) => Violations(text, Environment.UserName);

    /// <param name="user">
    /// The machine's user name. It counts only as a path segment (<c>/runner/</c>, <c>\runner\</c>, or at the end of a path), not
    /// anywhere in the text: the CI user is <c>runner</c>, which is also part of the package name <c>xunit.runner.visualstudio</c>.
    /// </param>
    public static IReadOnlyList<string> Violations(string text, string user)
    {
        var found = new List<string>();
        foreach (var bad in new[] { "Password", "Pwd", "localhost", "127.0.0.1", "/Users/", @"C:\Users\", @"C:\\Users\\" })
            if (text.Contains(bad, StringComparison.OrdinalIgnoreCase)) found.Add(bad);
        if (user.Length >= 3 && Regex.IsMatch(text, $@"[\\/]{Regex.Escape(user)}([\\/]|\z|\s)", RegexOptions.IgnoreCase))
            found.Add($"user name {user}");
        return found;
    }
}

/// <summary>Reads an allow-list (<c>deploy/package-files.txt</c>, <c>deploy/field-kit-files.txt</c>): one <c>zip path &lt;- source</c> per line, <c>#</c> comments.</summary>
internal static class PackageFileList
{
    private static readonly Regex Line = new(@"\A(\S+) <- (\S+)\z");

    public static IReadOnlyList<(string ZipPath, string Source)> Read(string listFile = "deploy/package-files.txt")
    {
        var result = new List<(string, string)>();
        foreach (var raw in File.ReadAllLines(RepoFile(listFile)))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = Line.Match(line);
            if (!m.Success) throw new FormatException($"{listFile}: bad line '{raw}'");
            result.Add((m.Groups[1].Value, m.Groups[2].Value));
        }
        return result;
    }

    public static IEnumerable<string> ZipPaths(string listFile = "deploy/package-files.txt") => Read(listFile).Select(e => e.ZipPath);

    public static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PellyCoder.sln")))
                return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        throw new DirectoryNotFoundException("PellyCoder.sln not found above the test binaries.");
    }
}
