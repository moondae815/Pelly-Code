namespace PellyCoder.Core.Models;

public sealed record Diagnostic(string File, int Line, int Column, string Severity, string Code, string Message);

public sealed record VerifyOutcome(
    bool Passed,
    IReadOnlyList<Diagnostic> NewErrors,
    IReadOnlyList<Diagnostic> NewWarnings,
    IReadOnlyList<string> Notices);
