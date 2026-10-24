using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Rules;

/// <summary>A proposed rule: the full AGENTS.md rule line and a one-line Korean evidence string.</summary>
public sealed record DraftRule(string Line, string Evidence);

public sealed record DraftResult(IReadOnlyList<DraftRule> Rules, int CsFiles, int SqlFiles);

/// <summary>
/// Deterministic, LLM-free analysis of a codebase's .cs and .sql files that proposes short project rules. Counts aggregate over
/// the whole codebase; a rule is proposed only when the sample reaches its minimum and the ratio reaches its threshold.
/// </summary>
public static partial class RulesDrafter
{
    public const int MaxDirectories = 2000;
    public const int MaxCsFiles = 500;
    public const int MaxSqlFiles = 300;
    public const int MaxFileBytes = 512 * 1024;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    public static DraftResult Draft(IFileSystem fs, string root, CancellationToken ct = default)
    {
        var cs = new CsStats();
        var csFiles = 0;
        foreach (var path in BoundedWalker.Files(fs, root, "*.cs", MaxDirectories, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (csFiles >= MaxCsFiles) break; // the cap counts analysed files, not skipped ones
            if (!TryRead(fs, path, out var text)) continue;
            csFiles++;
            cs.Add(CSharpSyntaxTree.ParseText(text, ParseOptions, cancellationToken: ct).GetRoot(ct));
        }

        var sql = new SqlStats();
        var sqlFiles = 0;
        foreach (var path in BoundedWalker.Files(fs, root, "*.sql", MaxDirectories, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (sqlFiles >= MaxSqlFiles) break;
            if (!TryRead(fs, path, out var text)) continue;
            sqlFiles++;
            sql.Add(text);
        }

        var star = new List<DraftRule>();
        var rest = new List<DraftRule>();
        void Emit(bool isStar, string line, string evidence) => (isStar ? star : rest).Add(new DraftRule(line, evidence));

        // async-suffix
        if (cs.AsyncChecked >= 10 && Ratio(cs.AsyncSuffixed, cs.AsyncChecked) >= 0.9m)
            Emit(false, "- [async] async 메서드 이름은 Async로 끝낸다", $"async 메서드 {cs.AsyncChecked}개 중 {cs.AsyncSuffixed}개가 Async로 끝남");

        // no-blocking
        if (cs.AsyncAll >= 10 && cs.Blocking == 0)
            Emit(false, @"- [async] .Result나 .Wait()로 비동기 결과를 기다리지 않는다 | forbid: \.Result\b|\.Wait\(\)", $"async 메서드 {cs.AsyncAll}개, .Result/.Wait()/GetResult() 사용 0건");

        // configure-await
        if (cs.Awaits >= 20 && Ratio(cs.ConfigureAwaitFalse, cs.Awaits) >= 0.8m)
            Emit(false, "- [await] await하는 Task에는 ConfigureAwait(false)를 붙인다", $"await {cs.Awaits}개 중 {cs.ConfigureAwaitFalse}개에 ConfigureAwait(false)");

        // dapper
        if (cs.DapperFiles >= 1)
            Emit(false, "- [dapper] SQL 값은 Dapper 파라미터로 넘기고 문자열 연결·보간으로 만들지 않는다", $"using Dapper; 파일 {cs.DapperFiles}개");

        // korean-comments
        if (cs.Comments >= 20 && Ratio(cs.KoreanComments, cs.Comments) >= 0.6m)
            Emit(true, "- [*] 주석은 한국어로 작성한다", $"주석 {cs.Comments}개 중 {cs.KoreanComments}개가 한국어");

        // exception-language
        if (cs.Throws >= 10)
        {
            var ratio = Ratio(cs.KoreanThrows, cs.Throws);
            if (ratio >= 0.6m)
                Emit(false, "- [throw] 예외 메시지는 한국어로 작성한다", $"throw 메시지 {cs.Throws}개 중 {cs.KoreanThrows}개가 한국어");
            else if (ratio <= 0.1m)
                Emit(false, "- [throw] 예외 메시지는 영어로 작성한다", $"throw 메시지 {cs.Throws}개 중 {cs.Throws - cs.KoreanThrows}개가 한국어 아님");
        }

        // var-style
        if (cs.Locals >= 50)
        {
            var ratio = Ratio(cs.VarLocals, cs.Locals);
            if (ratio >= 0.8m)
                Emit(true, "- [*] 지역 변수는 가능하면 var로 선언한다", $"지역 변수 선언 {cs.Locals}개 중 {cs.VarLocals}개가 var");
            else if (ratio <= 0.2m)
                Emit(true, "- [*] 지역 변수는 var 대신 명시적 타입으로 선언한다", $"지역 변수 선언 {cs.Locals}개 중 {cs.Locals - cs.VarLocals}개가 명시적 타입");
        }

        // sql-nocount
        if (sql.Procedures >= 3 && Ratio(sql.WithNoCount, sql.Procedures) >= 0.8m)
            Emit(false, "- [procedure] 프로시저 본문 처음에 SET NOCOUNT ON을 둔다", $"프로시저 {sql.Procedures}개 중 {sql.WithNoCount}개에 SET NOCOUNT ON");

        // sql-schema
        if (sql.Names >= 20 && Ratio(sql.Qualified, sql.Names) >= 0.9m)
            Emit(false, "- [procedure,function,view] 테이블·뷰를 참조할 때 스키마 이름(dbo. 등)을 붙인다", $"FROM/JOIN 객체 {sql.Names}개 중 {sql.Qualified}개에 스키마 이름");

        star.AddRange(rest);
        return new DraftResult(star, csFiles, sqlFiles);
    }

    private static decimal Ratio(int part, int total) => (decimal)part / total;

    private static bool TryRead(IFileSystem fs, string path, out string text)
    {
        text = "";
        try
        {
            if (fs.FileInfo.New(path).Length > MaxFileBytes) return false;
            text = TextDecoding.Decode(fs.File.ReadAllBytes(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool HasHangul(string s)
    {
        foreach (var c in s)
            if (c is >= '가' and <= '힣') return true;
        return false;
    }

    private sealed class CsStats
    {
        public int AsyncAll, AsyncChecked, AsyncSuffixed, Blocking, Awaits, ConfigureAwaitFalse, DapperFiles;
        public int Comments, KoreanComments, Throws, KoreanThrows, Locals, VarLocals;

        public void Add(SyntaxNode root)
        {
            var hasDapper = false;
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case MethodDeclarationSyntax m when m.Modifiers.Any(SyntaxKind.AsyncKeyword):
                        AsyncAll++;
                        if (!m.Modifiers.Any(SyntaxKind.OverrideKeyword) && m.Identifier.ValueText != "Main")
                        {
                            AsyncChecked++;
                            if (m.Identifier.ValueText.EndsWith("Async", StringComparison.Ordinal)) AsyncSuffixed++;
                        }
                        break;
                    case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Result" }:
                    case MemberBindingExpressionSyntax { Name.Identifier.ValueText: "Result" }:
                        Blocking++;
                        break;
                    case InvocationExpressionSyntax inv:
                        CountInvocation(inv);
                        break;
                    case AwaitExpressionSyntax aw:
                        Awaits++;
                        if (aw.Expression is InvocationExpressionSyntax
                            {
                                Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" },
                                ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax lit }],
                            } && lit.IsKind(SyntaxKind.FalseLiteralExpression))
                            ConfigureAwaitFalse++;
                        break;
                    case UsingDirectiveSyntax { StaticKeyword.RawKind: 0, Alias: null } u when u.Name?.ToString() == "Dapper":
                        hasDapper = true;
                        break;
                    case ThrowStatementSyntax { Expression: { } te }:
                        CountThrow(te);
                        break;
                    case ThrowExpressionSyntax tx:
                        CountThrow(tx.Expression);
                        break;
                    case LocalDeclarationStatementSyntax ld when !ld.IsConst && ld.Declaration.Type is not RefTypeSyntax:
                        Locals++;
                        if (ld.Declaration.Type.IsVar) VarLocals++;
                        break;
                }
            }

            if (hasDapper) DapperFiles++;

            foreach (var trivia in root.DescendantTrivia())
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                    && !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                    continue;
                Comments++;
                if (HasHangul(trivia.ToString())) KoreanComments++;
            }
        }

        private void CountInvocation(InvocationExpressionSyntax inv)
        {
            var (name, target) = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => (ma.Name.Identifier.ValueText, ma.Expression),
                MemberBindingExpressionSyntax mb => (mb.Name.Identifier.ValueText, null),
                _ => ("", null),
            };
            if (name == "Wait" && inv.ArgumentList.Arguments.Count == 0) Blocking++;
            else if (name == "GetResult" && target is InvocationExpressionSyntax { Expression: var getAwaiter }
                && getAwaiter is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetAwaiter" } or MemberBindingExpressionSyntax { Name.Identifier.ValueText: "GetAwaiter" })
                Blocking++;
        }

        private void CountThrow(ExpressionSyntax? expr)
        {
            var args = expr switch
            {
                ObjectCreationExpressionSyntax o => o.ArgumentList?.Arguments,
                ImplicitObjectCreationExpressionSyntax i => i.ArgumentList.Arguments,
                _ => null,
            };
            if (args is not { Count: > 0 } list) return;
            var first = list[0].Expression;
            if (first is not (LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } or InterpolatedStringExpressionSyntax)) return;
            Throws++;
            if (HasHangul(first.ToString())) KoreanThrows++;
        }
    }

    private sealed class SqlStats
    {
        public int Procedures, WithNoCount, Names, Qualified;

        public void Add(string raw)
        {
            // Comments and string literals blanked by the ScriptDom lexer; the regex strip only when the text does not lex.
            var text = CodeMask.Sql(raw, compatLevel: 160, quotedIdentifier: true) ?? BlockComment().Replace(LineComment().Replace(raw, ""), "");

            var defs = Definition().Matches(text);
            var goLines = GoLine().Matches(text);
            for (var i = 0; i < defs.Count; i++)
            {
                var d = defs[i];
                var kind = d.Groups[1].Value;
                if (!kind.StartsWith("PROC", StringComparison.OrdinalIgnoreCase)) continue;
                var start = d.Index + d.Length;
                var end = i + 1 < defs.Count ? defs[i + 1].Index : text.Length;
                foreach (Match go in goLines)
                {
                    if (go.Index >= start && go.Index < end) { end = go.Index; break; }
                }
                Procedures++;
                if (NoCount().IsMatch(text.AsSpan(start, end - start))) WithNoCount++;
            }

            foreach (Match m in FromJoin().Matches(text))
            {
                var name = m.Groups[1].Value;
                if (name.Length == 0 || name[0] is '#' or '@' or '(') continue;
                Names++;
                if (name.Contains('.')) Qualified++;
            }
        }
    }

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"\b(?:CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Definition();

    [GeneratedRegex(@"^[ \t]*GO[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoLine();

    [GeneratedRegex(@"\bSET\s+NOCOUNT\s+ON\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoCount();

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+(\[?[\w#@]+\]?(?:\.\[?\w+\]?)*)", RegexOptions.IgnoreCase)]
    private static partial Regex FromJoin();
}
