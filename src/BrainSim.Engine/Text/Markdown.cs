using System.Text.RegularExpressions;

namespace BrainSim.Engine.Text;

[Flags]
public enum SpanStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Link = 8,
}

public enum BlockKind
{
    Paragraph,
    Blank,
    Heading,
    Bullet,
    Numbered,
    Quote,
    Code,
    Rule,
    TableRow,
}

public sealed record MarkdownSpan(string Text, SpanStyle Style);

/// <summary>One line of rendered markdown. <paramref name="Level"/> is the heading level or the list indent.</summary>
public sealed record MarkdownBlock(BlockKind Kind, int Level, string Marker, IReadOnlyList<MarkdownSpan> Spans);

/// <summary>
/// The markdown a small model actually writes, parsed line by line: headings, bullet and numbered lists, quotes,
/// code fences, rules, table rows, and inline bold, italic, code and links. Pure text, no UI, so it is testable.
/// Unclosed marks (a reply still streaming) are left as they are.
/// </summary>
public static partial class Markdown
{
    public static List<MarkdownBlock> Parse(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var inCode = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                blocks.Add(new MarkdownBlock(BlockKind.Code, 0, "", [new MarkdownSpan(line, SpanStyle.Code)]));
                continue;
            }

            if (trimmed.Length == 0)
            {
                if (blocks.Count > 0 && blocks[^1].Kind != BlockKind.Blank)
                {
                    blocks.Add(new MarkdownBlock(BlockKind.Blank, 0, "", []));
                }

                continue;
            }

            var indent = (line.Length - trimmed.Length) / 2;
            Match m;
            if ((m = HeadingPattern().Match(trimmed)).Success)
            {
                blocks.Add(new MarkdownBlock(BlockKind.Heading, m.Groups[1].Length, "", Inline(m.Groups[2].Value)));
            }
            else if (RulePattern().IsMatch(trimmed))
            {
                blocks.Add(new MarkdownBlock(BlockKind.Rule, 0, "", []));
            }
            else if ((m = BulletPattern().Match(trimmed)).Success)
            {
                blocks.Add(new MarkdownBlock(BlockKind.Bullet, indent, "", Inline(m.Groups[1].Value)));
            }
            else if ((m = NumberedPattern().Match(trimmed)).Success)
            {
                blocks.Add(new MarkdownBlock(BlockKind.Numbered, indent, m.Groups[1].Value + ".", Inline(m.Groups[2].Value)));
            }
            else if (trimmed.StartsWith('>'))
            {
                blocks.Add(new MarkdownBlock(BlockKind.Quote, 0, "", Inline(trimmed.TrimStart('>', ' '))));
            }
            else if (trimmed.StartsWith('|'))
            {
                if (!TableSeparatorPattern().IsMatch(trimmed))
                {
                    var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim());
                    blocks.Add(new MarkdownBlock(BlockKind.TableRow, 0, "", Inline(string.Join("   |   ", cells))));
                }
            }
            else
            {
                blocks.Add(new MarkdownBlock(BlockKind.Paragraph, 0, "", Inline(trimmed)));
            }
        }

        if (blocks.Count > 0 && blocks[^1].Kind == BlockKind.Blank)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        return blocks;
    }

    public static List<MarkdownSpan> Inline(string text)
    {
        var spans = new List<MarkdownSpan>();
        var at = 0;
        foreach (Match m in InlinePattern().Matches(text))
        {
            if (m.Index > at)
            {
                spans.Add(new MarkdownSpan(text[at..m.Index], SpanStyle.None));
            }

            var g = m.Groups;
            spans.Add(
                g["code"].Success ? new MarkdownSpan(g["code"].Value, SpanStyle.Code)
                : g["bold"].Success ? new MarkdownSpan(g["bold"].Value, SpanStyle.Bold)
                : g["bold2"].Success ? new MarkdownSpan(g["bold2"].Value, SpanStyle.Bold)
                : g["italic"].Success ? new MarkdownSpan(g["italic"].Value, SpanStyle.Italic)
                : g["italic2"].Success ? new MarkdownSpan(g["italic2"].Value, SpanStyle.Italic)
                : new MarkdownSpan(g["link"].Value, SpanStyle.Link));
            at = m.Index + m.Length;
        }

        if (at < text.Length)
        {
            spans.Add(new MarkdownSpan(text[at..], SpanStyle.None));
        }

        return spans;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^[-*+]\s+(.*)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^(\d+)[.)]\s+(.*)$")]
    private static partial Regex NumberedPattern();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparatorPattern();

    // Underscore italics only outside words, so snake_case and file_names stay as they are.
    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|(?<![\p{L}\p{N}])__(?<bold2>[^_]+)__(?![\p{L}\p{N}])|\*(?<italic>[^*\s][^*]*)\*|(?<![\p{L}\p{N}])_(?<italic2>[^_\s][^_]*)_(?![\p{L}\p{N}])|\[(?<link>[^\]]+)\]\([^)\s]+\)")]
    private static partial Regex InlinePattern();
}
