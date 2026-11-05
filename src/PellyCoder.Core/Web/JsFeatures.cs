using Acornima;
using Acornima.Ast;

namespace PellyCoder.Core.Web;

/// <summary>Names the first syntax feature in a file that is above a target level.</summary>
public static class JsFeatures
{
    public static (string Feature, int Line)? FirstAbove(string textLf, bool module, EcmaVersion target)
    {
        if (JsDocument.TryParse(textLf, EcmaVersion.Latest, module, out var program) is not null || program is null)
            return null;

        (string Feature, int Line, int Col)? best = null;
        foreach (var n in JsAst.Descendants(program))
        {
            var hit = Classify(n);
            if (hit is null || JsLevels.Compare(hit.Value.Level, target) <= 0) continue;
            var s = n.Location.Start;
            if (best is null || s.Line < best.Value.Line || (s.Line == best.Value.Line && s.Column < best.Value.Col))
                best = (hit.Value.Feature, s.Line, s.Column);
        }
        return best is null ? null : (best.Value.Feature, best.Value.Line);
    }

    private static (string Feature, EcmaVersion Level)? Classify(Node n) => n switch
    {
        ArrowFunctionExpression a => a.Async ? ("async/await", EcmaVersion.ES2017) : ("화살표 함수(=>)", EcmaVersion.ES2015),
        TemplateLiteral => ("템플릿 문자열(`)", EcmaVersion.ES2015),
        VariableDeclaration { Kind: not VariableDeclarationKind.Var } => ("let/const", EcmaVersion.ES2015),
        ClassDeclaration or ClassExpression => ("class", EcmaVersion.ES2015),
        ObjectPattern or ArrayPattern => ("구조 분해", EcmaVersion.ES2015),
        AssignmentPattern => ("매개변수 기본값", EcmaVersion.ES2015),
        SpreadElement or RestElement => ("...(펼치기)", EcmaVersion.ES2015),
        ForOfStatement => ("for…of", EcmaVersion.ES2015),
        BinaryExpression { Operator: Operator.Exponentiation } => ("거듭제곱(**)", EcmaVersion.ES2016),
        AssignmentExpression { Operator: Operator.ExponentiationAssignment } => ("거듭제곱(**)", EcmaVersion.ES2016),
        FunctionDeclaration { Async: true } or FunctionExpression { Async: true } => ("async/await", EcmaVersion.ES2017),
        AwaitExpression => ("async/await", EcmaVersion.ES2017),
        ChainExpression => ("?.", EcmaVersion.ES2020),
        BinaryExpression { Operator: Operator.NullishCoalescing } => ("??", EcmaVersion.ES2020),
        _ => null,
    };
}
