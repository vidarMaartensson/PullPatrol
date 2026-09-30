using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PullPatrol.Api.GitHub;

public static class GitHubJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public interface IGitHubClient
{
    Task<PullRequest> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct);

    Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(string owner, string repo, int number, CancellationToken ct);

    Task CreateReviewAsync(string owner, string repo, int number, CreateReviewRequest review, CancellationToken ct);

    Task SetCommitStatusAsync(string owner, string repo, string sha, CommitStatusRequest status, CancellationToken ct);
}

public sealed class GitHubApiException(HttpStatusCode statusCode, string responseBody)
    : Exception($"GitHub API returned {(int)statusCode}: {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>Typed HttpClient for the GitHub REST API. Auth headers and base address are configured in Program.cs.</summary>
public sealed class GitHubClient(HttpClient http) : IGitHubClient
{
    // GitHub returns at most 3000 files for a pull request (30 pages x 100).
    private const int MaxFilePages = 30;

    public async Task<PullRequest> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct)
    {
        using var response = await http.GetAsync($"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PullRequest>(GitHubJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var files = new List<PullRequestFile>();

        for (int page = 1; page <= MaxFilePages; page++)
        {
            using var response = await http.GetAsync(
                $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/files?per_page=100&page={page}", ct);
            await EnsureSuccessAsync(response, ct);

            var batch = await response.Content.ReadFromJsonAsync<List<PullRequestFile>>(GitHubJson.Options, ct) ?? [];
            files.AddRange(batch);

            if (batch.Count < 100)
                break;
        }

        return files;
    }

    public async Task CreateReviewAsync(string owner, string repo, int number, CreateReviewRequest review, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/reviews", review, GitHubJson.Options, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task SetCommitStatusAsync(string owner, string repo, string sha, CommitStatusRequest status, CancellationToken ct)
    {
        // GitHub rejects descriptions longer than 140 characters.
        var trimmed = status with { Description = status.Description.Length > 140 ? status.Description[..137] + "..." : status.Description };

        using var response = await http.PostAsJsonAsync(
            $"repos/{Esc(owner)}/{Esc(repo)}/statuses/{Esc(sha)}", trimmed, GitHubJson.Options, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new GitHubApiException(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }
}
