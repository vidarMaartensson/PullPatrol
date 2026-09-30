using System.Net.Http.Headers;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using PullPatrol.Api.Analysis;
using PullPatrol.Api.Configuration;
using PullPatrol.Api.GitHub;
using PullPatrol.Api.Queue;
using PullPatrol.Api.Reviews;
using PullPatrol.Api.Webhooks;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration (secrets come from user-secrets locally, environment variables in containers) ---
builder.Services.AddOptions<GitHubOptions>()
    .Bind(builder.Configuration.GetSection(GitHubOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<GroqOptions>()
    .Bind(builder.Configuration.GetSection(GroqOptions.SectionName))
    .PostConfigure(o => { if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY"); })
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ReviewOptions>()
    .Bind(builder.Configuration.GetSection(ReviewOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// --- GitHub API client with retries/timeouts/circuit breaker ---
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>((sp, http) =>
    {
        var options = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
        http.BaseAddress = new Uri(options.ApiBaseUrl);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PullPatrol/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    })
    // Don't retry POSTs: a retried "create review" could post the same review twice.
    .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());

// --- Review pipeline ---
// --- AI reviewer: Llama via Groq's OpenAI-compatible API ---
builder.Services.AddHttpClient<IAiReviewer, GroqReviewer>((sp, http) =>
    {
        var options = sp.GetRequiredService<IOptions<GroqOptions>>().Value;
        http.BaseAddress = new Uri(options.BaseUrl);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    })
    .AddStandardResilienceHandler(o =>
    {
        // No POST retries: on a 429 we'd rather fall back to static rules right away than wait.
        o.Retry.DisableForUnsafeHttpMethods();
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
    });
builder.Services.AddSingleton<GroqRateLimitState>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StaticRuleAnalyzer>();
builder.Services.AddSingleton<WebhookSignatureValidator>();
builder.Services.AddSingleton<ReviewQueue>();
builder.Services.AddSingleton<ReviewHistory>();
builder.Services.AddScoped<PullRequestReviewService>();
builder.Services.AddHostedService<PullRequestReviewWorker>();

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapGet("/", () => Results.Ok(new { service = "PullPatrol", status = "running" })).ExcludeFromDescription();
app.MapHealthChecks("/health");
app.MapGitHubWebhook();
app.MapReviewEndpoints();

var aiOptions = app.Services.GetRequiredService<IOptions<GroqOptions>>().Value;
app.Logger.LogInformation(aiOptions.IsEnabled
    ? $"AI review enabled ({aiOptions.DisplayName}, model {aiOptions.Model})"
    : "AI review disabled: set Groq:ApiKey to enable it. Static rules will still run.");

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program;
