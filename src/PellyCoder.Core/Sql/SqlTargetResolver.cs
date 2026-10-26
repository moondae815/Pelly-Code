namespace PellyCoder.Core.Sql;

/// <param name="Target">The chosen object batch, or null when <paramref name="Problem"/> is set.</param>
/// <param name="Ambiguous">True when several object batches exist and no name was given (exit 3 for callers).</param>
public sealed record SqlTargetResolution(SqlBatch? Target, string? Problem, bool Ambiguous);

/// <summary>
/// Picks the object batch a command works on without interaction: <c>--object</c> by name, otherwise the only object batch.
/// Uses the same name rules as the locate phase (<see cref="SqlObjectName.Parse"/>, <see cref="SqlObjectName.SameAs"/>).
/// </summary>
public static class SqlTargetResolver
{
    public static SqlTargetResolution Resolve(SqlScript script, string? objectName)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.LexErrors.Count > 0)
            return new(null, $"SQL 파일을 해석할 수 없습니다: {script.LexErrors[0]}", false);

        if (objectName is not null)
        {
            SqlObjectName wanted;
            try
            {
                wanted = SqlObjectName.Parse(objectName);
            }
            catch (FormatException ex)
            {
                return new(null, $"--object 이름을 해석할 수 없습니다: {ex.Message}", false);
            }
            var matches = script.ObjectBatches.Where(b => b.ObjectName is { } n && n.SameAs(wanted)).ToList();
            if (matches.Count == 0)
                return new(null, $"대상 객체 '{objectName}'을(를) 파일에서 찾을 수 없습니다", false);
            if (matches.Count > 1)
                return new(null, $"대상 객체 '{objectName}'이(가) 파일에 {matches.Count}번 정의되어 있습니다", true);
            return new(matches[0], null, false);
        }

        var all = script.ObjectBatches.Where(b => b.ObjectName is not null).ToList();
        if (all.Count == 0)
            return new(null, "파일에 프로시저·함수·뷰 정의가 없습니다", false);
        if (all.Count > 1)
            return new(null, $"파일에 객체가 {all.Count}개 있습니다. --object <이름>으로 대상을 지정하세요 ({string.Join(", ", all.Select(b => b.ObjectName!.Display))})", true);
        return new(all[0], null, false);
    }
}
