using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PellyCoder.Core.CharTests;

/// <summary>Parsed model output: <c>using</c> lines and the raw text of each new class member.</summary>
public sealed record CharTestOutput(IReadOnlyList<string> Usings, IReadOnlyList<string> Members);

/// <summary>Parses the model's "### USINGS / ### MEMBERS" output. Pelly prefills the "### USINGS" header.</summary>
public static class CharTestOutputParser
{
    private const string UsingsHeader = "### USINGS";
    private const string MembersHeader = "### MEMBERS";
    private static readonly Regex UsingLine = new(@"^using\s+(static\s+)?[A-Za-z_][\w.]*\s*;$", RegexOptions.Compiled);

    public static (CharTestOutput? Output, string? Error) Parse(string output, LanguageVersion version)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].Trim() == UsingsHeader) lines.RemoveAt(0);

        var usings = new List<string>();
        var members = new List<string>();
        var inMembers = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal)) continue;
            if (!inMembers)
            {
                if (line == MembersHeader) { inMembers = true; continue; }
                if (line.Length == 0) continue;
                if (!UsingLine.IsMatch(line)) return (null, $"USINGS 구역에 using 줄이 아닌 내용이 있습니다: {line}");
                usings.Add(line);
            }
            else
            {
                members.Add(raw.TrimEnd());
            }
        }

        if (!inMembers) return (null, "### MEMBERS 줄이 없습니다");

        var body = string.Join("\n", members);
        var tree = CSharpSyntaxTree.ParseText("class __P {\n" + body + "\n}", new CSharpParseOptions(version));
        var root = tree.GetCompilationUnitRoot();
        var error = root.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error is not null) return (null, $"MEMBERS 구문 오류: {error.GetMessage()}");

        var cls = root.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().FirstOrDefault();
        if (cls is null || root.Members.Count != 1) return (null, "MEMBERS 구문 오류: 클래스 본문 밖으로 벗어났습니다");

        var texts = cls.Members.Select(m => m.ToFullString().Trim()).ToList();
        return (new CharTestOutput(usings, texts), null);
    }
}
