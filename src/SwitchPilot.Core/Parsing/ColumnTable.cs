using System.Text.RegularExpressions;

namespace SwitchPilot.Core.Parsing;

/// <summary>
/// Fixed-width CLI table ("Port  Name  Status …" over a dashed underline). Columns come from
/// the dashed separator when it has one dash run per column, otherwise from the positions of
/// the header words. Each whitespace-separated token of a row is assigned to the column whose
/// span contains its first character (nearest column otherwise), so values wider than their
/// underline and empty cells are both handled.
/// </summary>
public sealed class ColumnTable
{
    private readonly (int Start, int End)[] spans;
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; }

    private ColumnTable(string[] columns, (int, int)[] spans, IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    { Columns = columns; this.spans = spans; Rows = rows; }

    private static readonly Regex DashRun = new(@"-+");
    private static bool IsSeparator(string line) => line.Trim().Length > 0 && Regex.IsMatch(line, @"^[\s\-+=]+$");

    /// <summary>
    /// Parses the first table of <paramref name="output"/>. <paramref name="headerStart"/> is the
    /// first header word (for example "Port"); it locates tables without a usable underline.
    /// Returns null when no table is found.
    /// </summary>
    public static ColumnTable? Parse(string output, string headerStart)
    {
        var lines = Cisco.CiscoParser.Clean(output).Split('\n').Select(l => l.Replace('\t', ' ').TrimEnd()).ToArray();
        var headerIndex = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^\s*" + Regex.Escape(headerStart) + @"\b", RegexOptions.IgnoreCase));
        if (headerIndex < 0) return null;

        // Dashed underline with one run per column, just below the header (possibly two header lines).
        (int Start, int End)[]? spans = null;
        var bodyStart = headerIndex + 1;
        for (var i = headerIndex + 1; i < Math.Min(lines.Length, headerIndex + 3); i++)
        {
            if (!IsSeparator(lines[i])) continue;
            var runs = DashRun.Matches(lines[i]).Select(m => (m.Index, m.Index + m.Length)).ToArray();
            if (runs.Length >= 2) spans = runs;
            bodyStart = i + 1;
            break;
        }
        string[] columns;
        if (spans is not null)
        {
            var headers = new List<string>();
            for (var i = headerIndex; i < bodyStart - 1; i++) headers.Add(lines[i]);
            // A first header line above "headerStart" (EdgeSwitch prints two-line headers).
            if (headerIndex > 0 && lines[headerIndex - 1].Trim().Length > 0 && !IsSeparator(lines[headerIndex - 1])) headers.Insert(0, lines[headerIndex - 1]);
            columns = spans.Select((span, c) => string.Join(' ', headers.Select(h => Cell(h, spans, c)).Where(t => t.Length > 0))).ToArray();
        }
        else
        {
            var words = Regex.Matches(lines[headerIndex], @"\S+").ToArray();
            spans = words.Select((w, i) => (w.Index, i + 1 < words.Length ? words[i + 1].Index - 1 : int.MaxValue)).ToArray();
            columns = words.Select(w => w.Value).ToArray();
        }
        for (var c = 0; c < columns.Length; c++) if (columns[c].Length == 0) columns[c] = $"#{c}";

        var rows = new List<IReadOnlyDictionary<string, string>>();
        for (var i = bodyStart; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || IsSeparator(line)) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match token in Regex.Matches(line, @"\S+"))
            {
                var column = Nearest(spans, token.Index);
                row[columns[column]] = row.TryGetValue(columns[column], out var existing) ? existing + " " + token.Value : token.Value;
            }
            foreach (var name in columns) row.TryAdd(name, "");
            rows.Add(row);
        }
        return new(columns, spans, rows);
    }

    private static string Cell(string line, (int Start, int End)[] spans, int column)
    {
        var parts = Regex.Matches(line, @"\S+").Where(m => Nearest(spans, m.Index) == column).Select(m => m.Value);
        return string.Join(' ', parts);
    }

    private static int Nearest((int Start, int End)[] spans, int position)
    {
        var best = 0; var distance = int.MaxValue;
        for (var c = 0; c < spans.Length; c++)
        {
            var (start, end) = spans[c];
            var d = position < start ? start - position : position >= end ? position - end + 1 : 0;
            if (d == 0) return c;
            // Ties go to the left column: left-aligned values overflow to the right.
            if (d < distance || d == distance && position >= end) { best = c; distance = d; }
        }
        return best;
    }
}
