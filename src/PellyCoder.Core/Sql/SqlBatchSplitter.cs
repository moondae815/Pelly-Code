using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PellyCoder.Core.Sql;

public enum SqlObjectKind { Procedure, Function, View }

/// <summary>A ScriptDom parse error; <paramref name="Line"/> is relative to the whole file.</summary>
public sealed record SqlParseError(int Line, int Column, int Number, string Message);

/// <summary>One GO-delimited batch. Lines are 1-based and inclusive; GO lines are not part of any batch.</summary>
public sealed record SqlBatch(
    int Index,
    int StartLine,
    int EndLine,
    string Text,
    SqlObjectKind? ObjectKind,
    SqlObjectName? ObjectName,
    string? StatementType,
    IReadOnlyList<SqlParseError> ParseErrors);

public sealed record SqlSessionSettings(bool AnsiNulls, bool QuotedIdentifier)
{
    public static SqlSessionSettings Default { get; } = new(true, true);
}

public sealed record SqlScript(IReadOnlyList<SqlBatch> Batches, IReadOnlyList<string> LexErrors)
{
    internal IReadOnlyList<SqlSessionSettings> SettingsBeforeBatch { get; init; } = [];

    public IReadOnlyList<SqlBatch> ObjectBatches => Batches.Where(b => b.ObjectKind is not null).ToList();

    /// <summary>The ANSI_NULLS / QUOTED_IDENTIFIER values in force before the batch, from the last standalone SET batch.</summary>
    public SqlSessionSettings SettingsBefore(int batchIndex)
    {
        if (batchIndex < 0 || batchIndex >= Batches.Count)
            throw new ArgumentOutOfRangeException(nameof(batchIndex));
        return batchIndex < SettingsBeforeBatch.Count ? SettingsBeforeBatch[batchIndex] : SqlSessionSettings.Default;
    }
}

/// <summary>Splits an SSMS-style script (LF-normalised, no BOM) into GO batches and classifies object batches.</summary>
public static class SqlBatchSplitter
{
    public static SqlScript Split(string textLf, int compatLevel)
    {
        ArgumentNullException.ThrowIfNull(textLf);

        // Settings are only known after splitting, so tokenize with QUOTED_IDENTIFIER ON.
        var tokenizer = SqlParsers.Create(compatLevel, quotedIdentifier: true);
        IList<TSqlParserToken> tokens;
        IList<ParseError> lexErrors;
        using (var reader = new StringReader(textLf))
            tokens = tokenizer.GetTokenStream(reader, out lexErrors);

        var lexMessages = lexErrors.Select(e => $"line {e.Line}, column {e.Column}: {e.Message}").ToList();
        var lines = textLf.Split('\n');
        var goLines = tokens.Where(t => t.TokenType == TSqlTokenType.Go).Select(t => t.Line).Distinct().OrderBy(l => l).ToList();

        var segments = new List<(int Start, int End)>();
        var start = 1;
        foreach (var go in goLines)
        {
            segments.Add((start, go - 1));
            start = go + 1;
        }
        segments.Add((start, lines.Length));

        var batches = new List<SqlBatch>();
        var settingsBefore = new List<SqlSessionSettings>();
        var current = SqlSessionSettings.Default;

        foreach (var (s, e) in segments)
        {
            if (e < s)
                continue;
            var text = string.Join('\n', lines[(s - 1)..e]);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var batch = Analyze(batches.Count, s, e, text, compatLevel, current, out var standalone);
            batches.Add(batch);
            settingsBefore.Add(current);
            if (standalone is { } next)
                current = next(current);
        }

        return new SqlScript(batches, lexMessages) { SettingsBeforeBatch = settingsBefore };
    }

    private static SqlBatch Analyze(
        int index, int startLine, int endLine, string text, int compatLevel,
        SqlSessionSettings settings, out Func<SqlSessionSettings, SqlSessionSettings>? standaloneSet)
    {
        standaloneSet = null;
        var parser = SqlParsers.Create(compatLevel, settings.QuotedIdentifier);
        TSqlFragment fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(text))
            fragment = parser.Parse(reader, out errors);

        var parseErrors = errors
            .Select(e => new SqlParseError(e.Line + startLine - 1, e.Column, e.Number, e.Message))
            .ToList();

        TSqlStatement? first = null;
        var statementCount = 0;
        if (fragment is TSqlScript script)
        {
            foreach (var b in script.Batches)
            {
                foreach (var st in b.Statements)
                {
                    first ??= st;
                    statementCount++;
                }
            }
        }

        SqlObjectKind? kind = null;
        SqlObjectName? name = null;
        if (first is not null)
            (kind, name) = Classify(first);

        if (first is PredicateSetStatement set && statementCount == 1 && parseErrors.Count == 0)
        {
            var options = set.Options;
            var isOn = set.IsOn;
            standaloneSet = prev => new SqlSessionSettings(
                options.HasFlag(SetOptions.AnsiNulls) ? isOn : prev.AnsiNulls,
                options.HasFlag(SetOptions.QuotedIdentifier) ? isOn : prev.QuotedIdentifier);
        }

        return new SqlBatch(index, startLine, endLine, text, kind, name, first?.GetType().Name, parseErrors);
    }

    private static (SqlObjectKind?, SqlObjectName?) Classify(TSqlStatement statement) => statement switch
    {
        ProcedureStatementBody p => (SqlObjectKind.Procedure, ToName(p.ProcedureReference?.Name)),
        FunctionStatementBody f => (SqlObjectKind.Function, ToName(f.Name)),
        ViewStatementBody v => (SqlObjectKind.View, ToName(v.SchemaObjectName)),
        _ => (null, null),
    };

    private static SqlObjectName? ToName(SchemaObjectName? name) =>
        name?.BaseIdentifier is null ? null : new SqlObjectName(name.SchemaIdentifier?.Value, name.BaseIdentifier.Value);
}
