using PullPatrol.Api.Analysis;

namespace PullPatrol.Tests;

public class DiffParserTests
{
    private const string Patch =
        "@@ -10,4 +10,5 @@ public class Foo\n" +
        " unchanged line 10\n" +
        "-removed line\n" +
        "+added line 11\n" +
        "+added line 12\n" +
        " unchanged line 13\n" +
        "@@ -40,2 +41,3 @@\n" +
        " context 41\n" +
        "+added 42\n" +
        " context 43\n" +
        "\\ No newline at end of file";

    [Fact]
    public void Parse_TracksNewFileLineNumbers()
    {
        var parsed = DiffParser.Parse(Patch);

        var added = parsed.AddedLines.Select(l => (l.NewLineNumber, l.Content)).ToList();

        Assert.Equal([(11, "added line 11"), (12, "added line 12"), (42, "added 42")], added);
    }

    [Theory]
    [InlineData(10, true)]  // context
    [InlineData(11, true)]  // added
    [InlineData(13, true)]  // context
    [InlineData(14, false)] // between hunks
    [InlineData(43, true)]
    [InlineData(44, false)]
    public void IsCommentable_OnlyForLinesInsideTheDiff(int line, bool expected)
    {
        Assert.Equal(expected, DiffParser.Parse(Patch).IsCommentable(line));
    }

    [Fact]
    public void Parse_NullPatch_ReturnsEmpty()
    {
        Assert.Empty(DiffParser.Parse(null).Lines);
    }

    [Fact]
    public void ToAnnotatedText_PrefixesLineNumbers()
    {
        var text = DiffParser.Parse(Patch).ToAnnotatedText();

        Assert.Contains("   11 | + added line 11", text);
        Assert.Contains("      | - removed line", text);
        Assert.Contains("   10 |   unchanged line 10", text);
    }
}
