using Acornima;
using Acornima.Ast;

namespace PellyCoder.Core.Web;

/// <summary>A syntax feature with the level that introduced it; <paramref name="Name"/> is shown to users, <paramref name="EnglishName"/> to the model.</summary>
public sealed record JsFeature(string Name, string EnglishName, EcmaVersion Level);

/// <summary>Names the first syntax feature in a file that is above a target level.</summary>
public static class JsFeatures
{
    private static readonly JsFeature LetConst = new("let/const", "let/const", EcmaVersion.ES2015);
    private static readonly JsFeature Arrow = new("화살표 함수(=>)", "arrow functions", EcmaVersion.ES2015);
    private static readonly JsFeature Template = new("템플릿 문자열(`)", "template literals", EcmaVersion.ES2015);
    private static readonly JsFeature Class = new("class", "classes", EcmaVersion.ES2015);
    private static readonly JsFeature Destructuring = new("구조 분해", "destructuring", EcmaVersion.ES2015);
    private static readonly JsFeature DefaultParameter = new("매개변수 기본값", "default parameters", EcmaVersion.ES2015);
    private static readonly JsFeature Spread = new("...(펼치기)", "spread/rest (...)", EcmaVersion.ES2015);
    private static readonly JsFeature ForOf = new("for…of", "for...of", EcmaVersion.ES2015);
    private static readonly JsFeature Exponent = new("거듭제곱(**)", "exponentiation (**)", EcmaVersion.ES2016);
    private static readonly JsFeature Async = new("async/await", "async/await", EcmaVersion.ES2017);
    private static readonly JsFeature ObjectSpread = new("...(펼치기)", "object spread/rest (...)", EcmaVersion.ES2018);
    private static readonly JsFeature OptionalChaining = new("?.", "optional chaining (?.)", EcmaVersion.ES2020);
    private static readonly JsFeature Nullish = new("??", "nullish coalescing (??)", EcmaVersion.ES2020);

    /// <summary>Every feature the checks know, lowest level first.</summary>
    public static IReadOnlyList<JsFeature> Table { get; } =
    [
        LetConst, Arrow, Template, Class, Destructuring, DefaultParameter, Spread, ForOf,
        Exponent, Async, ObjectSpread, OptionalChaining, Nullish,
    ];

    /// <summary>Features introduced above <paramref name="target"/>, in table order.</summary>
    public static IReadOnlyList<JsFeature> Above(EcmaVersion target) =>
        Table.Where(f => JsLevels.Compare(f.Level, target) > 0).ToList();

    /// <summary>
    /// The first feature above <paramref name="target"/> with its line; the feature itself, since display names are shared
    /// (array and object spread are both 「...(펼치기)」) and the model needs the exact English name.
    /// </summary>
    public static (JsFeature Feature, int Line)? FirstAbove(string textLf, bool module, EcmaVersion target)
    {
        if (JsDocument.TryParse(textLf, EcmaVersion.Latest, module, out var program) is not null || program is null)
            return null;

        (JsFeature Feature, int Line, int Col)? best = null;
        // Object spread / rest are ES2018; array and argument spread / rest are ES2015.
        var objectLevel = new HashSet<Node>();
        foreach (var n in JsAst.Descendants(program))
        {
            if (n is ObjectExpression oe) foreach (var p in oe.Properties) if (p is SpreadElement) objectLevel.Add(p);
            if (n is ObjectPattern op) foreach (var p in op.Properties) if (p is RestElement) objectLevel.Add(p);
            var hit = objectLevel.Contains(n) ? ObjectSpread : Classify(n);
            if (hit is null || JsLevels.Compare(hit.Level, target) <= 0) continue;
            var s = n.Location.Start;
            if (best is null || s.Line < best.Value.Line || (s.Line == best.Value.Line && s.Column < best.Value.Col))
                best = (hit, s.Line, s.Column);
        }
        return best is null ? null : (best.Value.Feature, best.Value.Line);
    }

    private static JsFeature? Classify(Node n) => n switch
    {
        ArrowFunctionExpression a => a.Async ? Async : Arrow,
        TemplateLiteral => Template,
        VariableDeclaration { Kind: not VariableDeclarationKind.Var } => LetConst,
        ClassDeclaration or ClassExpression => Class,
        ObjectPattern or ArrayPattern => Destructuring,
        AssignmentPattern => DefaultParameter,
        SpreadElement or RestElement => Spread,
        ForOfStatement => ForOf,
        BinaryExpression { Operator: Operator.Exponentiation } => Exponent,
        AssignmentExpression { Operator: Operator.ExponentiationAssignment } => Exponent,
        FunctionDeclaration { Async: true } or FunctionExpression { Async: true } => Async,
        AwaitExpression => Async,
        ChainExpression => OptionalChaining,
        BinaryExpression { Operator: Operator.NullishCoalescing } => Nullish,
        _ => null,
    };
}
