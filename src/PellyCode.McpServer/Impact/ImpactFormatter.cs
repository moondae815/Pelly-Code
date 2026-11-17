using PellyCode.Core.Impact;

namespace PellyCode.McpServer.Impact;

/// <summary>One place of a SQL or JS search: workspace-relative path with '/', the member or function around it, the reason.</summary>
public sealed record ImpactPlace(string Path, int? Line, string Member, string Reason);

/// <summary>The <c>pelly_impact</c> text (design §6): heading, notes on how the places were found, one place per line.</summary>
public static class ImpactFormatter
{
    public const int MaxPlaces = 50;

    public const string Unsupported = "메서드와 속성만 찾을 수 있습니다";

    /// <summary>ASP·JS·SQL→ASP need an <c>asp</c> or <c>web</c> section in the settings (design §6).</summary>
    public const string NoWebSettings = "설정에 asp 절이 없어 ASP·JS 부분은 건너뜀";

    public const string Incomplete = "일부 폴더·파일을 읽지 못해 빠진 곳이 있을 수 있습니다";

    /// <summary>A SQL or JS result: always name-based, places sorted by path then line.</summary>
    public static string FormatPlaces(string heading, IReadOnlyList<string> notes, IReadOnlyList<ImpactPlace> places)
    {
        var lines = new List<string> { heading };
        lines.AddRange(notes);
        var sorted = places.OrderBy(p => p.Path, StringComparer.Ordinal).ThenBy(p => p.Line ?? 0).ToList();
        lines.Add($"부르는 곳 {sorted.Count}곳 (이름 기준):");
        foreach (var p in sorted.Take(MaxPlaces))
            lines.Add($"- {(p.Line is { } line ? $"{p.Path}:{line}" : p.Path)} {p.Member} — {p.Reason}");
        if (sorted.Count > MaxPlaces)
            lines.Add($"외 {sorted.Count - MaxPlaces}곳");
        return string.Join('\n', lines);
    }

    /// <param name="heading">The <c>대상: …</c> line.</param>
    /// <param name="typeOf">The type that holds a place, or null when unknown.</param>
    public static string Format(string heading, ImpactResult result, Func<ImpactTarget, string?> typeOf)
    {
        var lines = new List<string> { heading };
        if (result.Mode == ImpactMode.Name)
            lines.Add($"이름 기준으로 찾았습니다(빠진 곳이 있을 수 있음) — 의미 분석 실패: {result.Fallback?.Display ?? "알 수 없음"}");
        if (result.Incomplete)
            lines.Add(Incomplete);
        if (result.UnsupportedSeeds.Count > 0)
        {
            lines.Add(Unsupported);
            return string.Join('\n', lines);
        }
        if (result.SeedsNotFound.Count > 0)
        {
            lines.Add("불러온 프로젝트에서 대상을 찾지 못했습니다(프로젝트에 들지 않은 파일일 수 있음)");
            return string.Join('\n', lines);
        }

        var targets = result.Targets;
        lines.Add($"부르는 곳 {targets.Count}곳 ({(result.Mode == ImpactMode.Semantic ? "Roslyn 의미 분석" : "이름 기준")}):");
        foreach (var t in targets.Take(MaxPlaces))
        {
            var where = t.Line is { } line ? $"{t.Path}:{line}" : t.Path;
            var who = t.Reason == "멤버 밖" || typeOf(t) is not { } type ? t.Member : $"{type}.{t.Member}";
            lines.Add($"- {where} {who} — {t.Reason}");
        }
        if (targets.Count > MaxPlaces)
            lines.Add($"외 {targets.Count - MaxPlaces}곳");
        return string.Join('\n', lines);
    }
}
