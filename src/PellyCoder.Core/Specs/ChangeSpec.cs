namespace PellyCoder.Core.Specs;

public enum SpecStatus { Draft, InProgress, Done }

public enum TaskKind { CSharp, Sql }

public enum SpecIssueSeverity { Error, Warning }

/// <summary>A problem found while parsing a change spec. <paramref name="Line"/> is 1-based.</summary>
public sealed record SpecIssue(int Line, SpecIssueSeverity Severity, string Code, string Message);

public sealed record AcceptanceCriterion(string Id, string Text, string? Test);

public sealed record SpecTask(
    string Id,
    TaskKind Kind,
    string Path,
    string? Symbol,
    string Request,
    IReadOnlyList<string> AcRefs,
    string? Group,
    bool Done,
    string? RunId,
    string? Failure,
    int Line);

public sealed record ChangeSpec(
    string? Id,
    SpecStatus Status,
    string? Title,
    IReadOnlyList<AcceptanceCriterion> Criteria,
    IReadOnlyList<SpecTask> Tasks,
    IReadOnlyList<SpecIssue> Issues);
