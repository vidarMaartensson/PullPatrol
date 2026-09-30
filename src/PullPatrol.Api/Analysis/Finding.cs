namespace PullPatrol.Api.Analysis;

public enum Severity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

/// <param name="Path">Repository-relative file path.</param>
/// <param name="Line">Line number in the new version of the file, or null for file/PR-level findings.</param>
/// <param name="Source">Rule id (e.g. "PP001") or <see cref="AiSource"/> for AI findings.</param>
public sealed record Finding(
    string Path,
    int? Line,
    Severity Severity,
    string Category,
    string Title,
    string Message,
    string? Suggestion,
    string Source)
{
    public const string AiSource = "ai";
}

public sealed record ChangedFile(string Path, string Status, ParsedPatch Diff);
