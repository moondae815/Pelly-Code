namespace PellyCoder.Core.Models;

public enum EditKind { CSharpMethod, SqlProcedure }

/// <summary>1-based, inclusive line span.</summary>
public sealed record TextSpanInfo(int StartLine, int EndLine);

public sealed record EditTarget(EditKind Kind, string FilePath, string SymbolDisplay, TextSpanInfo MemberSpan, string? ProjectPath);

public sealed record EditableRegion(TextSpanInfo Span, string Reason);

public sealed record SlicePart(string Kind, string Text, int Tokens, int Priority);

public sealed record Slice(IReadOnlyList<SlicePart> Included, IReadOnlyList<SlicePart> Omitted, string LanguageHint);

public sealed record Feedback(string Text, int Tokens);
