namespace PullPatrol.Api.GitHub;

// Minimal shapes of the GitHub REST/webhook payloads. Property names map to snake_case JSON
// via GitHubJson.Options.

public sealed record PullRequestEvent(string Action, PullRequest PullRequest, Repository Repository);

public sealed record PullRequest(int Number, string Title, string? Body, bool Draft, string HtmlUrl, GitRef Head);

public sealed record GitRef(string Sha, string Ref);

public sealed record Repository(string Name, string FullName, Account Owner);

public sealed record Account(string Login);

public sealed record PullRequestFile(string Filename, string Status, int Additions, int Deletions, string? Patch);

public sealed record CreateReviewRequest(
    string CommitId,
    string Body,
    string Event,
    IReadOnlyList<ReviewComment> Comments);

/// <param name="Side">"RIGHT" = the new version of the file.</param>
public sealed record ReviewComment(string Path, int Line, string Body, string Side = "RIGHT");

public sealed record CommitStatusRequest(string State, string Description, string Context, string? TargetUrl = null);

public static class CommitState
{
    public const string Pending = "pending";
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Error = "error";
}
