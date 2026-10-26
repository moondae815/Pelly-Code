using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Sql;

/// <summary>Normalization, hashing and CREATE-to-ALTER conversion of a stored definition (design 6.1, 5).</summary>
public static class SqlDefinitionText
{
    /// <summary>Unifies line endings, trims trailing spaces and surrounding blank lines, and writes the leading
    /// CREATE / ALTER / CREATE OR ALTER as CREATE. Comments, case and brackets are kept.</summary>
    public static string Normalize(string definition, int compatLevel)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var lines = definition.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.TrimEnd(' ', '\t')).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var text = string.Join('\n', lines);

        var kw = FindKeyword(text, compatLevel);
        if (kw is null)
            return text;
        return string.Concat(text.AsSpan(0, kw.Value.Start), "CREATE", text.AsSpan(kw.Value.Start + kw.Value.Length));
    }

    /// <summary>Lowercase hex SHA-256 of the normalized definition (UTF-8).</summary>
    public static string Hash(string definition, int compatLevel) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(definition, compatLevel))));

    /// <summary>Replaces only the first CREATE keyword with ALTER; CREATE OR ALTER and ALTER stay as they are.</summary>
    public static string ToAlter(string definition, int compatLevel)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var kw = FindKeyword(definition, compatLevel);
        if (kw is null || !kw.Value.IsCreate || kw.Value.IsCreateOrAlter)
            return definition;
        var start = kw.Value.Start;
        return string.Concat(definition.AsSpan(0, start), "ALTER", definition.AsSpan(start + kw.Value.Length));
    }

    /// <summary>The text of a pulled file: pull record, optional dev-differs line, SET lines, ALTER definition, GO.
    /// Line endings follow the definition. Encoding is the writer's concern.</summary>
    public static string RenderPullFile(SqlModuleDefinition module, PullRecord record, string? devDiffersHash, int compatLevel)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(record);
        var definition = module.Definition ?? throw new ArgumentException("Definition is null (encrypted object).", nameof(module));
        var eol = definition.Contains("\r\n") ? "\r\n" : "\n";
        var body = ToAlter(definition, compatLevel).TrimEnd('\r', '\n');

        var sb = new StringBuilder();
        sb.Append(record.Format()).Append(eol);
        if (devDiffersHash is not null)
            sb.Append("-- pelly pull: dev differs (sha256=").Append(devDiffersHash).Append(')').Append(eol);
        sb.Append("SET ANSI_NULLS ").Append(module.UsesAnsiNulls ? "ON" : "OFF").Append(eol).Append("GO").Append(eol);
        sb.Append("SET QUOTED_IDENTIFIER ").Append(module.UsesQuotedIdentifier ? "ON" : "OFF").Append(eol).Append("GO").Append(eol);
        sb.Append(body).Append(eol).Append("GO").Append(eol);
        return sb.ToString();
    }

    private readonly record struct Keyword(int Start, int Length, bool IsCreate, bool IsCreateOrAlter);

    /// <summary>The first CREATE or ALTER token outside comments; for CREATE OR ALTER the range spans all three words.</summary>
    private static Keyword? FindKeyword(string text, int compatLevel)
    {
        var parser = SqlParsers.Create(compatLevel, quotedIdentifier: true);
        IList<TSqlParserToken> tokens;
        using (var reader = new StringReader(text))
            tokens = parser.GetTokenStream(reader, out _);

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.TokenType == TSqlTokenType.Alter)
                return new Keyword(t.Offset, t.Text.Length, false, false);
            if (t.TokenType != TSqlTokenType.Create)
                continue;

            var j = i + 1;
            while (j < tokens.Count && IsTrivia(tokens[j])) j++;
            if (j < tokens.Count && tokens[j].TokenType == TSqlTokenType.Or)
            {
                var k = j + 1;
                while (k < tokens.Count && IsTrivia(tokens[k])) k++;
                if (k < tokens.Count && tokens[k].TokenType == TSqlTokenType.Alter)
                    return new Keyword(t.Offset, tokens[k].Offset + tokens[k].Text.Length - t.Offset, true, true);
            }
            return new Keyword(t.Offset, t.Text.Length, true, false);
        }
        return null;
    }

    private static bool IsTrivia(TSqlParserToken t) =>
        t.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;
}
