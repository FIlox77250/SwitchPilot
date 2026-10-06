using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SwitchPilot.Core.Text;

namespace SwitchPilot.App.Services;

/// <summary>
/// Read-only Markdown view (release notes). Text stays selectable; links open in the browser
/// and only for http(s) targets.
/// </summary>
public sealed class MarkdownViewer : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownViewer),
        new PropertyMetadata("", (d, _) => ((MarkdownViewer)d).Render()));

    private static readonly Brush CodeBackground = Frozen(Color.FromRgb(0xEE, 0xF1, 0xF3));

    public MarkdownViewer()
    {
        IsToolBarVisible = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        // Inherited font and theme resources are only known once in the window.
        Loaded += (_, _) => Render();
    }

    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void Render()
    {
        var document = new FlowDocument
        {
            FontFamily = FontFamily,
            FontSize = FontSize,
            Foreground = Resource("Ink", Brushes.Black),
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };
        foreach (var block in Blocks(Core.Text.Markdown.Parse(Markdown), nested: false)) document.Blocks.Add(block);
        if (document.Blocks.FirstBlock is { } first) first.Margin = new Thickness(first.Margin.Left, 0, first.Margin.Right, first.Margin.Bottom);
        Document = document;
    }

    private IEnumerable<Block> Blocks(IEnumerable<MdBlock> blocks, bool nested)
    {
        var after = nested ? 2 : 8;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MdHeading h:
                    var heading = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
                    heading.FontSize = FontSize * (h.Level switch { 1 => 1.45, 2 => 1.25, 3 => 1.1, _ => 1.0 });
                    heading.Inlines.AddRange(Inlines(h.Content));
                    yield return heading;
                    break;
                case MdParagraph p:
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, after) };
                    paragraph.Inlines.AddRange(Inlines(p.Content));
                    yield return paragraph;
                    break;
                case MdList l:
                    var list = new List
                    {
                        MarkerStyle = l.Ordered ? TextMarkerStyle.Decimal : nested ? TextMarkerStyle.Circle : TextMarkerStyle.Disc,
                        StartIndex = Math.Max(1, l.Start),
                        Padding = new Thickness(22, 0, 0, 0),
                        Margin = new Thickness(0, 0, 0, after)
                    };
                    foreach (var item in l.Items)
                    {
                        var listItem = new ListItem();
                        listItem.Blocks.AddRange(Blocks(item, nested: true).ToList());
                        if (listItem.Blocks.Count == 0) listItem.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 2) });
                        list.ListItems.Add(listItem);
                    }
                    yield return list;
                    break;
                case MdQuote q:
                    var quote = new Section
                    {
                        BorderBrush = Resource("Line", Brushes.LightGray),
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(12, 0, 0, 0),
                        Margin = new Thickness(0, 0, 0, after),
                        Foreground = Resource("Muted", Brushes.Gray)
                    };
                    quote.Blocks.AddRange(Blocks(q.Blocks, nested: true).ToList());
                    yield return quote;
                    break;
                case MdCodeBlock c:
                    var code = new Paragraph
                    {
                        FontFamily = TryFindResource("CodeFont") as FontFamily ?? new FontFamily("Consolas"),
                        FontSize = FontSize * 0.9,
                        Background = CodeBackground,
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 0, 0, after)
                    };
                    var lines = c.Code.Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) code.Inlines.Add(new LineBreak());
                        code.Inlines.Add(new Run(lines[i]));
                    }
                    yield return code;
                    break;
                case MdRule:
                    yield return new Paragraph
                    {
                        BorderBrush = Resource("Line", Brushes.LightGray),
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        Margin = new Thickness(0, 6, 0, 10),
                        FontSize = 1
                    };
                    break;
                case MdTable t:
                    yield return Table(t, after);
                    break;
            }
        }
    }

    private Table Table(MdTable t, int after)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, after) };
        // Columns sized by their longest cell, so short columns do not take a third of the width.
        for (var c = 0; c < t.Header.Count; c++)
        {
            var longest = new[] { t.Header[c] }.Concat(t.Rows.Select(r => r[c])).Max(cell => Core.Text.Markdown.PlainText(cell).Length);
            table.Columns.Add(new TableColumn { Width = new GridLength(Math.Clamp(longest, 6, 60), GridUnitType.Star) });
        }
        var group = new TableRowGroup();
        group.Rows.Add(Row(t.Header, t.Align, header: true));
        foreach (var row in t.Rows) group.Rows.Add(Row(row, t.Align, header: false));
        table.RowGroups.Add(group);
        return table;
    }

    private TableRow Row(IReadOnlyList<IReadOnlyList<MdInline>> cells, IReadOnlyList<MdAlign> align, bool header)
    {
        var row = new TableRow();
        for (var c = 0; c < cells.Count; c++)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = align[c] switch { MdAlign.Center => TextAlignment.Center, MdAlign.Right => TextAlignment.Right, _ => TextAlignment.Left } };
            if (header) paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.Inlines.AddRange(Inlines(cells[c]));
            row.Cells.Add(new TableCell(paragraph)
            {
                Padding = new Thickness(6, 5, 10, 5),
                BorderBrush = Resource("Line", Brushes.LightGray),
                BorderThickness = new Thickness(0, 0, 0, header ? 2 : 1)
            });
        }
        return row;
    }

    private List<Inline> Inlines(IEnumerable<MdInline> inlines)
    {
        var result = new List<Inline>();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText t: result.Add(new Run(t.Text)); break;
                case MdLineBreak: result.Add(new LineBreak()); break;
                case MdCode c:
                    result.Add(new Run(c.Code)
                    {
                        FontFamily = TryFindResource("CodeFont") as FontFamily ?? new FontFamily("Consolas"),
                        FontSize = FontSize * 0.92,
                        Background = CodeBackground
                    });
                    break;
                case MdStrong s:
                    var bold = new Bold();
                    bold.Inlines.AddRange(Inlines(s.Content));
                    result.Add(bold);
                    break;
                case MdEmphasis e:
                    var italic = new Italic();
                    italic.Inlines.AddRange(Inlines(e.Content));
                    result.Add(italic);
                    break;
                case MdStrike s:
                    var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                    strike.Inlines.AddRange(Inlines(s.Content));
                    result.Add(strike);
                    break;
                case MdLink { Url: null } l:
                    var plain = new Span();
                    plain.Inlines.AddRange(Inlines(l.Content));
                    result.Add(plain);
                    break;
                case MdLink l:
                    var link = new Hyperlink { NavigateUri = new Uri(l.Url), ToolTip = l.Url, Foreground = Resource("Accent", Brushes.SteelBlue) };
                    link.Inlines.AddRange(Inlines(l.Content));
                    link.RequestNavigate += (_, e) =>
                    {
                        e.Handled = true;
                        if (Core.Text.Markdown.SafeUrl(e.Uri.OriginalString) is not { } url) return;
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                        catch { /* The link is informational. */ }
                    };
                    result.Add(link);
                    break;
            }
        }
        return result;
    }
}
