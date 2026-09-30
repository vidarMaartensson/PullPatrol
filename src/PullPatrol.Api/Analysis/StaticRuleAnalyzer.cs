using System.Text.RegularExpressions;

namespace PullPatrol.Api.Analysis;

/// <param name="Extensions">File extensions the rule applies to; null means all files.</param>
public sealed record StaticRule(
    string Id,
    string Title,
    Severity Severity,
    string Category,
    Regex Pattern,
    string Message,
    string? Suggestion = null,
    string[]? Extensions = null)
{
    public bool AppliesTo(string path) =>
        Extensions is null || Extensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Fast, deterministic checks that run on every added line. They catch well-known risky patterns
/// without any API cost, and complement the AI reviewer.
/// </summary>
public sealed class StaticRuleAnalyzer
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly string[] CSharp = [".cs"];
    private static readonly string[] JavaScript = [".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs"];

    public static IReadOnlyList<StaticRule> DefaultRules { get; } =
    [
        new("PP001", "Possible hardcoded secret", Severity.Critical, "security",
            new Regex(@"(?i)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)\b[""']?\s*[:=]\s*[""'][^""'\s]{8,}[""']", Opts),
            "A credential appears to be hardcoded in source code. Anyone with read access to the repository (and its history) can use it.",
            "Load the value from configuration (environment variables, user secrets, or a secret manager) and rotate the exposed credential."),

        new("PP002", "Cloud or GitHub token in source", Severity.Critical, "security",
            new Regex(@"\b(AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{50,}|sk-ant-[A-Za-z0-9_\-]{20,}|xox[baprs]-[A-Za-z0-9-]{10,})\b", Opts),
            "This looks like a real access token (AWS, GitHub, Anthropic or Slack). Committed tokens should be considered leaked.",
            "Revoke the token immediately and load a new one from a secret store."),

        new("PP003", "Private key committed", Severity.Critical, "security",
            new Regex(@"-----BEGIN (RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----", Opts),
            "A private key is being added to the repository.",
            "Remove the key, rotate it, and store it in a secret manager."),

        new("PP004", "SQL built with string concatenation", Severity.High, "security",
            new Regex(@"(?i)(\$""|""\s*\+|\+\s*""|string\.Format\(|f""|`).*\b(SELECT\s.+\sFROM|INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM)\b|\b(SELECT\s.+\sFROM|INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM)\b.*(""\s*\+|\+\s*\w|\{\w+\}|\$\{)", Opts),
            "SQL appears to be built by concatenating or interpolating values, which can lead to SQL injection.",
            "Use parameterized queries (e.g. SqlParameter, Dapper parameters, or EF Core LINQ / FromSql with interpolation handled by EF)."),

        new("PP005", "TLS certificate validation disabled", Severity.High, "security",
            new Regex(@"(ServerCertificateCustomValidationCallback\s*=.*=>\s*true|DangerousAcceptAnyServerCertificateValidator|rejectUnauthorized\s*:\s*false|verify\s*=\s*False|NODE_TLS_REJECT_UNAUTHORIZED\s*=\s*['""]?0)", Opts),
            "Certificate validation is turned off, which allows man-in-the-middle attacks.",
            "Keep certificate validation enabled; trust a specific CA or certificate if you need a custom one."),

        new("PP006", "Weak hash algorithm", Severity.Medium, "security",
            new Regex(@"(MD5\.Create|SHA1\.Create|MD5CryptoServiceProvider|SHA1Managed|hashlib\.(md5|sha1)\(|createHash\(\s*['""](md5|sha1)['""])", Opts),
            "MD5 and SHA-1 are broken for security purposes (collisions are practical).",
            "Use SHA-256 or stronger. For passwords, use a password hashing function such as PBKDF2, bcrypt or Argon2."),

        new("PP007", "Dynamic code execution / unsafe HTML", Severity.Medium, "security",
            new Regex(@"(\beval\s*\(|new\s+Function\s*\(|\.innerHTML\s*=|dangerouslySetInnerHTML)", Opts),
            "Executing strings as code or writing raw HTML can lead to cross-site scripting (XSS) if the input is user-controlled.",
            "Avoid eval; use textContent or a sanitizer (e.g. DOMPurify) for HTML.",
            JavaScript),

        new("PP101", "Blocking on async code", Severity.Medium, "performance",
            new Regex(@"(\.Result\b(?!\s*=)|\.Wait\(\s*\)|\.GetAwaiter\(\)\.GetResult\(\))", Opts),
            "Blocking on a Task (.Result, .Wait(), GetAwaiter().GetResult()) wastes thread-pool threads and can deadlock under a synchronization context.",
            "Make the calling method async and use await.",
            CSharp),

        new("PP102", "new HttpClient() per call", Severity.Low, "performance",
            new Regex(@"new\s+HttpClient\s*\(", Opts),
            "Creating HttpClient instances per request can exhaust sockets and ignores DNS changes.",
            "Inject HttpClient via IHttpClientFactory (AddHttpClient) or reuse a long-lived instance.",
            CSharp),

        new("PP201", "async void method", Severity.Medium, "correctness",
            new Regex(@"\basync\s+void\s+(?!On[A-Z])\w+\s*\(", Opts),
            "Exceptions thrown in an async void method cannot be caught by the caller and will crash the process.",
            "Return Task instead (async void is only appropriate for event handlers).",
            CSharp),

        new("PP202", "Empty catch block", Severity.Medium, "correctness",
            new Regex(@"catch\s*(\([^)]*\))?\s*\{\s*\}", Opts),
            "Exceptions are swallowed silently, which hides failures and makes debugging hard.",
            "Log the exception, handle it explicitly, or let it propagate."),
    ];

    private readonly IReadOnlyList<StaticRule> _rules;

    public StaticRuleAnalyzer() : this(DefaultRules) { }

    public StaticRuleAnalyzer(IReadOnlyList<StaticRule> rules) => _rules = rules;

    public IReadOnlyList<Finding> Analyze(IEnumerable<ChangedFile> files)
    {
        var findings = new List<Finding>();

        foreach (var file in files)
        {
            var rules = _rules.Where(r => r.AppliesTo(file.Path)).ToList();
            if (rules.Count == 0)
                continue;

            foreach (var line in file.Diff.AddedLines)
            {
                if (IsComment(line.Content))
                    continue;

                foreach (var rule in rules)
                {
                    if (rule.Pattern.IsMatch(line.Content))
                    {
                        findings.Add(new Finding(
                            file.Path, line.NewLineNumber, rule.Severity, rule.Category,
                            rule.Title, rule.Message, rule.Suggestion, rule.Id));
                    }
                }
            }
        }

        return findings;
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith('#')
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith('*');
    }
}
