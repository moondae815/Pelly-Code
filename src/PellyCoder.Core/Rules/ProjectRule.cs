namespace PellyCoder.Core.Rules;

/// <summary>One rule line of the Pelly rules section. <paramref name="Line"/> is 1-based in the file; <paramref name="Forbid"/> is the raw regex.</summary>
public sealed record ProjectRule(int Line, IReadOnlyList<string> Tags, string Text, string? Forbid)
{
    public bool IsAlways => Tags is ["*"];
}

/// <summary>A problem in the rules file. <paramref name="Line"/> is 0 for file-level errors.</summary>
public sealed record RulesError(int Line, string Message);

/// <summary>Line range of the section, heading included; <paramref name="EndLine"/> is the last line (1-based, inclusive).</summary>
public sealed record RulesSection(int StartLine, int EndLine);

public sealed record RulesLoadResult(string? Path, IReadOnlyList<ProjectRule> Rules, IReadOnlyList<RulesError> Errors);
