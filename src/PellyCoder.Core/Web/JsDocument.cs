using System.Text;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Web;

public sealed record JsFunction(string Name, string? Owner, IReadOnlyList<string> Parameters, int StartLine, int DeclLine, int EndLine, string Declaration)
{
    public string QualifiedName => Owner is null ? Name : $"{Owner}.{Name}";
}

public enum JsArgKind { None, StringLiteral, StringConcat, Other }

public sealed record JsCall(string Callee, bool IsNew, int Line, string Text, JsArgKind FirstArg);

public sealed record JsNameUse(string Name, int Line, string? Function);

internal static class JsAst
{
    /// <summary>Pre-order traversal of every node below (and including) the root.</summary>
    public static IEnumerable<Node> Descendants(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            var kids = n.ChildNodes.Where(c => c is not null).ToList();
            for (var i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
    }
}

/// <summary>A parsed JavaScript file: functions, top-level names, free names and calls.</summary>
public sealed class JsDocument
{
    private sealed class Scope
    {
        public Scope(Scope? parent, bool isFunction) { Parent = parent; IsFunction = isFunction; }
        public Scope? Parent { get; }
        public bool IsFunction { get; }
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        public Scope FunctionScope() { var s = this; while (!s.IsFunction && s.Parent is not null) s = s.Parent; return s; }
        public bool Resolves(string name) { for (var s = this; s is not null; s = s.Parent) if (s.Names.Contains(name)) return true; return false; }
    }

    private readonly string[] _lines;
    private readonly string _source;
    private readonly List<JsFunction> _functions = new();
    private readonly List<JsCall> _calls = new();
    private readonly List<(string Name, int Line, Scope Scope, string? Function)> _refs = new();
    private readonly List<(int Start, int End)> _spans = new();
    private readonly Dictionary<Node, (string Name, string? Owner)> _named = new();
    private Scope _global = new(null, true);

    private JsDocument(string text, bool module)
    {
        _source = text;
        _lines = text.Split('\n');
        Module = module;
    }

    public JsParseError? Error { get; private set; }
    public IReadOnlyList<JsFunction> Functions => _functions;
    public IReadOnlySet<string> TopLevelNames { get; private set; } = new HashSet<string>();
    public IReadOnlyList<JsNameUse> FreeNames { get; private set; } = Array.Empty<JsNameUse>();
    public IReadOnlyList<JsCall> Calls => _calls;
    public bool Module { get; }

    public string LineText(int line) => line >= 1 && line <= _lines.Length ? _lines[line - 1] : "";

    public bool CutsStatement(TextSpanInfo range)
    {
        foreach (var (s, e) in _spans)
        {
            var startsIn = s >= range.StartLine && s <= range.EndLine;
            var endsIn = e >= range.StartLine && e <= range.EndLine;
            if (startsIn != endsIn) return true;
        }
        return false;
    }

    /// <summary>Parses and returns the error (1-based column) or null; the program is returned on success.</summary>
    internal static JsParseError? TryParse(string text, EcmaVersion level, bool module, out Program? program)
    {
        program = null;
        try
        {
            var parser = new Parser(new ParserOptions { EcmaVersion = level });
            program = module ? parser.ParseModule(text) : parser.ParseScript(text);
            return null;
        }
        catch (ParseErrorException e)
        {
            return new JsParseError(e.LineNumber, e.Column + 1, e.Description);
        }
    }

    public static JsDocument Parse(string textLf, EcmaVersion level, bool module)
    {
        var doc = new JsDocument(textLf, module);
        var err = TryParse(textLf, level, module, out var program);
        if (err is not null || program is null) { doc.Error = err; return doc; }
        doc.Analyze(program);
        return doc;
    }

    // ---- analysis ----

    private void Analyze(Program program)
    {
        _global = new Scope(null, true);
        Visit(program, _global, null);
        TopLevelNames = new HashSet<string>(_global.Names, StringComparer.Ordinal);
        FreeNames = _refs.Where(r => !r.Scope.Resolves(r.Name))
            .OrderBy(r => r.Line)
            .Select(r => new JsNameUse(r.Name, r.Line, r.Function)).ToList();
        _functions.Sort((a, b) => a.DeclLine.CompareTo(b.DeclLine));
    }

    private void Visit(Node? n, Scope s, string? fn)
    {
        if (n is null) return;
        if (n is Statement || n is ObjectProperty) _spans.Add((n.Location.Start.Line, n.Location.End.Line));
        switch (n)
        {
            case Identifier id:
                _refs.Add((id.Name, id.Location.Start.Line, s, fn));
                return;
            case MemberExpression me:
                Visit(me.Object, s, fn);
                if (me.Computed) Visit(me.Property, s, fn);
                return;
            case ObjectProperty op:
                if (op.Computed) Visit(op.Key, s, fn);
                Visit(op.Value, s, fn);
                return;
            case BreakStatement or ContinueStatement or MetaProperty:
                return;
            case LabeledStatement ls:
                Visit(ls.Body, s, fn);
                return;
            case FunctionDeclaration fd:
                if (fd.Id is not null)
                {
                    s.FunctionScope().Names.Add(fd.Id.Name);
                    RegisterFunction(fd, fd.Id.Name, null, fd.Location.Start.Line, fd.Location.Start.Line);
                }
                VisitFunction(fd, s, fn, fd.Id?.Name);
                return;
            case FunctionExpression or ArrowFunctionExpression:
                VisitFunction(n, s, fn, null);
                return;
            case VariableDeclaration vd:
                VisitVarDecl(vd, s, fn);
                return;
            case ClassDeclaration cd:
                if (cd.Id is not null) s.FunctionScope().Names.Add(cd.Id.Name);
                VisitClass(cd, s, fn);
                return;
            case ClassExpression ce:
                VisitClass(ce, s, fn);
                return;
            case CatchClause cc:
            {
                var cs = new Scope(s, false);
                Pattern(cc.Param, cs, true, fn);
                Visit(cc.Body, cs, fn);
                return;
            }
            case AssignmentExpression ae:
                Pattern(ae.Left, s, false, fn);
                if (ae.Left is MemberExpression { Computed: false } lm && IsFunctionNode(ae.Right)
                    && lm.Property is Identifier pid && ChainPath(lm.Object) is { } owner)
                {
                    var line = ae.Location.Start.Line;
                    RegisterFunction(ae.Right, pid.Name, owner, line, line);
                }
                Visit(ae.Right, s, fn);
                return;
            case ForInStatement fi:
                VisitForLeft(fi.Left, s, fn);
                Visit(fi.Right, s, fn); Visit(fi.Body, s, fn);
                return;
            case ForOfStatement fo:
                VisitForLeft(fo.Left, s, fn);
                Visit(fo.Right, s, fn); Visit(fo.Body, s, fn);
                return;
            case ImportDeclaration imp:
                foreach (var spec in imp.Specifiers)
                    foreach (var c in spec.ChildNodes)
                        if (c is Identifier lid && ReferenceEquals(c, LocalOf(spec))) _global.Names.Add(lid.Name);
                return;
            case ExportNamedDeclaration en:
                Visit(en.Declaration, s, fn);
                return;
            case CallExpression call:
                RecordCall(call.Callee, call.Arguments, false, call);
                break;
            case NewExpression nw:
                RecordCall(nw.Callee, nw.Arguments, true, nw);
                break;
        }
        foreach (var c in n.ChildNodes) Visit(c, s, fn);
    }

    private static Node? LocalOf(Node spec) => spec switch
    {
        ImportSpecifier i => i.Local,
        ImportDefaultSpecifier d => d.Local,
        ImportNamespaceSpecifier ns => ns.Local,
        _ => null,
    };

    private static bool IsFunctionNode(Node? n) => n is FunctionExpression or ArrowFunctionExpression;

    private void VisitForLeft(Node left, Scope s, string? fn)
    {
        if (left is VariableDeclaration vd) VisitVarDecl(vd, s, fn);
        else Pattern(left, s, false, fn);
    }

    private void VisitVarDecl(VariableDeclaration vd, Scope s, string? fn)
    {
        _spans.Add((vd.Location.Start.Line, vd.Location.End.Line));
        var first = true;
        foreach (var d in vd.Declarations)
        {
            Pattern(d.Id, s, true, fn);
            if (d.Id is Identifier id && d.Init is { } init)
            {
                var declLine = first ? vd.Location.Start.Line : d.Location.Start.Line;
                if (IsFunctionNode(init)) RegisterFunction(init, id.Name, null, declLine, declLine);
                else if (init is ObjectExpression oe)
                {
                    foreach (var p in oe.Properties)
                    {
                        if (p is ObjectProperty { Computed: false } prop && IsFunctionNode(prop.Value))
                        {
                            var key = prop.Key switch { Identifier ki => ki.Name, Literal { Value: string str } => str, _ => null };
                            if (key is not null) RegisterFunction(prop.Value, key, id.Name, prop.Location.Start.Line, prop.Location.Start.Line);
                        }
                    }
                }
            }
            Visit(d.Init, s, fn);
            first = false;
        }
    }

    private void RegisterFunction(Node fnNode, string name, string? owner, int declLine, int startAnchor)
    {
        var parms = fnNode switch
        {
            FunctionDeclaration f => f.Params,
            FunctionExpression f => f.Params,
            ArrowFunctionExpression f => f.Params,
            _ => default,
        };
        var names = new List<string>();
        foreach (var p in parms) names.Add(p is Identifier pi ? pi.Name : _source.Substring(p.Start, p.End - p.Start));
        _named[fnNode] = (name, owner);
        var start = declLine;
        while (start > 1 && IsCommentLine(_lines[start - 2])) start--;
        _functions.Add(new JsFunction(name, owner, names, start, declLine, fnNode.Location.End.Line, LineText(declLine).Trim()));
    }

    private static bool IsCommentLine(string line)
    {
        var t = line.Trim();
        return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal)
            || t.StartsWith("*", StringComparison.Ordinal) || t.EndsWith("*/", StringComparison.Ordinal);
    }

    private void VisitFunction(Node f, Scope s, string? outerFn, string? declName)
    {
        var fs = new Scope(s, true);
        string? qual = declName;
        if (_named.TryGetValue(f, out var nm)) qual = nm.Owner is null ? nm.Name : $"{nm.Owner}.{nm.Name}";
        var fn = qual ?? outerFn;
        NodeList<Node> parms;
        Node? body;
        switch (f)
        {
            case FunctionDeclaration d: parms = d.Params; body = d.Body; fs.Names.Add("arguments"); break;
            case FunctionExpression e:
                parms = e.Params; body = e.Body; fs.Names.Add("arguments");
                if (e.Id is not null) fs.Names.Add(e.Id.Name);
                break;
            case ArrowFunctionExpression a: parms = a.Params; body = a.Body; break;
            default: return;
        }
        foreach (var p in parms) Pattern(p, fs, true, fn);
        Visit(body, fs, fn);
    }

    private void VisitClass(Node c, Scope s, string? fn)
    {
        var cs = new Scope(s, false);
        Node? superClass; ClassBody body;
        if (c is ClassDeclaration d) { superClass = d.SuperClass; body = d.Body; }
        else { var e = (ClassExpression)c; superClass = e.SuperClass; body = e.Body; if (e.Id is not null) cs.Names.Add(e.Id.Name); }
        Visit(superClass, cs, fn);
        foreach (var m in body.Body)
        {
            switch (m)
            {
                case MethodDefinition md:
                    if (md.Computed) Visit(md.Key, cs, fn);
                    Visit(md.Value, cs, fn);
                    break;
                case PropertyDefinition pd:
                    if (pd.Computed) Visit(pd.Key, cs, fn);
                    Visit(pd.Value, cs, fn);
                    break;
                default:
                    foreach (var k in m.ChildNodes) Visit(k, cs, fn);
                    break;
            }
        }
    }

    /// <summary>Walks a binding or assignment pattern; identifiers are declared (declare) or counted as references.</summary>
    private void Pattern(Node? p, Scope s, bool declare, string? fn)
    {
        switch (p)
        {
            case null: return;
            case Identifier id:
                if (declare) s.FunctionScope().Names.Add(id.Name);
                else _refs.Add((id.Name, id.Location.Start.Line, s, fn));
                return;
            case ObjectPattern op:
                foreach (var prop in op.Properties)
                {
                    if (prop is ObjectProperty q)
                    {
                        if (q.Computed) Visit(q.Key, s, fn);
                        Pattern(q.Value, s, declare, fn);
                    }
                    else Pattern(prop, s, declare, fn);
                }
                return;
            case ArrayPattern ap:
                foreach (var el in ap.Elements) Pattern(el, s, declare, fn);
                return;
            case AssignmentPattern asp:
                Pattern(asp.Left, s, declare, fn);
                Visit(asp.Right, s, fn);
                return;
            case RestElement re:
                Pattern(re.Argument, s, declare, fn);
                return;
            default:
                Visit(p, s, fn);
                return;
        }
    }

    // ---- calls ----

    private static string? ChainPath(Node? n) => n switch
    {
        Identifier i => i.Name,
        ThisExpression => "this",
        MemberExpression { Computed: false, Property: Identifier p } m => ChainPath(m.Object) is { } o ? o + "." + p.Name : null,
        MemberExpression { Computed: true, Property: Literal { Value: string str } } m => ChainPath(m.Object) is { } o ? o + "." + str : null,
        _ => null,
    };

    private void RecordCall(Node callee, IReadOnlyList<Node> args, bool isNew, Node call)
    {
        var path = ChainPath(callee);
        if (path is null && callee is MemberExpression { Computed: false, Property: Identifier mp }) path = "." + mp.Name;
        path ??= "";
        var kind = args.Count == 0 ? JsArgKind.None : ArgKind(args[0]);
        var text = Regex.Replace(_source.Substring(call.Start, call.End - call.Start), @"\s+", " ");
        _calls.Add(new JsCall(path, isNew, call.Location.Start.Line, text, kind));
    }

    private static JsArgKind ArgKind(Node a)
    {
        if (a is Literal { Value: string }) return JsArgKind.StringLiteral;
        if (a is TemplateLiteral { Expressions.Count: 0 }) return JsArgKind.StringLiteral;
        if (a is BinaryExpression { Operator: Operator.Addition } && JsAst.Descendants(a).Any(x => x is Literal { Value: string } or TemplateLiteral))
            return JsArgKind.StringConcat;
        return JsArgKind.Other;
    }
}
