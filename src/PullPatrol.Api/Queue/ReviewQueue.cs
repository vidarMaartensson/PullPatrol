using System.Threading.Channels;

namespace PullPatrol.Api.Queue;

public sealed record ReviewJob(
    string Owner,
    string Repo,
    int Number,
    string HeadSha,
    string Title,
    string? Body,
    string HtmlUrl,
    string Trigger);

/// <summary>
/// In-process queue between the webhook endpoint and the background worker. The endpoint only
/// validates and enqueues, so GitHub gets a response well within its 10 second webhook timeout.
/// </summary>
public sealed class ReviewQueue
{
    private readonly Channel<ReviewJob> _channel = Channel.CreateBounded<ReviewJob>(
        new BoundedChannelOptions(capacity: 100)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public bool TryEnqueue(ReviewJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<ReviewJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
