using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Rules;

/// <summary>
/// The code-only view of a file for <c>forbid</c> checks: comments and string/char literal text become spaces, everything else
/// (line breaks included) stays at the same position, so line N of the mask is line N of the file.
/// </summary>
public static class CodeMask
{
    /// <summary>
    /// Masks C# comments (doc comments included), inactive <c>#if</c> text, and string/char literal text. The expressions inside an
    /// interpolated string stay, because they are code. Roslyn always produces a tree, so this never fails.
    /// </summary>
    public static string CSharp(string text, LanguageVersion version)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(version));
        var spans = new List<TextSpan>();
        var root = tree.GetRoot();
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                spans.Add(trivia.FullSpan);
        }
        foreach (var token in root.DescendantTokens())
        {
            if (IsCSharpLiteralText(token.Kind())) spans.Add(token.Span);
        }
        return Blank(text, spans.Select(s => (s.Start, s.Length)));
    }

    /// <summary>
    /// Masks T-SQL comments and string literals using the ScriptDom lexer for <paramref name="compatLevel"/>. With
    /// <paramref name="quotedIdentifier"/> off, double-quoted text is a string. Null when the text does not lex cleanly (the
    /// caller then checks the raw text, which only makes the check stricter).
    /// </summary>
    public static string? Sql(string text, int compatLevel, bool quotedIdentifier)
    {
        var parser = SqlParsers.Create(compatLevel, quotedIdentifier);
        var tokens = parser.GetTokenStream(new StringReader(text), out var errors);
        if (errors.Count > 0) return null;
        // The lexer leaves "..." as AsciiStringOrQuotedIdentifier either way; with QUOTED_IDENTIFIER OFF the server reads it as a string.
        var spans = tokens
            .Where(t => t.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment
                    or TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral
                || (!quotedIdentifier && t.TokenType == TSqlTokenType.AsciiStringOrQuotedIdentifier))
            .Select(t => (t.Offset, t.Text.Length));
        return Blank(text, spans);
    }

    private static bool IsCSharpLiteralText(SyntaxKind kind) => kind is SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken
        or SyntaxKind.Utf8StringLiteralToken or SyntaxKind.InterpolatedStringTextToken
        or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
        or SyntaxKind.Utf8SingleLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken;

    /// <summary>Every character inside the spans becomes a space, except line breaks.</summary>
    private static string Blank(string text, IEnumerable<(int Start, int Length)> spans)
    {
        var chars = text.ToCharArray();
        foreach (var (start, length) in spans)
        {
            var end = Math.Min(chars.Length, start + length);
            for (var i = Math.Max(0, start); i < end; i++)
                if (chars[i] is not ('\n' or '\r')) chars[i] = ' ';
        }
        return new string(chars);
    }
}
