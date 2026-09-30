using System.ComponentModel.DataAnnotations;
using PullPatrol.Api.Analysis;

namespace PullPatrol.Api.Configuration;

public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>Personal access token (or installation token) with pull request + commit status write access.</summary>
    [Required]
    public string Token { get; set; } = "";

    /// <summary>Shared secret configured on the GitHub webhook, used to verify X-Hub-Signature-256.</summary>
    [Required]
    public string WebhookSecret { get; set; } = "";

    [Required, Url]
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";
}

public sealed class GroqOptions
{
    public const string SectionName = "Groq";

    /// <summary>Free key from console.groq.com. When empty, the AI reviewer is disabled and only the static rules run.</summary>
    public string? ApiKey { get; set; }

    [Required, Url]
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1/";

    [Required]
    public string Model { get; set; } = "llama-3.3-70b-versatile";

    [Required]
    public string DisplayName { get; set; } = "Llama 3.3 70B via Groq";

    /// <summary>
    /// Upper bound on estimated prompt tokens per request. Keep input + output below the model's
    /// tokens-per-minute limit on your Groq plan; files that don't fit only get static checks.
    /// </summary>
    [Range(1_000, 120_000)]
    public int MaxInputTokens { get; set; } = 8_000;

    [Range(256, 32_000)]
    public int MaxOutputTokens { get; set; } = 2_000;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class ReviewOptions
{
    public const string SectionName = "Review";

    /// <summary>Findings beyond this count are listed in the review body instead of as inline comments.</summary>
    [Range(0, 100)]
    public int MaxInlineComments { get; set; } = 25;

    /// <summary>The commit status is set to "failure" when any finding is at or above this severity.</summary>
    public Severity FailOnSeverity { get; set; } = Severity.High;

    public bool PostCommitStatus { get; set; } = true;

    /// <summary>Glob-like suffixes/segments for files that are never analyzed (lock files, generated code, ...).</summary>
    public string[] IgnoredPaths { get; set; } =
    [
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "packages.lock.json",
        ".min.js", ".min.css", ".Designer.cs", ".g.cs", "/Migrations/",
    ];

    /// <summary>Key required for the manual trigger endpoint. When empty, that endpoint is disabled.</summary>
    public string? AdminApiKey { get; set; }
}
