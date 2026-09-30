using PullPatrol.Api.Reviews;

namespace PullPatrol.Api.Queue;

/// <summary>Background service that drains the review queue, one pull request at a time.</summary>
public sealed class PullRequestReviewWorker(
    ReviewQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<PullRequestReviewWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Review worker started");

        try
        {
            await foreach (var job in queue.ReadAllAsync(stoppingToken))
            {
                using var scope = scopeFactory.CreateScope();
                var reviewer = scope.ServiceProvider.GetRequiredService<PullRequestReviewService>();

                using var _ = logger.BeginScope(new Dictionary<string, object>
                {
                    ["Repository"] = $"{job.Owner}/{job.Repo}",
                    ["PullRequest"] = job.Number,
                });

                try
                {
                    await reviewer.ReviewAsync(job, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never let one bad job take down the worker.
                    logger.LogError(ex, "Unhandled error while reviewing {Owner}/{Repo}#{Number}", job.Owner, job.Repo, job.Number);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        logger.LogInformation("Review worker stopped");
    }
}
