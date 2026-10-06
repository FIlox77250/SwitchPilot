using System.Text;
using System.Text.RegularExpressions;

namespace SwitchPilot.Core.Text;

public abstract record MdBlock;
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Content) : MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Content) : MdBlock;
public sealed record MdList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock;
public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;
public sealed record MdCodeBlock(string Code) : MdBlock;
public sealed record MdRule : MdBlock;
public enum MdAlign { Left, Center, Right }
public sealed record MdTable(IReadOnlyList<MdAlign> Align, IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;

public abstract record MdInline;
public sealed record MdText(string Text) : MdInline;
public sealed record MdStrong(IReadOnlyList<MdInline> Content) : MdInline;
public sealed record MdEmphasis(IReadOnlyList<MdInline> Content) : MdInline;
public sealed record MdStrike(IReadOnlyList<MdInline> Content) : MdInline;
public sealed record MdCode(string Code) : MdInline;
/// <summary>Link; <see cref="Url"/> is only http(s) or null for a target that must not be opened.</summary>
public sealed record MdLink(string? Url, IReadOnlyList<MdInline> Content) : MdInline;
public sealed record MdLineBreak : MdInline;

/// <summary>
/// The Markdown subset GitHub release notes use (headings, paragraphs, nested lists, quotes,
/// fenced code, rules, pipe tables, bold/italic/strike, code spans, links, autolinks). Like
/// GitHub release pages, a single newline inside a paragraph is a line break. Raw HTML is shown
/// as text except &lt;br&gt;. Never throws: anything unrecognised stays literal text.
/// </summary>
public static class Markdown
{
    private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Rule = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.CultureInvariant);
    private static readonly Regex Quote = new(@"^ {0,3}> ?", RegexOptions.CultureInvariant);
    private static readonly Regex Item = new(@"^( *)([-*+]|(\d{1,9})[.)])( +|$)", RegexOptions.CultureInvariant);
    private static readonly Regex TableSeparator = new(@"^ *\|? *:?-+:? *(?:\| *:?-+:? *)*\|? *$", RegexOptions.CultureInvariant);
    private static readonly Regex Break = new(@"^<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string Escapable = @"\`*_{}[]()#+-.!|~<>""'";

    public static IReadOnlyList<MdBlock> Parse(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');
        return Blocks(lines);
    }

    // ---- Blocks ----------------------------------------------------------------------------

    private static List<MdBlock> Blocks(IReadOnlyList<string> lines)
    {
        var blocks = new List<MdBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            if (Fence.Match(line) is { Success: true } fence)
            {
                var marker = fence.Groups[1].Value;
                var code = new List<string>();
                for (i++; i < lines.Count && !(lines[i].TrimStart().StartsWith(marker[..3], StringComparison.Ordinal) && lines[i].Trim().Trim(marker[0]).Length == 0); i++)
                    code.Add(lines[i]);
                i++;
                blocks.Add(new MdCodeBlock(string.Join("\n", code)));
            }
            else if (Heading.Match(line) is { Success: true } heading)
            {
                blocks.Add(new MdHeading(heading.Groups[1].Length, Inlines(heading.Groups[2].Value)));
                i++;
            }
            else if (Rule.IsMatch(line))
            {
                blocks.Add(new MdRule());
                i++;
            }
            else if (Quote.IsMatch(line))
            {
                var quoted = new List<string>();
                for (; i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]); i++)
                {
                    if (Quote.Match(lines[i]) is { Success: true } q) quoted.Add(lines[i][q.Length..]);
                    else if (StartsBlock(lines[i])) break;
                    else quoted.Add(lines[i]);
                }
                blocks.Add(new MdQuote(Blocks(quoted)));
            }
            else if (IsTableStart(lines, i))
            {
                var header = Cells(line);
                var align = Cells(lines[i + 1]).Select(c => c.StartsWith(':') && c.EndsWith(':') && c.Length > 1 ? MdAlign.Center : c.EndsWith(':') ? MdAlign.Right : MdAlign.Left).ToList();
                var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
                for (i += 2; i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].Contains('|') && !StartsBlock(lines[i]); i++)
                {
                    var cells = Cells(lines[i]);
                    rows.Add(Enumerable.Range(0, header.Count).Select(c => (IReadOnlyList<MdInline>)Inlines(c < cells.Count ? cells[c] : "")).ToList());
                }
                while (align.Count < header.Count) align.Add(MdAlign.Left);
                blocks.Add(new MdTable(align.Take(header.Count).ToList(), header.Select(h => (IReadOnlyList<MdInline>)Inlines(h)).ToList(), rows));
            }
            else if (Item.Match(line) is { Success: true } item)
            {
                i = List(lines, i, item, blocks);
            }
            else
            {
                var paragraph = new List<string> { line.Trim() };
                for (i++; i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]); i++)
                {
                    var next = lines[i].Trim();
                    // Setext headings: "Title" underlined with === or ---.
                    if (Regex.IsMatch(next, "^(=+|-+)$"))
                    {
                        blocks.Add(new MdHeading(next[0] == '=' ? 1 : 2, Inlines(string.Join("\n", paragraph))));
                        paragraph.Clear();
                        i++;
                        break;
                    }
                    if (StartsBlock(lines[i])) break;
                    paragraph.Add(next);
                }
                if (paragraph.Count > 0) blocks.Add(new MdParagraph(Inlines(string.Join("\n", paragraph))));
            }
        }
        return blocks;
    }

    private static bool StartsBlock(string line) =>
        Fence.IsMatch(line) || Heading.IsMatch(line) || Rule.IsMatch(line) || Quote.IsMatch(line) || Item.IsMatch(line);

    private static bool IsTableStart(IReadOnlyList<string> lines, int i) =>
        i + 1 < lines.Count && lines[i].Contains('|') && TableSeparator.IsMatch(lines[i + 1]) && lines[i + 1].Contains('-')
        && (lines[i + 1].Contains('|') || Cells(lines[i]).Count == 1);

    /// <summary>Consumes consecutive items of one list; returns the next unread line.</summary>
    private static int List(IReadOnlyList<string> lines, int i, Match first, List<MdBlock> blocks)
    {
        var ordered = first.Groups[3].Success;
        var start = ordered && int.TryParse(first.Groups[3].Value, out var n) ? n : 1;
        var items = new List<IReadOnlyList<MdBlock>>();
        while (i < lines.Count && Item.Match(lines[i]) is { Success: true } m && m.Groups[3].Success == ordered)
        {
            // Content column: after the marker and its spaces (one space when the item starts with indented code-like padding).
            var spaces = m.Groups[4].Value.Length;
            var column = m.Groups[1].Length + m.Groups[2].Length + (spaces is 0 or > 4 ? 1 : spaces);
            var content = new List<string> { lines[i].Length > m.Length ? lines[i][m.Length..] : "" };
            for (i++; i < lines.Count; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    var next = i + 1;
                    while (next < lines.Count && string.IsNullOrWhiteSpace(lines[next])) next++;
                    if (next < lines.Count && Indent(lines[next]) >= column) { content.Add(""); continue; }
                    break;
                }
                if (Indent(line) >= column) content.Add(line[column..]);
                else if (StartsBlock(line)) break;
                else content.Add(line.Trim()); // Lazy continuation of the item's paragraph.
            }
            items.Add(Blocks(content));
            // Blank lines between two items of the same list.
            var after = i;
            while (after < lines.Count && string.IsNullOrWhiteSpace(lines[after])) after++;
            if (after > i && after < lines.Count && Item.Match(lines[after]) is { Success: true } sibling && sibling.Groups[3].Success == ordered) i = after;
        }
        blocks.Add(new MdList(ordered, start, items));
        return i;
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    /// <summary>Pipe-table cells; "\|" and pipes inside code spans do not split.</summary>
    private static List<string> Cells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text[..^1];
        var cells = new List<string>();
        var cell = new StringBuilder();
        var code = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '|') { cell.Append('|'); i++; }
            else if (c == '`') { code = !code; cell.Append(c); }
            else if (c == '|' && !code) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    // ---- Inlines ---------------------------------------------------------------------------

    public static IReadOnlyList<MdInline> Inlines(string text)
    {
        var result = new List<MdInline>();
        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length == 0) return;
            if (result.Count > 0 && result[^1] is MdText previous) result[^1] = new MdText(previous.Text + buffer);
            else result.Add(new MdText(buffer.ToString()));
            buffer.Clear();
        }
        void Add(MdInline inline) { Flush(); result.Add(inline); }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && Escapable.Contains(text[i + 1])) { buffer.Append(text[i + 1]); i += 2; continue; }
            if (c == '\n') { Add(new MdLineBreak()); i++; continue; }
            if (c == '`' && CodeSpan(text, i) is var (code, codeEnd) && codeEnd > 0) { Add(new MdCode(code)); i = codeEnd; continue; }
            if ((c == '*' || c == '_') && Emphasis(text, i) is var (emphasis, emphasisEnd) && emphasis is not null) { Add(emphasis); i = emphasisEnd; continue; }
            if (c == '~' && Delimited(text, i, "~~") is var (strike, strikeEnd) && strike is not null) { Add(new MdStrike(Inlines(strike))); i = strikeEnd; continue; }
            if ((c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '[')) && Link(text, i) is var (link, linkEnd) && link is not null) { Add(link); i = linkEnd; continue; }
            if (c == '<')
            {
                if (Break.Match(text[i..]) is { Success: true } br) { Add(new MdLineBreak()); i += br.Length; continue; }
                var close = text.IndexOf('>', i);
                if (close > i && SafeUrl(text[(i + 1)..close]) is { } auto) { Add(new MdLink(auto, [new MdText(auto)])); i = close + 1; continue; }
            }
            if ((c == 'h' || c == 'H') && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && BareUrl(text, i) is { } bare)
            {
                Add(new MdLink(bare, [new MdText(bare)]));
                i += bare.Length;
                continue;
            }
            buffer.Append(c);
            i++;
        }
        Flush();
        return result;
    }

    private static (string Code, int End) CodeSpan(string text, int i)
    {
        var run = 0;
        while (i + run < text.Length && text[i + run] == '`') run++;
        var fence = new string('`', run);
        for (var j = text.IndexOf(fence, i + run, StringComparison.Ordinal); j >= 0; j = text.IndexOf(fence, j + 1, StringComparison.Ordinal))
        {
            var end = j + run;
            if (end < text.Length && text[end] == '`') { j = end; while (j + 1 < text.Length && text[j + 1] == '`') j++; continue; }
            var code = text[(i + run)..j].Replace('\n', ' ');
            if (code.Length > 1 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
            return (code, end);
        }
        return ("", 0);
    }

    private static (MdInline? Inline, int End) Emphasis(string text, int i)
    {
        var c = text[i];
        var run = 0;
        while (i + run < text.Length && text[i + run] == c) run++;
        // "_" inside a word (snake_case, port_overrides) is never emphasis.
        if (c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1])) return (null, 0);
        foreach (var size in run >= 3 ? new[] { 3, 2, 1 } : run == 2 ? new[] { 2, 1 } : new[] { 1 })
        {
            var marker = new string(c, size);
            if (Delimited(text, i, marker) is not ({ } inner, var end)) continue;
            if (c == '_' && end < text.Length && char.IsLetterOrDigit(text[end])) continue;
            var content = Inlines(inner);
            return (size switch { 3 => new MdStrong([new MdEmphasis(content)]), 2 => new MdStrong(content), _ => new MdEmphasis(content) }, end);
        }
        return (null, 0);
    }

    /// <summary>Text between <paramref name="marker"/> at <paramref name="i"/> and its closing twin; skips code spans, links and longer runs.</summary>
    private static (string? Inner, int End) Delimited(string text, int i, string marker)
    {
        if (string.CompareOrdinal(text, i, marker, 0, marker.Length) != 0) return (null, 0);
        var open = i + marker.Length;
        if (open >= text.Length || char.IsWhiteSpace(text[open])) return (null, 0);
        var c = marker[0];
        for (var j = open; j < text.Length; j++)
        {
            if (text[j] == '\\') { j++; continue; }
            if (text[j] == '`' && CodeSpan(text, j) is var (_, codeEnd) && codeEnd > 0) { j = codeEnd - 1; continue; }
            if (text[j] != c) continue;
            var run = 0;
            while (j + run < text.Length && text[j + run] == c) run++;
            // A closing run must match exactly ("**" does not close on "*", "*" skips over "**…**").
            if (run == marker.Length && j > open && !char.IsWhiteSpace(text[j - 1])) return (text[open..j], j + run);
            if (run == 3 && marker.Length < 3 && j > open && !char.IsWhiteSpace(text[j - 1]))
                return (text[open..j] + new string(c, 3 - marker.Length), j + 3); // "*a **b***": the inner run closes first.
            j += run - 1;
        }
        return (null, 0);
    }

    private static (MdInline? Inline, int End) Link(string text, int i)
    {
        var image = text[i] == '!';
        var open = image ? i + 1 : i;
        var depth = 0;
        var close = -1;
        for (var j = open; j < text.Length; j++)
        {
            if (text[j] == '\\') { j++; continue; }
            if (text[j] == '[') depth++;
            else if (text[j] == ']' && --depth == 0) { close = j; break; }
        }
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return (null, 0);
        depth = 0;
        var end = -1;
        for (var j = close + 1; j < text.Length; j++)
        {
            if (text[j] == '(') depth++;
            else if (text[j] == ')' && --depth == 0) { end = j; break; }
        }
        if (end < 0) return (null, 0);
        var target = text[(close + 2)..end].Trim();
        // Optional title: [text](url "title").
        var space = target.IndexOfAny([' ', '\n']);
        if (space > 0) target = target[..space];
        target = target.Trim('<', '>');
        var label = text[(open + 1)..close];
        var content = image && label.Length == 0 ? [new MdText("image")] : Inlines(label);
        return (new MdLink(SafeUrl(target), content), end + 1);
    }

    private static string? BareUrl(string text, int i)
    {
        var m = Regex.Match(text[i..], @"^https?://[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        var url = m.Value;
        // Trailing punctuation belongs to the sentence; a closing parenthesis only when unbalanced.
        while (url.Length > 0 && (".,;:!?*_~'".Contains(url[^1]) || (url[^1] == ')' && url.Count(ch => ch == ')') > url.Count(ch => ch == '('))))
            url = url[..^1];
        return SafeUrl(url);
    }

    /// <summary>Absolute http(s) URL, or null: release notes must not launch files, scripts or other protocols.</summary>
    public static string? SafeUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0
            ? uri.OriginalString
            : null;

    /// <summary>Plain text of inlines (tests, accessibility, copy).</summary>
    public static string PlainText(IEnumerable<MdInline> inlines) => string.Concat(inlines.Select(inline => inline switch
    {
        MdText t => t.Text,
        MdCode c => c.Code,
        MdStrong s => PlainText(s.Content),
        MdEmphasis e => PlainText(e.Content),
        MdStrike s => PlainText(s.Content),
        MdLink l => PlainText(l.Content),
        MdLineBreak => "\n",
        _ => ""
    }));
}
