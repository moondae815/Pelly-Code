using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Package;

/// <summary>
/// Checks the air-gap zip built by <c>deploy/build-package.ps1</c> (Task 9). The zip path comes from
/// <see cref="ZipVariable"/>; the CI <c>package</c> job sets it and runs only this class.
/// A missing variable on Windows is a failure, not a skip: the job must not pass without looking at the zip.
/// </summary>
public sealed class PackageTests
{
    public const string ZipVariable = "PELLY_TEST_PACKAGE";

    // The brief's allow-list (Task 9). deploy/package-files.txt must say the same; the zip must contain exactly this.
    internal static readonly string[] ExpectedFiles =
    [
        "pelly.exe",
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
    ];

    private static readonly Lazy<string> Extracted = new(Extract);

    private static readonly Regex SumLine = new(@"\A([0-9a-f]{64})  ([^ ].*)\z");

    [WindowsFact]
    public void Package_ContainsExactlyAllowedFiles()
    {
        var root = Extracted.Value;
        var actual = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(ExpectedFiles.Order(StringComparer.Ordinal), actual);
        Assert.Equal(ExpectedFiles.Order(StringComparer.Ordinal), PackageFileList.ZipPaths().Order(StringComparer.Ordinal));
    }

    [WindowsFact]
    public void Package_ChecksumsMatch()
    {
        var root = Extracted.Value;
        var lines = File.ReadAllLines(Path.Combine(root, "SHA256SUMS.txt"));
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var m = SumLine.Match(line);
            Assert.True(m.Success, $"bad SHA256SUMS line: '{line}'");
            Assert.True(listed.TryAdd(m.Groups[2].Value, m.Groups[1].Value), $"duplicate entry: {m.Groups[2].Value}");
        }

        var files = ExpectedFiles.Where(f => f != "SHA256SUMS.txt").Order(StringComparer.Ordinal).ToArray();
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
        var text = File.ReadAllText(Path.Combine(Extracted.Value, "pelly.json.template"));
        Assert.Empty(TemplateRules.Violations(text));
    }

    [WindowsFact]
    public void TextFiles_HaveNoDeveloperPaths()
    {
        var root = Extracted.Value;
        var problems = new List<string>();
        foreach (var file in ExpectedFiles.Where(f => f != "pelly.exe"))
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            // localhost is not checked here: the docs name it as the built-in default (no-network.md).
            foreach (var bad in new[] { "/Users/", @"C:\Users\", @"C:\\Users\\" })
                if (text.Contains(bad, StringComparison.OrdinalIgnoreCase)) problems.Add($"{file}: {bad}");
        }
        Assert.Empty(problems);
    }

    [WindowsFact]
    public void Pdb_NotShipped()
    {
        Assert.Empty(Directory.GetFiles(Extracted.Value, "*.pdb", SearchOption.AllDirectories));
        using var zip = ZipFile.OpenRead(ZipPath());
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
    }

    [WindowsFact]
    public async Task Packaged_SelfTest_Exit0()
    {
        var root = Extracted.Value;
        var result = await new SystemProcessRunner(TimeProvider.System).RunAsync(
            new ProcessSpec(Path.Combine(root, "pelly.exe"), ["doctor", "--offline-selftest"], root, TimeSpan.FromMinutes(2)),
            TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}\n{result.StdOut}\n{result.StdErr}");
    }

    private static string ZipPath()
    {
        var path = Environment.GetEnvironmentVariable(ZipVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"{ZipVariable} must name the built pelly-<version>-win-x64.zip (was '{path}')");
        return path;
    }

    private static string Extract()
    {
        var dir = Directory.CreateTempSubdirectory("pelly-package-").FullName;
        ZipFile.ExtractToDirectory(ZipPath(), dir);
        return dir;
    }
}

/// <summary>Checks on the committed files that do not need the zip; these run on every OS.</summary>
public sealed class PackageSourceTests
{
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
    public void FileList_MatchesBriefAndSourcesExist()
    {
        var entries = PackageFileList.Read();
        Assert.Equal(PackageTests.ExpectedFiles.Order(StringComparer.Ordinal), entries.Select(e => e.ZipPath).Order(StringComparer.Ordinal));
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
        string[] keys = ["pellyVersion", "ollamaVersion", "model", "modelDigest", "modelFormat", "modelQuantization", "recordedAt"];
        Assert.Equal(keys.Order(StringComparer.Ordinal), root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(typeof(PackageTests).Assembly.GetName().Version!.ToString(3), root.GetProperty("pellyVersion").GetString());
        Assert.Matches(Hex64, root.GetProperty("modelDigest").GetString()!);
        foreach (var bad in new[] { "http", "localhost", "127.0.0.1", "/Users/", @"C:\\Users" })
            Assert.DoesNotContain(bad, text, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class TemplateRules
{
    public static IReadOnlyList<string> Violations(string text)
    {
        var found = new List<string>();
        foreach (var bad in new[] { "Password", "Pwd", "localhost", "127.0.0.1", "/Users/", @"C:\Users\", @"C:\\Users\\" })
            if (text.Contains(bad, StringComparison.OrdinalIgnoreCase)) found.Add(bad);
        var user = Environment.UserName;
        if (user.Length >= 3 && text.Contains(user, StringComparison.OrdinalIgnoreCase)) found.Add($"user name {user}");
        return found;
    }
}

/// <summary>Reads <c>deploy/package-files.txt</c>: one <c>zip path &lt;- source</c> per line, <c>#</c> comments.</summary>
internal static class PackageFileList
{
    private static readonly Regex Line = new(@"\A(\S+) <- (\S+)\z");

    public static IReadOnlyList<(string ZipPath, string Source)> Read()
    {
        var result = new List<(string, string)>();
        foreach (var raw in File.ReadAllLines(RepoFile("deploy/package-files.txt")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = Line.Match(line);
            if (!m.Success) throw new FormatException($"package-files.txt: bad line '{raw}'");
            result.Add((m.Groups[1].Value, m.Groups[2].Value));
        }
        return result;
    }

    public static IEnumerable<string> ZipPaths() => Read().Select(e => e.ZipPath);

    public static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PellyCoder.sln")))
                return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        throw new DirectoryNotFoundException("PellyCoder.sln not found above the test binaries.");
    }
}
