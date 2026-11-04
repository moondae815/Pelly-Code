using System.Text;

namespace PellyCoder.Core.Asp;

public enum VbTokenKind { Identifier, Keyword, String, Number, Date, Operator, Dot, Comma, OpenParen, CloseParen }

public sealed record VbToken(VbTokenKind Kind, string Text, int Line, int Column);

/// <summary>One VBScript statement. <see cref="Text"/> is the source text without comments, continuation lines joined with a space.</summary>
public sealed record VbStatement(int StartLine, int EndLine, IReadOnlyList<VbToken> Tokens, string Text);

/// <summary>
/// Hand-written VBScript tokenizer that splits code chunks into statements: strings with <c>""</c> escapes,
/// <c>'</c> and <c>Rem</c> comments, <c> _</c> line continuation, <c>:</c> separators and single-line
/// <c>If … Then …</c> (kept as one statement, colons included).
/// </summary>
public static class VbLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "And", "As", "ByRef", "ByVal", "Call", "Case", "Class", "Const", "Default", "Dim", "Do", "Each", "Else",
        "ElseIf", "Empty", "End", "Eqv", "Erase", "Error", "Exit", "Explicit", "False", "For", "Function", "Get",
        "GoTo", "If", "Imp", "In", "Is", "Let", "Loop", "Me", "Mod", "New", "Next", "Not", "Nothing", "Null", "On",
        "Option", "Optional", "Or", "Preserve", "Private", "Property", "Public", "ReDim", "Rem", "Resume", "Select",
        "Set", "Step", "Stop", "Sub", "Then", "To", "True", "Until", "Wend", "While", "With", "Xor",
    };

    /// <summary>Splits code chunks (in page order) into statements. A chunk boundary always ends a statement.</summary>
    public static IReadOnlyList<VbStatement> Statements(
        IReadOnlyList<(int Line, int Column, string Text)> codeChunks, List<AspProblem> problems)
    {
        var result = new List<VbStatement>();
        foreach (var chunk in codeChunks)
            new ChunkLexer(chunk.Line, chunk.Column, chunk.Text, problems, result).Run();
        return result;
    }

    private sealed class ChunkLexer(int line, int column, string text, List<AspProblem> problems, List<VbStatement> output)
    {
        private int _pos;
        private int _line = line;
        private int _col = column;
        private readonly List<VbToken> _tokens = [];
        private readonly StringBuilder _stmtText = new();
        private int _pieceStart = -1;
        private int _pieceEnd = -1;

        public void Run()
        {
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (c == '\n')
                {
                    EndStatement();
                    Advance(1);
                }
                else if (c is ' ' or '\t' or '\r')
                    Advance(1);
                else if (c == '\'')
                    SkipToLineEnd();
                else if (c == ':')
                {
                    if (InSingleLineIf())
                        Emit(VbTokenKind.Operator, 1);
                    else
                    {
                        EndStatement();
                        Advance(1);
                    }
                }
                else if (c == '_' && IsContinuation())
                    Continue();
                else if (c == '"')
                    LexString();
                else if (char.IsLetter(c))
                    LexWord();
                else if (c == '[')
                    LexBracketName();
                else if (char.IsDigit(c) || (c == '.' && NextIsDigit() && !AfterOperand()))
                    LexNumber();
                else if (c == '&' && IsRadixLiteral())
                    LexRadix();
                else if (c == '#' && DateLength() is int dateLength)
                    Emit(VbTokenKind.Date, dateLength);
                else if (c == '.')
                    Emit(VbTokenKind.Dot, 1);
                else if (c == ',')
                    Emit(VbTokenKind.Comma, 1);
                else if (c == '(')
                    Emit(VbTokenKind.OpenParen, 1);
                else if (c == ')')
                    Emit(VbTokenKind.CloseParen, 1);
                else if (IsTwoCharOperator())
                    Emit(VbTokenKind.Operator, 2);
                else
                    Emit(VbTokenKind.Operator, 1);
            }
            EndStatement();
        }

        private void Advance(int count)
        {
            for (var k = 0; k < count && _pos < text.Length; k++)
            {
                if (text[_pos] == '\n')
                {
                    _line++;
                    _col = 1;
                }
                else
                    _col++;
                _pos++;
            }
        }

        private void Emit(VbTokenKind kind, int length)
        {
            if (_pieceStart < 0)
                _pieceStart = _pos;
            _tokens.Add(new VbToken(kind, text.Substring(_pos, length), _line, _col));
            Advance(length);
            _pieceEnd = _pos;
        }

        private void FlushPiece()
        {
            if (_pieceStart < 0)
                return;
            _stmtText.Append(text, _pieceStart, _pieceEnd - _pieceStart);
            _pieceStart = -1;
        }

        private void EndStatement()
        {
            FlushPiece();
            if (_tokens.Count > 0)
                output.Add(new VbStatement(_tokens[0].Line, _tokens[^1].Line, _tokens.ToArray(), _stmtText.ToString()));
            _tokens.Clear();
            _stmtText.Clear();
        }

        private void SkipToLineEnd()
        {
            while (_pos < text.Length && text[_pos] != '\n')
                Advance(1);
        }

        private bool InSingleLineIf()
        {
            if (_tokens.Count < 3 || !Is(_tokens[0], "If"))
                return false;
            var then = _tokens.FindIndex(t => Is(t, "Then"));
            return then >= 0 && then < _tokens.Count - 1;
        }

        private bool IsContinuation()
        {
            var k = _pos + 1;
            while (k < text.Length && text[k] is ' ' or '\t' or '\r')
                k++;
            return k < text.Length && text[k] == '\n';
        }

        private void Continue()
        {
            FlushPiece();
            if (_stmtText.Length > 0)
                _stmtText.Append(' ');
            while (text[_pos] != '\n')
                Advance(1);
            Advance(1);
        }

        private void LexString()
        {
            var k = _pos + 1;
            while (k < text.Length && text[k] != '\n')
            {
                if (text[k] == '"')
                {
                    if (k + 1 < text.Length && text[k + 1] == '"')
                    {
                        k += 2;
                        continue;
                    }
                    Emit(VbTokenKind.String, k + 1 - _pos);
                    return;
                }
                k++;
            }
            problems.Add(new AspProblem(_line, _col, "ASP003", "문자열이 닫히지 않았습니다"));
            Emit(VbTokenKind.String, k - _pos);
        }

        private void LexWord()
        {
            var k = _pos + 1;
            while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_'))
                k++;
            var word = text.Substring(_pos, k - _pos);
            if (_tokens.Count == 0 && word.Equals("Rem", StringComparison.OrdinalIgnoreCase))
            {
                SkipToLineEnd();
                return;
            }
            var afterDot = _tokens.Count > 0 && _tokens[^1].Kind == VbTokenKind.Dot;
            Emit(!afterDot && Keywords.Contains(word) ? VbTokenKind.Keyword : VbTokenKind.Identifier, k - _pos);
        }

        private void LexBracketName()
        {
            var close = text.IndexOf(']', _pos);
            var newline = text.IndexOf('\n', _pos);
            if (close < 0 || (newline >= 0 && newline < close))
                Emit(VbTokenKind.Operator, 1);
            else
                Emit(VbTokenKind.Identifier, close + 1 - _pos);
        }

        private bool IsTwoCharOperator()
        {
            if (_pos + 1 >= text.Length)
                return false;
            var pair = text.AsSpan(_pos, 2);
            return pair is "<>" or "<=" or ">=";
        }

        private bool NextIsDigit() => _pos + 1 < text.Length && char.IsDigit(text[_pos + 1]);

        private bool AfterOperand() =>
            _tokens.Count > 0 && _tokens[^1].Kind is VbTokenKind.Identifier or VbTokenKind.CloseParen;

        private void LexNumber()
        {
            var k = _pos;
            while (k < text.Length && char.IsDigit(text[k]))
                k++;
            if (k < text.Length && text[k] == '.')
            {
                k++;
                while (k < text.Length && char.IsDigit(text[k]))
                    k++;
            }
            if (k < text.Length && text[k] is 'e' or 'E')
            {
                var e = k + 1;
                if (e < text.Length && text[e] is '+' or '-')
                    e++;
                if (e < text.Length && char.IsDigit(text[e]))
                {
                    k = e;
                    while (k < text.Length && char.IsDigit(text[k]))
                        k++;
                }
            }
            Emit(VbTokenKind.Number, k - _pos);
        }

        private bool IsRadixLiteral() =>
            _pos + 2 < text.Length && text[_pos + 1] is 'h' or 'H' or 'o' or 'O' && Uri.IsHexDigit(text[_pos + 2]);

        private void LexRadix()
        {
            var k = _pos + 2;
            while (k < text.Length && Uri.IsHexDigit(text[k]))
                k++;
            if (k < text.Length && text[k] == '&')
                k++;
            Emit(VbTokenKind.Number, k - _pos);
        }

        private int? DateLength()
        {
            for (var k = _pos + 1; k < text.Length && text[k] != '\n'; k++)
                if (text[k] == '#')
                    return k > _pos + 1 ? k + 1 - _pos : null;
            return null;
        }

        private static bool Is(VbToken token, string keyword) =>
            token.Kind == VbTokenKind.Keyword && token.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
