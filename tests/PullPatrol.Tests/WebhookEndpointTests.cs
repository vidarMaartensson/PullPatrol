using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PullPatrol.Api.Analysis;
using PullPatrol.Api.GitHub;

namespace PullPatrol.Tests;

public class WebhookEndpointTests : IClassFixture<WebhookEndpointTests.Factory>
{
    public const string Secret = "test-webhook-secret";

    private readonly Factory _factory;

    public WebhookEndpointTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeGitHubClient GitHub { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("GitHub:Token", "test-token");
            builder.UseSetting("GitHub:WebhookSecret", Secret);
            builder.UseSetting("Groq:ApiKey", "");

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IGitHubClient>(GitHub);
                services.AddSingleton<IAiReviewer>(new FakeAiReviewer());
            });
        }
    }

    private static HttpRequestMessage Delivery(string eventName, string body, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        request.Headers.Add("X-Hub-Signature-256", signature ?? WebhookSignatureValidatorTests.Sign(body, Secret));
        return request;
    }

    private static string PullRequestPayload(string action, bool draft = false) => $$"""
        {
          "action": "{{action}}",
          "number": 42,
          "pull_request": {
            "number": 42,
            "title": "Add login",
            "body": "Adds a login endpoint",
            "draft": {{(draft ? "true" : "false")}},
            "html_url": "https://github.com/octo/demo/pull/42",
            "head": { "sha": "deadbeef", "ref": "feature/login" }
          },
          "repository": {
            "name": "demo",
            "full_name": "octo/demo",
            "owner": { "login": "octo" }
          }
        }
        """;

    [Fact]
    public async Task InvalidSignature_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Delivery("ping", "{}", signature: "sha256=00"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ping_ReturnsPong()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Delivery("ping", "{\"zen\":\"Keep it logically awesome.\"}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("pong", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("closed", false)]
    [InlineData("opened", true)]
    public async Task IgnoredActionsAndDrafts_Return200WithoutQueueing(string action, bool draft)
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Delivery("pull_request", PullRequestPayload(action, draft)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenedPullRequest_IsQueuedAndReviewedInTheBackground()
    {
        _factory.GitHub.Files.Add(new PullRequestFile("src/Login.cs", "added", 1, 0,
            "@@ -0,0 +1,1 @@\n+var password = \"hunter2hunter2\";"));
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Delivery("pull_request", PullRequestPayload("opened")));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The worker runs asynchronously; wait for it to post the review.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_factory.GitHub.Reviews.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        var review = Assert.Single(_factory.GitHub.Reviews);
        Assert.Equal("deadbeef", review.CommitId);
        var comment = Assert.Single(review.Comments);
        Assert.Equal("src/Login.cs", comment.Path);
        Assert.Equal(1, comment.Line);
        Assert.Contains("hardcoded secret", comment.Body);
    }
}
