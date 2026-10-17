using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Models;
using Diagnostic = PellyCoder.Core.Models.Diagnostic;

namespace PellyCoder.Core.CSharp;

/// <summary>Cheap pre-check: parse only, report syntax errors in source order (English messages).</summary>
public static class SyntaxVerifier
{
    public static VerifyOutcome Verify(string textLf, LanguageVersion version, string displayPath)
    {
        var tree = CSharpSyntaxTree.ParseText(textLf, new CSharpParseOptions(version));
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .OrderBy(d => d.Location.SourceSpan.Start)
            .Select(d =>
            {
                var pos = d.Location.GetLineSpan().StartLinePosition;
                return new Diagnostic(displayPath, pos.Line + 1, pos.Character + 1, "error", d.Id,
                    d.GetMessage(CultureInfo.InvariantCulture));
            })
            .ToList();
        return new VerifyOutcome(errors.Count == 0, errors, [], []);
    }
}
