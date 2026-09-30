using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PullPatrol.Api.Configuration;

namespace PullPatrol.Api.Webhooks;

/// <summary>
/// Verifies the X-Hub-Signature-256 header GitHub sends with every webhook delivery:
/// "sha256=" + hex(HMAC-SHA256(secret, rawBody)).
/// </summary>
public sealed class WebhookSignatureValidator(IOptions<GitHubOptions> options)
{
    private const string Prefix = "sha256=";

    public bool IsValid(ReadOnlySpan<byte> body, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signatureHeader.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var key = Encoding.UTF8.GetBytes(options.Value.WebhookSecret);
        var expected = HMACSHA256.HashData(key, body);

        // Constant-time comparison so the signature can't be discovered through timing.
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
