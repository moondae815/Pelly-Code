using System.IO.Abstractions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Verification;

namespace PellyCoder.Core.Testing;

/// <summary>Compile-RED verdict: <c>Unverifiable</c> holds the user message when the baseline build errors are not a compile RED.</summary>
public sealed record CompileRedVerdict(bool IsCompileRed, string? Unverifiable);

/// <summary>
/// Judges whether a test project's baseline build errors are a compile RED: the named tests do not compile yet because they use
/// code the group has still to write. That holds when every error is a compiler error, inside the test project's folder, and at
/// least one lies inside the declaration of a named test method.
/// </summary>
public static class CompileRed
{
    /// <summary>
    /// <paramref name="buildErrors"/> paths are absolute or relative to <paramref name="workspaceRoot"/> (either separator);
    /// <paramref name="testProject"/> is the project file. No errors at all is not a compile RED and not unverifiable either.
    /// </summary>
    public static CompileRedVerdict Judge(IFileSystem fs, string workspaceRoot, string testProject,
        IReadOnlyList<Diagnostic> buildErrors, IReadOnlyList<TestMethodDecl> declsInProject)
    {
        if (buildErrors.Count == 0) return new CompileRedVerdict(false, null);
        if (buildErrors.FirstOrDefault(e => !BuildMessages.IsCompilerError(e)) is { } blocker)
            return Unverifiable($"테스트 프로젝트를 원본 상태에서 빌드할 수 없습니다: {blocker.Code}");

        var projectDir = fs.Path.GetDirectoryName(fs.Path.GetFullPath(testProject))!;
        var errors = buildErrors.Select(e => (Error: e, File: Resolve(fs, workspaceRoot, e.File))).ToList();
        if (errors.FirstOrDefault(e => !IsUnder(fs, projectDir, e.File)) is { Error: not null } outside)
            return Unverifiable($"테스트 프로젝트 밖에 원래 컴파일 오류가 있습니다: {Relative(fs, workspaceRoot, outside.File)}");

        var spans = declsInProject.SelectMany(d => Spans(fs, d)).ToList();
        if (errors.Any(e => spans.Any(s => PathComparers.ForOs.Equals(s.File, e.File) && e.Error.Line >= s.First && e.Error.Line <= s.Last)))
            return new CompileRedVerdict(true, null);

        var first = errors[0];
        return Unverifiable($"테스트 프로젝트의 컴파일 오류가 지정한 테스트 안에 없습니다: {Relative(fs, workspaceRoot, first.File)}({first.Error.Line})");
    }

    private static CompileRedVerdict Unverifiable(string message) => new(false, message);

    private static string Resolve(IFileSystem fs, string root, string file)
    {
        if (file.Length == 0) return "";
        var local = file.Replace('\\', fs.Path.DirectorySeparatorChar).Replace('/', fs.Path.DirectorySeparatorChar);
        return fs.Path.GetFullPath(fs.Path.Combine(root, local));
    }

    private static bool IsUnder(IFileSystem fs, string dir, string file)
    {
        if (file.Length == 0) return false;
        var prefix = fs.Path.EndsInDirectorySeparator(dir) ? dir : dir + fs.Path.DirectorySeparatorChar;
        return file.StartsWith(prefix, PathComparers.ForOs == StringComparer.Ordinal
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(IFileSystem fs, string root, string file) =>
        file.Length == 0 ? "" : fs.Path.GetRelativePath(root, file);

    /// <summary>1-based line spans of the methods in the declaring file named like the test (its last name segment).</summary>
    private static IEnumerable<(string File, int First, int Last)> Spans(IFileSystem fs, TestMethodDecl decl)
    {
        var file = fs.Path.GetFullPath(decl.File);
        string text;
        try
        {
            text = fs.File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        var name = decl.FullName[(decl.FullName.LastIndexOf('.') + 1)..];
        var tree = CSharpSyntaxTree.ParseText(text);
        foreach (var m in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (m.Identifier.ValueText != name) continue;
            var span = tree.GetLineSpan(m.Span);
            yield return (file, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
        }
    }
}
