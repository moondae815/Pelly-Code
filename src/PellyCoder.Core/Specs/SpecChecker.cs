using System.IO.Abstractions;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;
using Workspace = PellyCoder.Core.Workspaces.Workspace;

namespace PellyCoder.Core.Specs;

/// <param name="Impact">The impact search result; null when no search ran or it threw.</param>
public sealed record SpecCheckResult(ChangeSpec Spec, Workspace Workspace, IReadOnlyList<SpecIssue> Issues, ImpactResult? Impact = null)
{
    public bool HasErrors => Issues.Any(i => i.Severity == SpecIssueSeverity.Error);
}

/// <summary>Static checks of a change spec against the workspace (no model, no DB): grammar, targets, AC tests, names.</summary>
public static partial class SpecChecker
{
    private const int MaxDirectories = 2000;
    private const int MaxFiles = 5000;
    private const int MaxFileBytes = 512 * 1024;

    public static SpecCheckResult Check(IFileSystem fs, string specPath, int sqlCompatLevel)
    {
        var spec = ChangeSpecParser.Parse(TextFileCodec.Read(fs, specPath).Text);
        var ws = WorkspaceLocator.Resolve(fs, specPath);
        var issues = new List<SpecIssue>(spec.Issues);
        var scans = new Scans(fs, ws.Root);

        foreach (var task in spec.Tasks)
            CheckTask(fs, ws, task, sqlCompatLevel, scans, issues);

        foreach (var ac in spec.Criteria.Where(c => c.Test is not null))
        {
            if (!scans.TestExists(ac.Test!))
                issues.Add(new SpecIssue(0, SpecIssueSeverity.Warning, "TestNotFound", $"수용 기준 {ac.Id}의 테스트 {ac.Test}를 작업 공간에서 찾을 수 없습니다."));
        }

        return new SpecCheckResult(spec, ws, issues);
    }

    /// <summary>
    /// <see cref="Check"/>, then — when it found no error and the spec has seeds — the impact search, whose findings are
    /// added as warnings. A search that throws (anything but a cancellation) becomes the warning <c>ImpactFailed</c>:
    /// the warning feature never blocks a spec.
    /// </summary>
    /// <param name="impactAllCsTasks">Seed every not-done cs task (<c>spec check --impact</c>), not just the grouped ones.</param>
    public static async Task<SpecCheckResult> CheckAsync(
        IFileSystem fs, string specPath, int sqlCompatLevel, IImpactFinder? impact, bool impactAllCsTasks, CancellationToken ct)
    {
        var check = Check(fs, specPath, sqlCompatLevel);
        if (impact is null || check.HasErrors) return check;
        var seeds = ImpactCheck.Seeds(check.Spec, impactAllCsTasks);
        if (seeds.Count == 0) return check;

        ImpactResult result;
        try
        {
            result = await impact.FindAsync(check.Workspace, seeds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = new SpecIssue(0, SpecIssueSeverity.Warning, "ImpactFailed", $"영향 탐색을 할 수 없습니다: {ex.Message}");
            return check with { Issues = [.. check.Issues, failed] };
        }

        return check with { Issues = [.. check.Issues, .. ImpactCheck.Issues(check.Spec, seeds, result)], Impact = result };
    }

    private static void CheckTask(IFileSystem fs, Workspace ws, SpecTask task, int compat, Scans scans, List<SpecIssue> issues)
    {
        var before = issues.Count;
        void Error(string code, string message) => issues.Add(new SpecIssue(task.Line, SpecIssueSeverity.Error, code, message));

        var full = fs.Path.GetFullPath(fs.Path.Combine(ws.Root, task.Path.Replace('\\', '/')));
        if (!IsInside(fs, ws.Root, full)) { Error("OutsideWorkspace", $"작업 {task.Id}: 경로가 작업 공간 밖입니다: {task.Path}"); return; }
        if (!fs.File.Exists(full)) { Error("FileNotFound", $"작업 {task.Id}: 파일이 없습니다: {task.Path}"); return; }
        var expectedExt = task.Kind == TaskKind.CSharp ? ".cs" : ".sql";
        if (!string.Equals(fs.Path.GetExtension(full), expectedExt, StringComparison.OrdinalIgnoreCase))
        {
            Error("KindMismatch", $"작업 {task.Id}: {(task.Kind == TaskKind.CSharp ? "cs" : "sql")} 작업의 파일이 {expectedExt}가 아닙니다: {task.Path}");
            return;
        }

        FileSnapshot snapshot;
        try { snapshot = TextFileCodec.Read(fs, full); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Error("FileUnreadable", $"작업 {task.Id}: 파일을 읽을 수 없습니다: {task.Path} ({ex.Message})");
            return;
        }

        string? csprojDir = null;
        if (task.Kind == TaskKind.CSharp)
        {
            if (task.Symbol is null) { Error("SymbolRequired", $"작업 {task.Id}: cs 작업은 #심볼로 대상 멤버를 지정해야 합니다."); return; }
            // A done task is never run again, and its target may be gone by design (a group renamed it): --resume must not trip on it.
            if (task.Done) return;
            var csproj = ProjectFileLocator.FindNearestCsproj(fs, full);
            LanguageTarget language;
            try { language = LanguageVersionDetector.Detect(fs, csproj); }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                Error("ProjectFileUnreadable", $"작업 {task.Id}: 프로젝트 파일을 읽을 수 없습니다: {csproj} ({ex.Message})");
                return;
            }

            csprojDir = csproj is null ? ws.Root : fs.Path.GetDirectoryName(csproj)!;
            var (className, member) = SpecSymbol.Split(task.Symbol);
            var found = MemberLocator.FindByName(snapshot.Text, language.Version, className, member);
            if (found.Count == 0) Error("SymbolNotFound", $"작업 {task.Id}: 심볼을 찾을 수 없습니다: {task.Symbol}");
            else if (found.Count > 1) Error("SymbolAmbiguous", $"작업 {task.Id}: 심볼 {task.Symbol}가 {found.Count}곳과 맞습니다: {string.Join(", ", found.Select(f => f.Display))}");
        }
        else
        {
            if (task.Done) return;
            var batches = SqlBatchSplitter.Split(snapshot.Text, compat).ObjectBatches;
            if (task.Symbol is not null)
            {
                SqlObjectName? wanted = null;
                try { wanted = SqlObjectName.Parse(task.Symbol); }
                catch (FormatException ex) { Error("SqlObjectNotFound", $"작업 {task.Id}: SQL 객체 이름이 올바르지 않습니다: {ex.Message}"); }
                if (wanted is not null && !batches.Any(b => b.ObjectName?.SameAs(wanted) == true))
                    Error("SqlObjectNotFound", $"작업 {task.Id}: SQL 객체를 찾을 수 없습니다: {task.Symbol}");
            }
            else if (batches.Count != 1)
                Error("SqlObjectAmbiguous", $"작업 {task.Id}: #객체 없이는 파일에 객체 배치가 정확히 하나여야 합니다(현재 {batches.Count}개).");
        }

        if (issues.Count > before) return; // names in the request are only worth checking against a valid target
        var known = task.Kind == TaskKind.CSharp ? scans.CsIdentifiers(csprojDir!) : scans.SqlIdentifiers();
        foreach (var word in RequestIdentifiers.Extract(task.Request))
        {
            // A dotted or backticked word is known when every identifier-like part of it is.
            var parts = IdentifierPart().Matches(word).Select(m => m.Value).Where(p => !char.IsDigit(p[0]));
            if (parts.Any(p => !known.Contains(p)))
                issues.Add(new SpecIssue(task.Line, SpecIssueSeverity.Warning, "UnknownIdentifier", $"작업 {task.Id}: 요청문의 이름 {word}을(를) 소스에서 찾을 수 없습니다."));
        }
    }

    private static bool IsInside(IFileSystem fs, string root, string full)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.TrimEnd(fs.Path.DirectorySeparatorChar, fs.Path.AltDirectorySeparatorChar) + fs.Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, cmp);
    }

    [GeneratedRegex("[A-Za-z0-9_]+")]
    private static partial Regex IdentifierPart();

    /// <summary>Lazy, cached bounded scans of the workspace sources.</summary>
    private sealed partial class Scans(IFileSystem fs, string root)
    {
        private readonly Dictionary<string, HashSet<string>> cs = new(StringComparer.Ordinal);
        private HashSet<string>? sql;
        private HashSet<(string? Type, string Method)>? methods;

        /// <summary>Identifier tokens (Roslyn tokenizer) of every .cs file under the project folder.</summary>
        public HashSet<string> CsIdentifiers(string dir)
        {
            if (cs.TryGetValue(dir, out var set)) return set;
            set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var text in Read(dir, "*.cs"))
                foreach (var t in CSharpSyntaxTree.ParseText(text).GetRoot().DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
                    set.Add(t.ValueText);
            return cs[dir] = set;
        }

        /// <summary>Identifier-looking words of every .sql file (regex; T-SQL is case-insensitive).</summary>
        public HashSet<string> SqlIdentifiers()
        {
            if (sql is not null) return sql;
            sql = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in Read(root, "*.sql"))
                foreach (Match m in SqlWord().Matches(text)) sql.Add(m.Value);
            return sql;
        }

        /// <summary>True when a method declaration named <c>Class.Method</c> or <c>Method</c> exists in a workspace .cs file.</summary>
        public bool TestExists(string name)
        {
            methods ??= CollectMethods();
            var (cls, method) = SpecSymbol.Split(name);
            return methods.Any(m => m.Method == method && (cls is null || m.Type == cls));
        }

        private HashSet<(string? Type, string Method)> CollectMethods()
        {
            var set = new HashSet<(string?, string)>();
            foreach (var text in Read(root, "*.cs"))
                foreach (var m in CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
                    set.Add(((m.Parent as BaseTypeDeclarationSyntax)?.Identifier.ValueText, m.Identifier.ValueText));
            return set;
        }

        private IEnumerable<string> Read(string dir, string pattern)
        {
            foreach (var path in BoundedWalker.Files(fs, dir, pattern, MaxDirectories).Take(MaxFiles))
            {
                string text;
                try
                {
                    if (fs.FileInfo.New(path).Length > MaxFileBytes) continue;
                    text = TextFileCodec.Read(fs, path).Text;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { continue; }
                yield return text;
            }
        }

        [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*")]
        private static partial Regex SqlWord();
    }
}
