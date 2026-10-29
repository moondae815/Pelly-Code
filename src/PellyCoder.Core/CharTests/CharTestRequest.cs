namespace PellyCoder.Core.CharTests;

/// <summary>Input of <c>pelly test</c>; data only.</summary>
public sealed record CharTestRequest(
    string FilePath, string? ClassName, string? MethodName, int? Line, string? Hint, int Count,
    string? TestProject, bool Yes, bool DryRun, int? MaxRetries, bool Verbose);

public static class CharTestLimits
{
    public const int MinCount = 1, MaxCount = 5, DefaultCount = 3;
}
