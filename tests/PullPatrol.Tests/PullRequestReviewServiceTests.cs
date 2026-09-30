using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PullPatrol.Api.Analysis;
using PullPatrol.Api.Configuration;
using PullPatrol.Api.GitHub;
using PullPatrol.Api.Queue;
using PullPatrol.Api.Reviews;

namespace PullPatrol.Tests;

public class PullRequestReviewServiceTests
{
    private static readonly ReviewJob Job = new("octo", "repo", 7, "abc123", "Add feature", null, "https://github.com/octo/repo/pull/7", "test");

    private readonly FakeGitHubClient _github = new();
    private readonly ReviewHistory _history = new();

    private PullRequestReviewService CreateService(IAiReviewer ai, ReviewOptions? options = null) =>
        new(_github, new StaticRuleAnalyzer(), ai, _history, Options.Create(options ?? new ReviewOptions()),
            NullLogger<PullRequestReviewService>.Instance);

    public PullRequestReviewServiceTests()
    {
        _github.Files.Add(new PullRequestFile("src/Service.cs", "modified", 2, 0,
            "@@ -1,1 +1,3 @@\n using System;\n+var user = repo.GetAsync(id).Result;\n+var ok = true;"));
    }

    [Fact]
    public async Task PostsInlineCommentsForRuleAndAiFindings_AndFailsStatusOnHighSeverity()
    {
        var aiFinding = new Finding("src/Service.cs", 3, Severity.High, "correctness", "Unused variable", "ok is never read", null, Finding.AiSource);

        await CreateService(new FakeAiReviewer(aiFinding)).ReviewAsync(Job, CancellationToken.None);

        var review = Assert.Single(_github.Reviews);
        Assert.Equal("abc123", review.CommitId);
        Assert.Equal("COMMENT", review.Event);
        Assert.Equal(2, review.Comments.Count);
        Assert.Contains(review.Comments, c => c.Line == 2 && c.Body.Contains("PP101"));
        Assert.Contains(review.Comments, c => c.Line == 3 && c.Body.Contains("Unused variable"));
        Assert.Contains("Looks mostly fine.", review.Body);

        Assert.Equal([CommitState.Pending, CommitState.Failure], _github.Statuses.Select(s => s.State));
        Assert.Equal("failure", Assert.Single(_history.Recent()).Outcome);
    }

    [Fact]
    public async Task FindingsOutsideTheDiff_GoToTheReviewBody()
    {
        var outside = new Finding("src/Service.cs", 99, Severity.Low, "maintainability", "Far away", "Not in the diff", null, Finding.AiSource);

        await CreateService(new FakeAiReviewer(outside)).ReviewAsync(Job, CancellationToken.None);

        var review = Assert.Single(_github.Reviews);
        Assert.DoesNotContain(review.Comments, c => c.Line == 99);
        Assert.Contains("`src/Service.cs:99`", review.Body);
    }

    [Fact]
    public async Task WhenGitHubRejectsInlineComments_FallsBackToSummaryOnlyReview()
    {
        _github.RejectInlineComments = true;

        await CreateService(new FakeAiReviewer()).ReviewAsync(Job, CancellationToken.None);

        var review = Assert.Single(_github.Reviews);
        Assert.Empty(review.Comments);
        Assert.Contains("Blocking on async code", review.Body);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WhenAiIsUnavailableOrFails_SeamlesslyPostsStaticFindings(bool unavailable, bool throws)
    {
        await CreateService(new FakeAiReviewer { Unavailable = unavailable, Throw = throws }).ReviewAsync(Job, CancellationToken.None);

        var review = Assert.Single(_github.Reviews);
        Assert.Single(review.Comments);
        Assert.Contains("PullPatrol · static rules.", review.Body);
        Assert.DoesNotContain("Fake LLM", review.Body);
        Assert.False(Assert.Single(_history.Recent()).AiReviewUsed);
    }

    [Fact]
    public async Task WhenAiWorks_FooterNamesTheModel()
    {
        await CreateService(new FakeAiReviewer()).ReviewAsync(Job, CancellationToken.None);

        Assert.Contains("static rules + Fake LLM.", Assert.Single(_github.Reviews).Body);
    }

    [Fact]
    public async Task MediumFindingsBelowThreshold_GiveSuccessStatus()
    {
        await CreateService(new FakeAiReviewer()).ReviewAsync(Job, CancellationToken.None);

        Assert.Equal(CommitState.Success, _github.Statuses.Last().State);
    }

    [Fact]
    public async Task DuplicateFindingsOnTheSameLine_AreMerged()
    {
        var aiDuplicate = new Finding("src/Service.cs", 2, Severity.High, "performance", "Sync over async",
            "This blocks a thread pool thread while waiting for the database.", null, Finding.AiSource);

        await CreateService(new FakeAiReviewer(aiDuplicate)).ReviewAsync(Job, CancellationToken.None);

        var comment = Assert.Single(Assert.Single(_github.Reviews).Comments);
        Assert.Contains("Sync over async", comment.Body);
    }

    [Theory]
    [InlineData("package-lock.json", true)]
    [InlineData("web/dist/app.min.js", true)]
    [InlineData("src/Data/Migrations/20260101_Init.cs", true)]
    [InlineData("Migrations/Init.cs", true)]
    [InlineData("src/MigrationsHelper.cs", false)]
    [InlineData("src/Service.cs", false)]
    public void IsIgnored_MatchesDefaultPatterns(string path, bool expected)
    {
        Assert.Equal(expected, PullRequestReviewService.IsIgnored(path, new ReviewOptions().IgnoredPaths));
    }
}
