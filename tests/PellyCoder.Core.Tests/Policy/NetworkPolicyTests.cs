using System.Text;
using System.Text.RegularExpressions;

namespace PellyCoder.Core.Tests.Policy;

/// <summary>
/// Source policy for "no external communication at runtime" (PRD §5): network, SQL connection and process creation
/// sites may only appear in the files listed below. Scope is <c>src/</c> only; <c>eval/</c> is a developer tool and is not shipped.
/// Comments and string/char literals are blanked before matching, so prose and <c>nameof</c>-style mentions do not count.
/// Only creation sites are checked: taking an injected <c>HttpClient</c> or using <c>SqlConnectionStringBuilder</c> is fine.
/// </summary>
public sealed class NetworkPolicyTests
{
    private const string Composition = "src/PellyCoder.Cli/Composition.cs";
    private const string SqlExecutor = "src/PellyCoder.Core/Sql/SqlServerExecutor.cs";
    private const string ProcessRunner = "src/PellyCoder.Core/Processes/SystemProcessRunner.cs";

    private sealed record Rule(string Name, Regex Pattern, string[] AllowedFiles);

    private static readonly Rule[] Rules =
    [
        new("HttpClient/SocketsHttpHandler creation", new Regex(@"\bnew\s+HttpClient\s*\(|\bSocketsHttpHandler\b"), [Composition]),
        new("SqlConnection creation", new Regex(@"\bnew\s+SqlConnection\s*\("), [SqlExecutor]),
        new("raw socket / legacy web API", new Regex(@"\bTcpClient\b|\bUdpClient\b|\bnew\s+Socket\s*\(|WebRequest\b|\bWebClient\b|\bDns\s*\."), []),
        new("process creation", new Regex(@"\bnew\s+Process\s*[({]|\bProcess\s*\.\s*Start\s*\(|\bnew\s+ProcessStartInfo\b"), [ProcessRunner]),
    ];

    [Fact]
    public void NetworkSqlAndProcessCreationAppearOnlyInAllowedFiles()
    {
        var root = FindRepoRoot();
        var violations = new List<string>();
        foreach (var file in SourceFiles(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            violations.AddRange(Scan(File.ReadAllText(file), rel));
        }

        Assert.True(violations.Count == 0, "Forbidden creation sites:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void AllowedFilesStillContainTheirCreationSites()
    {
        // Guards against the policy going blind after a rename: each allowed file must actually hit its rule.
        var root = FindRepoRoot();
        foreach (var rule in Rules.Where(r => r.AllowedFiles.Length > 0))
            foreach (var allowed in rule.AllowedFiles)
            {
                var text = Strip(File.ReadAllText(Path.Combine(root, allowed)));
                Assert.True(rule.Pattern.IsMatch(text), $"{allowed} no longer matches '{rule.Name}'; update the policy.");
            }
    }

    [Fact]
    public void Scanner_FlagsRealCreationAndIgnoresCommentsAndStrings()
    {
        const string code = """
            // new HttpClient( in a comment
            /* new TcpClient() */
            class C {
                string s = "new SqlConnection(x)";
                char c = 'x';
                object a = nameof(System.Net.Sockets.TcpClient);
                void M(HttpClient injected, SqlConnectionStringBuilder b) { }
                object p = new ProcessSpec("dotnet");
                void Bad() { var h = new HttpClient(); Dns.GetHostEntry("x"); Process.Start("x"); }
            }
            """;
        var hits = Scan(code, "x.cs");
        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, h => h.StartsWith("x.cs:9:") && h.Contains("HttpClient"));
        Assert.Contains(hits, h => h.Contains("raw socket"));
        Assert.Contains(hits, h => h.Contains("process creation"));
    }

    [Fact]
    public void PackageList_HasNoTelemetryOrUpdateCheckPackages()
    {
        var props = File.ReadAllText(Path.Combine(FindRepoRoot(), "Directory.Packages.props"));
        foreach (var banned in new[] { "ApplicationInsights", "OpenTelemetry", "Sentry" })
            Assert.DoesNotContain(banned, props, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> Scan(string source, string relPath)
    {
        var stripped = Strip(source);
        var hits = new List<string>();
        foreach (var rule in Rules)
        {
            if (rule.AllowedFiles.Contains(relPath, StringComparer.Ordinal))
                continue;
            foreach (Match m in rule.Pattern.Matches(stripped))
            {
                var line = 1 + stripped.AsSpan(0, m.Index).Count('\n');
                hits.Add($"{relPath}:{line}: {rule.Name}: '{m.Value}'");
            }
        }

        return hits;
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var parts = Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar);
                return !parts.Contains("bin") && !parts.Contains("obj");
            });

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        throw new DirectoryNotFoundException("repository root not found.");
    }

    /// <summary>Replaces comments and string/char literal contents with spaces, keeping newlines so line numbers survive.</summary>
    internal static string Strip(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            char n = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && n == '/')
            {
                while (i < s.Length && s[i] != '\n') { sb.Append(' '); i++; }
            }
            else if (c == '/' && n == '*')
            {
                sb.Append("  "); i += 2;
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')) { sb.Append(s[i] == '\n' ? '\n' : ' '); i++; }
                if (i < s.Length) { sb.Append("  "); i += 2; }
            }
            else if (c == '"' && n == '"' && i + 2 < s.Length && s[i + 2] == '"')
            {
                // raw string literal: """ ... """ (or longer quote runs)
                int q = 0;
                while (i + q < s.Length && s[i + q] == '"') q++;
                sb.Append(' ', q); i += q;
                while (i < s.Length)
                {
                    int run = 0;
                    while (i + run < s.Length && s[i + run] == '"') run++;
                    if (run >= q) { sb.Append(' ', run); i += run; break; }
                    sb.Append(s[i] == '\n' ? '\n' : ' '); i++;
                }
            }
            else if (c == '"')
            {
                bool verbatim = i > 0 && (s[i - 1] == '@' || (i > 1 && s[i - 2] == '@' && s[i - 1] == '$') || (i > 1 && s[i - 2] == '$' && s[i - 1] == '@'));
                sb.Append(' '); i++;
                while (i < s.Length)
                {
                    if (verbatim && s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"') { sb.Append("  "); i += 2; continue; }
                    if (!verbatim && s[i] == '\\' && i + 1 < s.Length) { sb.Append("  "); i += 2; continue; }
                    if (s[i] == '"') { sb.Append(' '); i++; break; }
                    if (!verbatim && s[i] == '\n') break;
                    sb.Append(s[i] == '\n' ? '\n' : ' '); i++;
                }
            }
            else if (c == '\'' && !(i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')))
            {
                sb.Append(' '); i++;
                while (i < s.Length && s[i] != '\'' && s[i] != '\n')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append("  "); i += 2; continue; }
                    sb.Append(' '); i++;
                }
                if (i < s.Length && s[i] == '\'') { sb.Append(' '); i++; }
            }
            else { sb.Append(c); i++; }
        }

        return NameofCall.Replace(sb.ToString(), m => new string(' ', m.Length));
    }

    private static readonly Regex NameofCall = new(@"\bnameof\s*\([^)]*\)");
}
