using System.Text;
using System.Text.RegularExpressions;

namespace PullPatrol.Api.Analysis;

public enum DiffLineKind
{
    HunkHeader,
    Context,
    Added,
    Removed,
}

/// <param name="NewLineNumber">Line number in the new file; null for removed lines and hunk headers.</param>
public sealed record DiffLine(DiffLineKind Kind, int? NewLineNumber, string Content);

public sealed class ParsedPatch
{
    private readonly HashSet<int> _commentableLines;

    public ParsedPatch(IReadOnlyList<DiffLine> lines)
    {
        Lines = lines;
        _commentableLines = lines
            .Where(l => l.NewLineNumber is not null)
            .Select(l => l.NewLineNumber!.Value)
            .ToHashSet();
    }

    public static ParsedPatch Empty { get; } = new([]);

    public IReadOnlyList<DiffLine> Lines { get; }

    public IEnumerable<DiffLine> AddedLines => Lines.Where(l => l.Kind == DiffLineKind.Added);

    /// <summary>
    /// GitHub only accepts review comments on lines that are part of the diff (added or context lines on the RIGHT side).
    /// </summary>
    public bool IsCommentable(int newLineNumber) => _commentableLines.Contains(newLineNumber);

    /// <summary>Renders the patch with new-file line numbers, so an LLM can reference exact lines.</summary>
    public string ToAnnotatedText()
    {
        var sb = new StringBuilder();
        foreach (var line in Lines)
        {
            switch (line.Kind)
            {
                case DiffLineKind.HunkHeader:
                    sb.Append("      | ").AppendLine(line.Content);
                    break;
                case DiffLineKind.Added:
                    sb.Append($"{line.NewLineNumber,5} | + ").AppendLine(line.Content);
                    break;
                case DiffLineKind.Context:
                    sb.Append($"{line.NewLineNumber,5} |   ").AppendLine(line.Content);
                    break;
                case DiffLineKind.Removed:
                    sb.Append("      | - ").AppendLine(line.Content);
                    break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>Parses the unified-diff "patch" field that GitHub returns for each file in a pull request.</summary>
public static partial class DiffParser
{
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeaderRegex();

    public static ParsedPatch Parse(string? patch)
    {
        if (string.IsNullOrEmpty(patch))
            return ParsedPatch.Empty;

        var lines = new List<DiffLine>();
        int newLine = 0;

        foreach (var raw in patch.Split('\n'))
        {
            var text = raw.TrimEnd('\r');

            var header = HunkHeaderRegex().Match(text);
            if (header.Success)
            {
                newLine = int.Parse(header.Groups[1].Value);
                lines.Add(new DiffLine(DiffLineKind.HunkHeader, null, text));
                continue;
            }

            if (text.Length == 0)
            {
                // Trailing newline at end of patch; a real blank context line is " ".
                continue;
            }

            switch (text[0])
            {
                case '+':
                    lines.Add(new DiffLine(DiffLineKind.Added, newLine++, text[1..]));
                    break;
                case '-':
                    lines.Add(new DiffLine(DiffLineKind.Removed, null, text[1..]));
                    break;
                case ' ':
                    lines.Add(new DiffLine(DiffLineKind.Context, newLine++, text[1..]));
                    break;
                case '\\':
                    // "\ No newline at end of file"
                    break;
            }
        }

        return new ParsedPatch(lines);
    }
}
