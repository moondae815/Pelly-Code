using System.Globalization;
using System.Text;

namespace S6Planning;

public static class Report
{
    public static IReadOnlyList<RawRecord> LoadRaws(string rawDir) =>
        Directory.Exists(rawDir)
            ? Directory.GetFiles(rawDir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(f => RawRecord.FromJson(File.ReadAllText(f))).ToList()
            : [];

    public static IReadOnlyList<RunScore> ScoreAll(string rawDir, string casesDir, string corpusRoot, string? requiredGoldCommit = null)
    {
        var golds = GoldLoader.LoadAll(casesDir).ToDictionary(g => g.Id);
        var indexes = new Dictionary<string, CorpusIndex>();
        var scores = new List<RunScore>();
        foreach (var raw in LoadRaws(rawDir))
        {
            if (!golds.TryGetValue(raw.CaseId, out var gold))
                throw new InvalidDataException($"raw record for unknown case '{raw.CaseId}'");
            if (requiredGoldCommit is not null && raw.GoldCommit != requiredGoldCommit)
                throw new InvalidDataException($"raw {raw.Arm}/{raw.CaseId}/{raw.Rep} was produced against gold {raw.GoldCommit ?? "(none)"}, not the approved {requiredGoldCommit}.");
            if (!indexes.TryGetValue(gold.Id, out var idx))
                indexes[gold.Id] = idx = CorpusIndexBuilder.Build(corpusRoot, gold.Scope);
            scores.Add(Scorer.Score(gold, idx, raw.Arm, raw.Rep, raw.Output));
        }
        return scores;
    }

    public static string Csv(IReadOnlyList<RunScore> s)
    {
        var sb = new StringBuilder("CaseId,Category,Arm,Rep,Parsed,Recall,Precision,Hallucinated,Forbidden,OrderViolations,GroupOk,KindOk,Outside,ExampleCopy,Usable,NearMiss\n");
        foreach (var r in s)
            sb.AppendJoin(',', r.CaseId, r.Category, r.Arm, r.Rep, r.Parsed, F(r.Recall), F(r.Precision), r.Hallucinated, r.Forbidden,
                r.OrderViolations, r.GroupOk, r.KindOk, r.Outside, r.ExampleCopy, r.Usable, r.NearMiss).Append('\n');
        return sb.ToString();
    }

    public static string Summary(IReadOnlyList<RunScore> s, IReadOnlyList<RawRecord> raws, string? goldCommit = null)
    {
        var sb = new StringBuilder();
        var digests = raws.Select(r => r.ModelDigest).Where(d => d is not null).Distinct().ToList();
        sb.Append($"goldCommit `{goldCommit ?? "(not given)"}` · model digest {(digests.Count == 0 ? "(none)" : string.Join(", ", digests.Select(d => "`" + d + "`")))}\n\n");
        var modelArms = s.Select(x => x.Arm).Where(a => a != "C").Distinct().Order(StringComparer.Ordinal).ToList();

        sb.Append("## 범주별 「초안으로 쓸 만함」\n\n| 범주 |");
        foreach (var a in modelArms) sb.Append(" arm ").Append(a).Append(" |");
        sb.Append("\n| :--- |");
        foreach (var _ in modelArms) sb.Append(" ---: |");
        sb.Append('\n');
        foreach (var cat in "ABCD")
        {
            sb.Append("| ").Append(cat).Append(" |");
            foreach (var a in modelArms)
                sb.Append(' ').Append(Rate(s.Where(x => x.Arm == a && x.Category == cat).ToList())).Append(" |");
            sb.Append('\n');
        }
        sb.Append("| 전체 |");
        foreach (var a in modelArms) sb.Append(' ').Append(Rate(s.Where(x => x.Arm == a).ToList())).Append(" |");
        sb.Append("\n\n## arm별 지표\n\n| arm | 평균 재현율 | 평균 정밀도 | 파싱 실패 | 지어낸 대상이 있는 반복 | 금지 대상을 건드린 반복 | 순서 위반 반복 | 그중 표기만 틀린 반복 | 종류 오류 반복 | 예시 복사 | think 잘림 | 판정 |\n| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :--- |\n");
        foreach (var a in modelArms)
        {
            var x = s.Where(r => r.Arm == a).ToList();
            sb.Append($"| {a} | {F(x.Average(r => r.Recall))} | {F(x.Average(r => r.Precision))} | {x.Count(r => !r.Parsed)} | {x.Count(r => r.Hallucinated > 0)} | {x.Count(r => r.Forbidden > 0)} | {x.Count(r => r.OrderViolations > 0)} | {x.Count(r => r.NearMiss > 0)} | {x.Count(r => !r.KindOk)} | {x.Count(r => r.ExampleCopy)} | {raws.Count(r => r.Arm == a && r.ThinkTruncated)} | {Verdict.Decide(x)} |\n");
        }

        var c = s.Where(r => r.Arm == "C").ToList();
        if (c.Count > 0)
        {
            sb.Append("\n## 범주 A: 코드(arm C) 대 모델\n\n");
            sb.Append($"- arm C 평균 재현율 {F(c.Average(r => r.Recall))} (사례 {c.Count}건, 쓸 만함 {c.Count(r => r.Usable)}건)\n");
            foreach (var a in modelArms)
            {
                var x = s.Where(r => r.Arm == a && r.Category == 'A').ToList();
                if (x.Count > 0)
                    sb.Append($"- arm {a} 범주 A 평균 재현율 {F(x.Average(r => r.Recall))}\n");
            }
        }
        return sb.ToString();
    }

    private static string Rate(IReadOnlyList<RunScore> x) =>
        x.Count == 0 ? "-" : $"{x.Count(r => r.Usable)}/{x.Count} ({100.0 * x.Count(r => r.Usable) / x.Count:F0}%)";

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
