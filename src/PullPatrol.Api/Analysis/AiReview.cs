using System.Text;
using PullPatrol.Api.Queue;

namespace PullPatrol.Api.Analysis;

/// <param name="ReviewedFiles">Files the AI actually saw; the rest only got static checks.</param>
public sealed record AiReviewResult(string? Summary, IReadOnlyList<Finding> Findings, IReadOnlyList<string> ReviewedFiles);

public interface IAiReviewer
{
    bool IsEnabled { get; }

    /// <summary>Display name used in review comments, e.g. "Llama 3.3 70B via Groq".</summary>
    string DisplayName { get; }

    /// <exception cref="AiUnavailableException">The AI can't review this PR right now (budget or rate limit).</exception>
    Task<AiReviewResult> ReviewAsync(ReviewJob job, IReadOnlyList<ChangedFile> files, CancellationToken ct);
}

/// <summary>
/// Expected, non-error reasons to skip the AI review (rate limited, PR too large for the token budget).
/// The review then continues with static rules only.
/// </summary>
public sealed class AiUnavailableException(string reason) : Exception(reason);

public static class ReviewPrompt
{
    private const int MaxDescriptionChars = 2_000;

    /// <summary>
    /// Builds the user message. Files are added in order until the character budget is used up;
    /// the ones that fit are returned so the review can say what the AI covered.
    /// </summary>
    public static (string Prompt, IReadOnlyList<string> Included) Build(ReviewJob job, IReadOnlyList<ChangedFile> files, int maxDiffChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Repository: {job.Owner}/{job.Repo}");
        sb.AppendLine($"Pull request #{job.Number}: {job.Title}");
        sb.AppendLine();
        sb.AppendLine("<description>");
        var description = job.Body ?? "(no description)";
        sb.AppendLine(description.Length > MaxDescriptionChars ? description[..MaxDescriptionChars] + "\n[description truncated]" : description);
        sb.AppendLine("</description>");
        sb.AppendLine();

        var included = new List<string>();
        int used = 0;

        foreach (var file in files)
        {
            if (file.Diff.Lines.Count == 0)
                continue;

            var annotated = file.Diff.ToAnnotatedText();
            if (used + annotated.Length > maxDiffChars)
                continue;

            used += annotated.Length;
            included.Add(file.Path);
            sb.AppendLine($"<file path=\"{file.Path}\" status=\"{file.Status}\">");
            sb.Append(annotated);
            sb.AppendLine("</file>");
            sb.AppendLine();
        }

        return (sb.ToString(), included);
    }
}
