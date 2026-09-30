using System.Text;
using PullPatrol.Api.Analysis;

namespace PullPatrol.Api.Reviews;

public static class ReviewCommentFormatter
{
    private const string Marker = "<!-- pullpatrol -->";

    public static string SeverityBadge(Severity severity) => severity switch
    {
        Severity.Critical => "🔴 **Critical**",
        Severity.High => "🟠 **High**",
        Severity.Medium => "🟡 **Medium**",
        Severity.Low => "🔵 **Low**",
        _ => "⚪ **Info**",
    };

    public static string FormatInline(Finding finding, string aiName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{SeverityBadge(finding.Severity)} · {finding.Category} · {finding.Title}");
        sb.AppendLine();
        sb.AppendLine(finding.Message);

        if (finding.Suggestion is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"**Suggestion:** {finding.Suggestion}");
        }

        sb.AppendLine();
        sb.Append($"<sub>{SourceLabel(finding, aiName)}</sub>");
        return sb.ToString();
    }

    public static string FormatSummary(
        string? aiSummary,
        IReadOnlyList<Finding> allFindings,
        IReadOnlyList<Finding> notInline,
        string analyzedBy)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Marker);
        sb.AppendLine("## 🚓 PullPatrol review");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(aiSummary))
        {
            sb.AppendLine(aiSummary.Trim());
            sb.AppendLine();
        }

        if (allFindings.Count == 0)
        {
            sb.AppendLine("✅ No issues found in the changed lines.");
        }
        else
        {
            sb.AppendLine("| Severity | Count |");
            sb.AppendLine("|---|---|");
            foreach (var group in allFindings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key))
                sb.AppendLine($"| {SeverityBadge(group.Key)} | {group.Count()} |");
        }

        if (notInline.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Other findings");
            foreach (var f in notInline)
            {
                var location = f.Line is { } line ? $"`{f.Path}:{line}`" : $"`{f.Path}`";
                sb.AppendLine($"- {SeverityBadge(f.Severity)} {location} — **{f.Title}**: {f.Message}");
            }
        }

        sb.AppendLine();
        sb.Append($"<sub>Automated review by PullPatrol · {analyzedBy}. Findings may be wrong — use your judgement.</sub>");
        return sb.ToString();
    }

    private static string SourceLabel(Finding finding, string aiName) =>
        finding.Source == Finding.AiSource ? $"Found by AI review ({aiName})" : $"PullPatrol rule {finding.Source}";
}
