using BrainSim.Engine.Text;

namespace BrainSim.Engine.Tests;

public class MarkdownTests
{
    [Fact]
    public void Inline_marks_become_styles_and_snake_case_is_left_alone()
    {
        var spans = Markdown.Inline("Octopuses have **three hearts**, *blue* blood, a `max_tokens` cap, see [docs](https://x.y) and my_file_name.");

        Assert.Equal(
            [
                new MarkdownSpan("Octopuses have ", SpanStyle.None),
                new MarkdownSpan("three hearts", SpanStyle.Bold),
                new MarkdownSpan(", ", SpanStyle.None),
                new MarkdownSpan("blue", SpanStyle.Italic),
                new MarkdownSpan(" blood, a ", SpanStyle.None),
                new MarkdownSpan("max_tokens", SpanStyle.Code),
                new MarkdownSpan(" cap, see ", SpanStyle.None),
                new MarkdownSpan("docs", SpanStyle.Link),
                new MarkdownSpan(" and my_file_name.", SpanStyle.None),
            ],
            spans);
    }

    [Fact]
    public void Blocks_cover_headings_lists_quotes_code_rules_and_tables()
    {
        const string text = "## Facts\n\n- one\n  - nested\n2. two\n> quoted\n---\n```\nvar x = 1;\n```\n| a | b |\n|---|---|\n| 1 | 2 |\n\n\n";

        var blocks = Markdown.Parse(text);

        Assert.Equal(
            [BlockKind.Heading, BlockKind.Blank, BlockKind.Bullet, BlockKind.Bullet, BlockKind.Numbered, BlockKind.Quote, BlockKind.Rule, BlockKind.Code, BlockKind.TableRow, BlockKind.TableRow],
            blocks.Select(b => b.Kind));
        Assert.Equal(2, blocks[0].Level);
        Assert.Equal(1, blocks[3].Level);
        Assert.Equal("2.", blocks[4].Marker);
        Assert.Equal("var x = 1;", blocks[7].Spans[0].Text);
        Assert.Equal("a   |   b", blocks[8].Spans[0].Text);
    }

    [Fact]
    public void Unclosed_marks_while_streaming_stay_as_text()
    {
        Assert.Equal([new MarkdownSpan("This is **bol", SpanStyle.None)], Markdown.Inline("This is **bol"));
    }

    [Fact]
    public void A_plain_line_is_a_paragraph()
    {
        var block = Assert.Single(Markdown.Parse("Just a sentence, with *some* emphasis."));

        Assert.Equal(BlockKind.Paragraph, block.Kind);
        Assert.Equal(["Just a sentence, with ", "some", " emphasis."], block.Spans.Select(s => s.Text));
    }
}
