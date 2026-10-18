using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PellyCoder.Core.Sql;

/// <summary>Creates the ScriptDom parser that matches a database compatibility level.</summary>
public static class SqlParsers
{
    public static IReadOnlyList<int> SupportedLevels { get; } = [90, 100, 110, 120, 130, 140, 150, 160, 170];

    public static TSqlParser Create(int compatLevel, bool quotedIdentifier) => compatLevel switch
    {
        90 => new TSql90Parser(quotedIdentifier),
        100 => new TSql100Parser(quotedIdentifier),
        110 => new TSql110Parser(quotedIdentifier),
        120 => new TSql120Parser(quotedIdentifier),
        130 => new TSql130Parser(quotedIdentifier),
        140 => new TSql140Parser(quotedIdentifier),
        150 => new TSql150Parser(quotedIdentifier),
        160 => new TSql160Parser(quotedIdentifier),
        170 => new TSql170Parser(quotedIdentifier),
        _ => throw new ArgumentOutOfRangeException(nameof(compatLevel), compatLevel,
            "Supported compatibility levels: " + string.Join(", ", SupportedLevels)),
    };
}
