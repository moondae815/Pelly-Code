namespace PellyCode.McpServer.Git;

public enum GitChangeKind { Modified, Added, Deleted, Renamed }

/// <summary>A changed path relative to the repository root, separated by '/'. <paramref name="OldPath"/> is set for renames.</summary>
public sealed record GitChange(string Path, GitChangeKind Kind, string? OldPath);
