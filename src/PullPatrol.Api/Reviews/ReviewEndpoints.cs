using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PullPatrol.Api.Configuration;
using PullPatrol.Api.GitHub;
using PullPatrol.Api.Queue;

namespace PullPatrol.Api.Reviews;

public sealed record ManualReviewRequest(string Owner, string Repo, int Number);

public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reviews").WithTags("Reviews");

        group.MapGet("/", (HttpRequest request, ReviewHistory history, IOptions<ReviewOptions> options) =>
                Authorize(request, options.Value) ?? Results.Ok(history.Recent()))
            .WithSummary("Lists the most recent reviews processed by this instance. Requires the X-Api-Key header.");

        group.MapPost("/", TriggerAsync)
            .WithSummary("Queues a review of an existing pull request. Requires the X-Api-Key header.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> TriggerAsync(
        ManualReviewRequest body,
        HttpRequest request,
        IGitHubClient github,
        ReviewQueue queue,
        IOptions<ReviewOptions> options,
        CancellationToken ct)
    {
        if (Authorize(request, options.Value) is { } denied)
            return denied;

        PullRequest pr;
        try
        {
            pr = await github.GetPullRequestAsync(body.Owner, body.Repo, body.Number, ct);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Results.NotFound(new { message = "Pull request not found (or the token has no access to it)" });
        }

        var job = new ReviewJob(body.Owner, body.Repo, pr.Number, pr.Head.Sha, pr.Title, pr.Body, pr.HtmlUrl, Trigger: "manual");

        return queue.TryEnqueue(job)
            ? Results.Accepted(value: new { message = "Review queued", body.Owner, body.Repo, body.Number })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Admin endpoints expose repository names and spend API credits, so they are off (404) unless an
    /// admin key is configured, and require it in the X-Api-Key header. Returns null when authorized.
    /// </summary>
    private static IResult? Authorize(HttpRequest request, ReviewOptions options)
    {
        if (string.IsNullOrEmpty(options.AdminApiKey))
            return Results.NotFound();

        return KeysMatch(options.AdminApiKey, request.Headers["X-Api-Key"].ToString()) ? null : Results.Unauthorized();
    }

    private static bool KeysMatch(string expected, string provided) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)));
}
