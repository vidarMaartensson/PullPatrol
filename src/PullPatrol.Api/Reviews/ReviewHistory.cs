using System.Collections.Concurrent;
using PullPatrol.Api.Analysis;

namespace PullPatrol.Api.Reviews;

public sealed record ReviewRecord(
    string Repository,
    int PullRequest,
    string HeadSha,
    string Trigger,
    DateTimeOffset CompletedAt,
    string Outcome,
    int FindingCount,
    IReadOnlyDictionary<Severity, int> BySeverity,
    bool AiReviewUsed,
    string? Error);

/// <summary>Keeps the most recent reviews in memory for the /api/reviews endpoint.</summary>
public sealed class ReviewHistory
{
    private const int Capacity = 50;
    private readonly ConcurrentQueue<ReviewRecord> _records = new();

    public void Add(ReviewRecord record)
    {
        _records.Enqueue(record);
        while (_records.Count > Capacity && _records.TryDequeue(out _)) { }
    }

    public IReadOnlyList<ReviewRecord> Recent() => _records.Reverse().ToList();
}
