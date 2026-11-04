using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BrainSim.Engine.Text;

namespace BrainSim.App;

/// <summary>
/// <c>app:MarkdownText.Markdown="{Binding Text}"</c> on a TextBlock renders the markdown as inlines.
/// A TextBlock rather than a FlowDocument viewer, so chat bubbles wrap and size to their text with no inner scrolling.
/// </summary>
public static class MarkdownText
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached(
        "Markdown", typeof(string), typeof(MarkdownText), new PropertyMetadata(null, OnChanged));

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Brush CodeBackground = Palette.Brush("#0E141B");
    private static readonly Brush Muted = Palette.Brush(Palette.TextMuted);
    private static readonly Brush LinkBrush = Palette.Brush("#22D3EE");

    public static string? GetMarkdown(DependencyObject o) => (string?)o.GetValue(MarkdownProperty);

    public static void SetMarkdown(DependencyObject o, string? value) => o.SetValue(MarkdownProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        var first = true;
        foreach (var b in Markdown.Parse((string?)e.NewValue ?? ""))
        {
            if (!first)
            {
                block.Inlines.Add(new LineBreak());
            }

            first = false;
            switch (b.Kind)
            {
                case BlockKind.Blank:
                    break; // the line break above leaves the gap between paragraphs
                case BlockKind.Heading:
                    var heading = new Span { FontWeight = FontWeights.SemiBold, FontSize = block.FontSize + Math.Max(0, 4 - b.Level) };
                    AddSpans(heading.Inlines, b.Spans);
                    block.Inlines.Add(heading);
                    break;
                case BlockKind.Bullet:
                    block.Inlines.Add(new Run(new string(' ', 2 + b.Level * 4) + "•  "));
                    AddSpans(block.Inlines, b.Spans);
                    break;
                case BlockKind.Numbered:
                    block.Inlines.Add(new Run(new string(' ', 2 + b.Level * 4) + b.Marker + "  "));
                    AddSpans(block.Inlines, b.Spans);
                    break;
                case BlockKind.Quote:
                    var quote = new Span { FontStyle = FontStyles.Italic, Foreground = Muted };
                    quote.Inlines.Add(new Run("  "));
                    AddSpans(quote.Inlines, b.Spans);
                    block.Inlines.Add(quote);
                    break;
                case BlockKind.Code:
                    block.Inlines.Add(new Run(" " + b.Spans[0].Text + " ") { FontFamily = Mono, Background = CodeBackground, FontSize = block.FontSize - 1 });
                    break;
                case BlockKind.Rule:
                    block.Inlines.Add(new Run("·  ·  ·") { Foreground = Muted });
                    break;
                case BlockKind.TableRow:
                    var row = new Span { FontFamily = Mono, FontSize = block.FontSize - 1 };
                    AddSpans(row.Inlines, b.Spans);
                    block.Inlines.Add(row);
                    break;
                default:
                    AddSpans(block.Inlines, b.Spans);
                    break;
            }
        }
    }

    private static void AddSpans(InlineCollection inlines, IReadOnlyList<MarkdownSpan> spans)
    {
        foreach (var s in spans)
        {
            var run = new Run(s.Text);
            if (s.Style.HasFlag(SpanStyle.Bold))
            {
                run.FontWeight = FontWeights.SemiBold;
            }

            if (s.Style.HasFlag(SpanStyle.Italic))
            {
                run.FontStyle = FontStyles.Italic;
            }

            if (s.Style.HasFlag(SpanStyle.Code))
            {
                run.FontFamily = Mono;
                run.Background = CodeBackground;
            }

            if (s.Style.HasFlag(SpanStyle.Link))
            {
                run.Foreground = LinkBrush;
                run.TextDecorations = TextDecorations.Underline;
            }

            inlines.Add(run);
        }
    }
}
