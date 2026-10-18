using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Shop.Data.JudgeTests
{
    /// <summary>
    /// 가짜 DB가 SQL의 마지막 <c>ORDER BY</c>를 행에 적용한다. 정렬을 SQL로 고친 수정과 C#으로 고친 수정을 같은 기준으로 판정하기 위함이다.
    /// 지원하는 식: 열 이름(별칭·대괄호 허용), 숫자·문자열 상수, <c>CASE WHEN 식 = 값 THEN … ELSE … END</c>, <c>CASE 식 WHEN 값 THEN …</c>,
    /// <c>CAST(식 AS 형식)</c>, <c>CONVERT(형식, 식)</c>, 각 항의 <c>ASC</c>/<c>DESC</c>. 그 밖의 식은 해석할 수 없다는 예외를 던진다(판정 실패로 보인다).
    /// <c>ORDER BY</c>가 없으면 넣은 순서 그대로 돌려준다(SQL Server도 순서를 보장하지 않는다).
    /// </summary>
    public static class FakeOrderBy
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

        public static IReadOnlyList<IDictionary<string, object>> Apply(string sql, IEnumerable<IDictionary<string, object>> rows)
        {
            var list = rows.ToList();
            var matches = Regex.Matches(sql, @"\bORDER\s+BY\b", Options);
            if (matches.Count == 0) return list;
            var last = matches[matches.Count - 1];
            var clause = sql.Substring(last.Index + last.Length);
            var stop = Regex.Match(clause, @"\bOFFSET\b|;|\bFOR\s+(XML|JSON)\b", Options);
            if (stop.Success) clause = clause.Substring(0, stop.Index);

            IOrderedEnumerable<IDictionary<string, object>> ordered = null;
            foreach (var term in SplitTopLevel(clause))
            {
                var t = term.Trim();
                var desc = false;
                var dir = Regex.Match(t, @"\s+(ASC|DESC)\s*\z", Options);
                if (dir.Success)
                {
                    desc = dir.Groups[1].Value.Equals("DESC", StringComparison.OrdinalIgnoreCase);
                    t = t.Substring(0, dir.Index).Trim();
                }
                var expr = t;
                Func<IDictionary<string, object>, object> key = row => Evaluate(expr, row);
                ordered = ordered == null
                    ? (desc ? list.OrderByDescending(key, ValueComparer.Instance) : list.OrderBy(key, ValueComparer.Instance))
                    : (desc ? ordered.ThenByDescending(key, ValueComparer.Instance) : ordered.ThenBy(key, ValueComparer.Instance));
            }
            return ordered == null ? list : ordered.ToList();
        }

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            var depth = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth--;
                else if (text[i] == ',' && depth == 0)
                {
                    yield return text.Substring(start, i - start);
                    start = i + 1;
                }
            }
            if (text.Substring(start).Trim().Length > 0) yield return text.Substring(start);
        }

        private static object Evaluate(string expr, IDictionary<string, object> row)
        {
            var e = expr.Trim();
            while (e.StartsWith("(", StringComparison.Ordinal) && e.EndsWith(")", StringComparison.Ordinal) && Balanced(e.Substring(1, e.Length - 2)))
                e = e.Substring(1, e.Length - 2).Trim();

            if (decimal.TryParse(e, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) return number;
            var literal = Regex.Match(e, @"\AN?'(?<v>[^']*)'\z", Options);
            if (literal.Success) return literal.Groups["v"].Value;

            var cast = Regex.Match(e, @"\ACAST\s*\((?<x>.+)\s+AS\s+\w+(\s*\([0-9, ]+\))?\s*\)\z", Options);
            if (cast.Success) return Evaluate(cast.Groups["x"].Value, row);
            var convert = Regex.Match(e, @"\ACONVERT\s*\(\s*\w+(\s*\([0-9, ]+\))?\s*,(?<x>.+)\)\z", Options);
            if (convert.Success) return Evaluate(convert.Groups["x"].Value, row);

            var caseExpr = Regex.Match(e, @"\ACASE\s+(?<body>.+)\s+END\z", Options);
            if (caseExpr.Success) return EvaluateCase(caseExpr.Groups["body"].Value, row);

            var column = Regex.Match(e, @"\A(\[?\w+\]?\.)?\[?(?<c>\w+)\]?\z", Options);
            if (column.Success)
            {
                if (!row.TryGetValue(column.Groups["c"].Value, out var value))
                    throw new NotSupportedException($"판정 가짜 DB: ORDER BY의 열 '{e}'이(가) 결과에 없습니다.");
                return Normalize(value);
            }

            throw new NotSupportedException($"판정 가짜 DB가 해석하지 못하는 ORDER BY 식: {e}");
        }

        private static object EvaluateCase(string body, IDictionary<string, object> row)
        {
            string input = null;
            var firstWhen = Regex.Match(body, @"\bWHEN\b", Options);
            if (firstWhen.Index > 0) input = body.Substring(0, firstWhen.Index);
            var rest = body.Substring(firstWhen.Index);
            string elseValue = null;
            var elseMatch = Regex.Match(rest, @"\s+ELSE\s+(?<v>.+)\z", Options);
            if (elseMatch.Success)
            {
                elseValue = elseMatch.Groups["v"].Value;
                rest = rest.Substring(0, elseMatch.Index);
            }

            foreach (Match when in Regex.Matches(rest, @"\bWHEN\s+(?<c>.+?)\s+THEN\s+(?<v>.+?)(?=\s+WHEN\b|\z)", Options))
            {
                bool hit;
                if (input != null)
                {
                    hit = ValueComparer.Instance.Compare(Evaluate(input, row), Evaluate(when.Groups["c"].Value, row)) == 0;
                }
                else
                {
                    var cond = Regex.Match(when.Groups["c"].Value, @"\A(?<l>.+?)\s*(?<op><>|!=|>=|<=|=|<|>)\s*(?<r>.+)\z", Options);
                    if (!cond.Success) throw new NotSupportedException($"판정 가짜 DB가 해석하지 못하는 CASE 조건: {when.Groups["c"].Value}");
                    var cmp = ValueComparer.Instance.Compare(Evaluate(cond.Groups["l"].Value, row), Evaluate(cond.Groups["r"].Value, row));
                    switch (cond.Groups["op"].Value)
                    {
                        case "=": hit = cmp == 0; break;
                        case "<>":
                        case "!=": hit = cmp != 0; break;
                        case ">": hit = cmp > 0; break;
                        case "<": hit = cmp < 0; break;
                        case ">=": hit = cmp >= 0; break;
                        default: hit = cmp <= 0; break;
                    }
                }
                if (hit) return Evaluate(when.Groups["v"].Value, row);
            }
            return elseValue == null ? null : Evaluate(elseValue, row);
        }

        private static bool Balanced(string s)
        {
            var depth = 0;
            foreach (var ch in s)
            {
                if (ch == '(') depth++;
                else if (ch == ')' && --depth < 0) return false;
            }
            return depth == 0;
        }

        private static object Normalize(object value)
        {
            switch (value)
            {
                case null: return null;
                case bool b: return b ? 1m : 0m;
                case string s: return s;
                case DateTime d: return d;
                default: return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>SQL Server처럼 NULL이 가장 앞. 숫자는 숫자로, 나머지는 서수 문자열로 비교한다.</summary>
        private sealed class ValueComparer : IComparer<object>
        {
            public static readonly ValueComparer Instance = new ValueComparer();

            public int Compare(object x, object y)
            {
                x = Normalize(x);
                y = Normalize(y);
                if (x == null || y == null) return x == null ? (y == null ? 0 : -1) : 1;
                if (x is decimal a && y is decimal b) return a.CompareTo(b);
                if (x is DateTime da && y is DateTime db) return da.CompareTo(db);
                return string.CompareOrdinal(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture));
            }
        }
    }
}
