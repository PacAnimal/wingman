using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

// resolve ambiguity: Block/Inline exist in both Markdig and WPF
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace Wingman;

internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly FontFamily MonoFont = new("Consolas");
    private static readonly SolidColorBrush CodeBg = new(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly SolidColorBrush CodeFg = new(Color.FromRgb(0xD4, 0xD4, 0xD4));
    private static readonly SolidColorBrush InlineCodeFg = new(Color.FromRgb(0xE8, 0xE8, 0xE8));
    private static readonly SolidColorBrush LinkFg = new(Color.FromRgb(0x4E, 0xC9, 0xE0));
    private static readonly SolidColorBrush QuoteFg = new(Color.FromRgb(0x99, 0x99, 0x99));

    internal static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            Foreground = Brushes.White,
            FontSize = 13,
            PagePadding = new Thickness(0),
        };

        var ast = Markdown.Parse(markdown, Pipeline);
        foreach (var block in ast)
        {
            var b = RenderBlock(block);
            if (b != null) doc.Blocks.Add(b);
        }

        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(new Paragraph());

        return doc;
    }

    private static WpfBlock? RenderBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case ParagraphBlock para:
                {
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                    AddInlines(p.Inlines, para.Inline);
                    return p;
                }
            case HeadingBlock heading:
                {
                    double fontSize = heading.Level switch { 1 => 20, 2 => 17, _ => 14 };
                    var p = new Paragraph { FontSize = fontSize, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 4) };
                    AddInlines(p.Inlines, heading.Inline);
                    return p;
                }
            case FencedCodeBlock code:
                {
                    var p = new Paragraph
                    {
                        FontFamily = MonoFont,
                        FontSize = 12,
                        Background = CodeBg,
                        Foreground = CodeFg,
                        Padding = new Thickness(8, 4, 8, 4),
                        Margin = new Thickness(0, 4, 0, 8),
                        LineHeight = 16,
                    };
                    p.Inlines.Add(new Run(GetCodeText(code)));
                    return p;
                }
            case CodeBlock codeBlock:
                {
                    var p = new Paragraph
                    {
                        FontFamily = MonoFont,
                        FontSize = 12,
                        Background = CodeBg,
                        Foreground = CodeFg,
                        Padding = new Thickness(8, 4, 8, 4),
                        Margin = new Thickness(0, 4, 0, 8),
                        LineHeight = 16,
                    };
                    p.Inlines.Add(new Run(GetCodeText(codeBlock)));
                    return p;
                }
            case ListBlock list:
                {
                    var wl = new List
                    {
                        MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 6),
                        Padding = new Thickness(20, 0, 0, 0),
                    };
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var li = new ListItem { Margin = new Thickness(0) };
                        foreach (var child in item)
                        {
                            var b = RenderBlock(child);
                            if (b != null) li.Blocks.Add(b);
                        }
                        if (li.Blocks.Count == 0) li.Blocks.Add(new Paragraph());
                        wl.ListItems.Add(li);
                    }
                    return wl;
                }
            case QuoteBlock quote:
                {
                    var p = new Paragraph { Foreground = QuoteFg, Margin = new Thickness(16, 0, 0, 6) };
                    foreach (var child in quote)
                        if (child is ParagraphBlock qpara)
                            AddInlines(p.Inlines, qpara.Inline);
                    return p;
                }
            case ThematicBreakBlock:
                return new Paragraph(new Run("────────────────")) { Foreground = QuoteFg, Margin = new Thickness(0, 4, 0, 4) };
            default:
                {
                    if (block is LeafBlock leaf)
                    {
                        var text = leaf.Lines.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                            return new Paragraph(new Run(text)) { Margin = new Thickness(0, 0, 0, 4) };
                    }
                    return null;
                }
        }
    }

    private static void AddInlines(InlineCollection target, ContainerInline? inlines)
    {
        if (inlines == null) return;
        foreach (var inline in inlines)
        {
            var result = RenderInline(inline);
            if (result != null) target.Add(result);
        }
    }

    private static WpfInline? RenderInline(Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());

            case LineBreakInline lb:
                return lb.IsHard ? new LineBreak() : new Run(" ");

            case CodeInline code:
                return new Run(code.Content) { FontFamily = MonoFont, Foreground = InlineCodeFg, FontSize = 12 };

            case EmphasisInline emphasis:
                {
                    if (emphasis.DelimiterCount >= 2)
                    {
                        var bold = new Bold();
                        AddInlines(bold.Inlines, emphasis);
                        return bold;
                    }
                    var italic = new Italic();
                    AddInlines(italic.Inlines, emphasis);
                    return italic;
                }

            case LinkInline link:
                {
                    var hl = new Hyperlink { Foreground = LinkFg };
                    AddInlines(hl.Inlines, link);
                    if (hl.Inlines.Count == 0) hl.Inlines.Add(new Run(link.Url ?? ""));
                    if (!string.IsNullOrEmpty(link.Url) && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
                    {
                        hl.NavigateUri = uri;
                        hl.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                    }
                    return hl;
                }

            case AutolinkInline autolink:
                {
                    var hl = new Hyperlink(new Run(autolink.Url)) { Foreground = LinkFg };
                    if (Uri.TryCreate(autolink.Url, UriKind.Absolute, out var uri))
                    {
                        hl.NavigateUri = uri;
                        hl.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                    }
                    return hl;
                }

            case ContainerInline container:
                {
                    var span = new Span();
                    AddInlines(span.Inlines, container);
                    return span.Inlines.Count > 0 ? span : null;
                }

            default:
                return null;
        }
    }

    private static string GetCodeText(LeafBlock block)
    {
        var lines = block.Lines;
        if (lines.Count == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines.Lines[i].Slice.ToString());
        }
        return sb.ToString();
    }
}
