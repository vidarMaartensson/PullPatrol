using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PullPatrol.Api.Configuration;
using PullPatrol.Api.Webhooks;

namespace PullPatrol.Tests;

public class WebhookSignatureValidatorTests
{
    private const string Secret = "It's a Secret to Everybody";

    private static readonly WebhookSignatureValidator Validator =
        new(Options.Create(new GitHubOptions { WebhookSecret = Secret, Token = "t" }));

    public static string Sign(string body, string secret = Secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void AcceptsGitHubsDocumentedExample()
    {
        // Test vector from GitHub's "Validating webhook deliveries" docs.
        var body = Encoding.UTF8.GetBytes("Hello, World!");
        const string signature = "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17";

        Assert.True(Validator.IsValid(body, signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=abc")]
    [InlineData("sha256=not-hex")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e18")]
    public void RejectsMissingOrWrongSignatures(string? signature)
    {
        Assert.False(Validator.IsValid(Encoding.UTF8.GetBytes("Hello, World!"), signature));
    }

    [Fact]
    public void RejectsSignatureMadeWithAnotherSecret()
    {
        const string body = "{\"action\":\"opened\"}";
        Assert.False(Validator.IsValid(Encoding.UTF8.GetBytes(body), Sign(body, "wrong secret")));
    }
}
