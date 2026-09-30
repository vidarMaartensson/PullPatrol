using System.Net;
using Microsoft.Extensions.Options;
using PullPatrol.Api.Analysis;
using PullPatrol.Api.Configuration;
using PullPatrol.Api.GitHub;
using PullPatrol.Api.Queue;

namespace PullPatrol.Api.Reviews;

/// <summary>
/// Runs one review end to end: fetch the diff, analyze it (rules + AI), post a PR review with
/// inline comments, and report a commit status that branch protection can gate merges on.
/// </summary>
public sealed class PullRequestReviewService(
    IGitHubClient github,
    StaticRuleAnalyzer rules,
    IAiReviewer ai,
    ReviewHistory history,
    IOptions<ReviewOptions> options,
    ILogger<PullRequestReviewService> logger)
{
    public const string StatusContext = "PullPatrol";

    private readonly ReviewOptions _options = options.Value;

    public async Task ReviewAsync(ReviewJob job, CancellationToken ct)
    {
        await TrySetStatusAsync(job, CommitState.Pending, "Reviewing pull request...", ct);

        try
        {
            var record = await RunAsync(job, ct);
            history.Add(record);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Review of {Owner}/{Repo}#{Number} failed", job.Owner, job.Repo, job.Number);
            await TrySetStatusAsync(job, CommitState.Error, "PullPatrol could not complete the review.", ct);
            history.Add(new ReviewRecord($"{job.Owner}/{job.Repo}", job.Number, job.HeadSha, job.Trigger,
                DateTimeOffset.UtcNow, "error", 0, new Dictionary<Severity, int>(), false, ex.Message));
        }
    }

    private async Task<ReviewRecord> RunAsync(ReviewJob job, CancellationToken ct)
    {
        var rawFiles = await github.GetPullRequestFilesAsync(job.Owner, job.Repo, job.Number, ct);

        var files = rawFiles
            .Where(f => f.Status != "removed" && !IsIgnored(f.Filename, _options.IgnoredPaths))
            .Select(f => new ChangedFile(f.Filename, f.Status, DiffParser.Parse(f.Patch)))
            .ToList();

        var findings = new List<Finding>(rules.Analyze(files));

        string? aiSummary = null;
        IReadOnlyList<string> aiReviewed = [];

        if (ai.IsEnabled && files.Count > 0)
        {
            try
            {
                var aiResult = await ai.ReviewAsync(job, files, ct);
                aiSummary = aiResult.Summary;
                aiReviewed = aiResult.ReviewedFiles;
                findings.AddRange(aiResult.Findings);
            }
            catch (AiUnavailableException ex)
            {
                // Expected on a free tier (rate limit / PR too large): quietly continue with static rules.
                logger.LogInformation("Skipping AI review of {Owner}/{Repo}#{Number}: {Reason}", job.Owner, job.Repo, job.Number, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "AI review failed for {Owner}/{Repo}#{Number}; continuing with static rules only",
                    job.Owner, job.Repo, job.Number);
            }
        }

        var analyzedBy = DescribeAnalysis(ai, aiReviewed.Count, files.Count(f => f.Diff.Lines.Count > 0));

        // Rules and the AI often flag the same problem; keep one finding per line and category,
        // preferring the highest severity and then the AI's more contextual explanation.
        var ordered = findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Source == Finding.AiSource)
            .DistinctBy(f => (f.Path, f.Line, f.Category))
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ToList();

        var (inline, notInline) = SplitInline(ordered, files, _options.MaxInlineComments);

        await PostReviewAsync(job, aiSummary, ordered, inline, notInline, analyzedBy, ct);

        var worst = ordered.Count > 0 ? ordered.Max(f => f.Severity) : (Severity?)null;
        var failed = worst is { } w && w >= _options.FailOnSeverity;

        await TrySetStatusAsync(job,
            failed ? CommitState.Failure : CommitState.Success,
            ordered.Count == 0 ? "No issues found" : $"{ordered.Count} finding(s), highest severity: {worst}",
            ct);

        logger.LogInformation("Reviewed {Owner}/{Repo}#{Number}: {Count} findings ({Inline} inline)",
            job.Owner, job.Repo, job.Number, ordered.Count, inline.Count);

        return new ReviewRecord(
            $"{job.Owner}/{job.Repo}", job.Number, job.HeadSha, job.Trigger, DateTimeOffset.UtcNow,
            failed ? "failure" : "success",
            ordered.Count,
            ordered.GroupBy(f => f.Severity).ToDictionary(g => g.Key, g => g.Count()),
            AiReviewUsed: aiReviewed.Count > 0,
            Error: null);
    }

    /// <summary>
    /// Findings can only be posted inline when their line is part of the diff; everything else
    /// (and anything past the inline cap) goes into the review body.
    /// </summary>
    internal static (List<Finding> Inline, List<Finding> NotInline) SplitInline(
        IReadOnlyList<Finding> findings, IReadOnlyList<ChangedFile> files, int maxInline)
    {
        var diffs = files.ToDictionary(f => f.Path, f => f.Diff);
        var inline = new List<Finding>();
        var notInline = new List<Finding>();

        foreach (var finding in findings)
        {
            var commentable = finding.Line is { } line
                && diffs.TryGetValue(finding.Path, out var diff)
                && diff.IsCommentable(line);

            if (commentable && inline.Count < maxInline)
                inline.Add(finding);
            else
                notInline.Add(finding);
        }

        return (inline, notInline);
    }

    private async Task PostReviewAsync(
        ReviewJob job, string? aiSummary, List<Finding> all, List<Finding> inline, List<Finding> notInline,
        string analyzedBy, CancellationToken ct)
    {
        var comments = inline
            .Select(f => new ReviewComment(f.Path, f.Line!.Value, ReviewCommentFormatter.FormatInline(f, ai.DisplayName)))
            .ToList();

        var review = new CreateReviewRequest(
            job.HeadSha,
            ReviewCommentFormatter.FormatSummary(aiSummary, all, notInline, analyzedBy),
            Event: "COMMENT",
            comments);

        try
        {
            await github.CreateReviewAsync(job.Owner, job.Repo, job.Number, review, ct);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.UnprocessableEntity && comments.Count > 0)
        {
            // GitHub rejects the whole review if any comment points outside the diff (e.g. the PR was
            // force-pushed while we were reviewing). Fall back to one review with everything in the body.
            logger.LogWarning(ex, "Inline comments rejected for {Owner}/{Repo}#{Number}; posting summary only",
                job.Owner, job.Repo, job.Number);

            var fallback = review with
            {
                Body = ReviewCommentFormatter.FormatSummary(aiSummary, all, all, analyzedBy),
                Comments = [],
            };
            await github.CreateReviewAsync(job.Owner, job.Repo, job.Number, fallback, ct);
        }
    }

    /// <summary>Footer text saying what analyzed the PR, e.g. "static rules + Llama 3.3 70B via Groq (3 of 5 files)".</summary>
    internal static string DescribeAnalysis(IAiReviewer ai, int aiFiles, int totalFiles) =>
        aiFiles == 0 ? "static rules"
        : aiFiles < totalFiles ? $"static rules + {ai.DisplayName} ({aiFiles} of {totalFiles} files)"
        : $"static rules + {ai.DisplayName}";

    private async Task TrySetStatusAsync(ReviewJob job, string state, string description, CancellationToken ct)
    {
        if (!_options.PostCommitStatus)
            return;

        try
        {
            await github.SetCommitStatusAsync(job.Owner, job.Repo, job.HeadSha,
                new CommitStatusRequest(state, description, StatusContext, job.HtmlUrl), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing status is not worth failing the review over.
            logger.LogWarning(ex, "Could not set commit status '{State}' on {Sha}", state, job.HeadSha);
        }
    }

    /// <summary>Patterns starting with "/" match a directory anywhere in the path; others match the end of the path.</summary>
    internal static bool IsIgnored(string path, IEnumerable<string> patterns) =>
        patterns.Any(pattern => pattern.StartsWith('/')
            ? ("/" + path).Contains(pattern, StringComparison.OrdinalIgnoreCase)
            : path.EndsWith(pattern, StringComparison.OrdinalIgnoreCase));
}
