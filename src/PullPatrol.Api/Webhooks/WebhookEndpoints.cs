using System.Text.Json;
using PullPatrol.Api.GitHub;
using PullPatrol.Api.Queue;

namespace PullPatrol.Api.Webhooks;

public static class WebhookEndpoints
{
    private static readonly HashSet<string> ReviewableActions = ["opened", "reopened", "synchronize", "ready_for_review"];

    public static IEndpointRouteBuilder MapGitHubWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/github", HandleAsync)
            .WithName("GitHubWebhook")
            .WithSummary("Receives GitHub webhook deliveries and queues pull requests for review.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        WebhookSignatureValidator validator,
        ReviewQueue queue,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("PullPatrol.Webhooks");

        // The signature is computed over the exact raw bytes, so read them before deserializing.
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();

        if (!validator.IsValid(body, request.Headers["X-Hub-Signature-256"]))
        {
            logger.LogWarning("Rejected webhook delivery with an invalid signature");
            return Results.Unauthorized();
        }

        var eventName = request.Headers["X-GitHub-Event"].ToString();
        var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();

        if (eventName == "ping")
            return Results.Ok(new { message = "pong" });

        if (eventName != "pull_request")
            return Results.Ok(new { message = $"Ignored event '{eventName}'" });

        PullRequestEvent? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PullRequestEvent>(body, GitHubJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse pull_request payload for delivery {DeliveryId}", deliveryId);
            return Results.BadRequest(new { message = "Invalid pull_request payload" });
        }

        if (payload is null)
            return Results.BadRequest(new { message = "Empty payload" });

        if (!ReviewableActions.Contains(payload.Action))
            return Results.Ok(new { message = $"Ignored action '{payload.Action}'" });

        if (payload.PullRequest.Draft)
            return Results.Ok(new { message = "Ignored draft pull request" });

        var job = new ReviewJob(
            payload.Repository.Owner.Login,
            payload.Repository.Name,
            payload.PullRequest.Number,
            payload.PullRequest.Head.Sha,
            payload.PullRequest.Title,
            payload.PullRequest.Body,
            payload.PullRequest.HtmlUrl,
            Trigger: $"webhook:{payload.Action}");

        if (!queue.TryEnqueue(job))
        {
            logger.LogError("Review queue is full; dropping {Repo}#{Number}", payload.Repository.FullName, job.Number);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("Queued review for {Repo}#{Number} at {Sha} (delivery {DeliveryId})",
            payload.Repository.FullName, job.Number, job.HeadSha, deliveryId);

        return Results.Accepted(value: new { message = "Review queued", repository = payload.Repository.FullName, pullRequest = job.Number });
    }
}
