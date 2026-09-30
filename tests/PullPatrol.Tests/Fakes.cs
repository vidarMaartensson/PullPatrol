using System.Collections.Concurrent;
using System.Net;
using PullPatrol.Api.Analysis;
using PullPatrol.Api.GitHub;
using PullPatrol.Api.Queue;

namespace PullPatrol.Tests;

public sealed class FakeGitHubClient : IGitHubClient
{
    public List<PullRequestFile> Files { get; } = [];
    public ConcurrentQueue<CreateReviewRequest> Reviews { get; } = new();
    public ConcurrentQueue<CommitStatusRequest> Statuses { get; } = new();
    public bool RejectInlineComments { get; set; }

    public Task<PullRequest> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct) =>
        Task.FromResult(new PullRequest(number, "Title", "Body", false, $"https://github.com/{owner}/{repo}/pull/{number}", new GitRef("abc123", "feature")));

    public Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(string owner, string repo, int number, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PullRequestFile>>(Files);

    public Task CreateReviewAsync(string owner, string repo, int number, CreateReviewRequest review, CancellationToken ct)
    {
        if (RejectInlineComments && review.Comments.Count > 0)
            throw new GitHubApiException(HttpStatusCode.UnprocessableEntity, "Line could not be resolved");

        Reviews.Enqueue(review);
        return Task.CompletedTask;
    }

    public Task SetCommitStatusAsync(string owner, string repo, string sha, CommitStatusRequest status, CancellationToken ct)
    {
        Statuses.Enqueue(status);
        return Task.CompletedTask;
    }
}

public sealed class FakeAiReviewer(params Finding[] findings) : IAiReviewer
{
    public bool IsEnabled { get; set; } = true;
    public string DisplayName => "Fake LLM";
    public bool Throw { get; set; }
    public bool Unavailable { get; set; }

    public Task<AiReviewResult> ReviewAsync(ReviewJob job, IReadOnlyList<ChangedFile> files, CancellationToken ct) =>
        Throw ? throw new InvalidOperationException("boom")
        : Unavailable ? throw new AiUnavailableException("rate limited")
        : Task.FromResult(new AiReviewResult("Looks mostly fine.", findings, files.Select(f => f.Path).ToList()));
}

public sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
