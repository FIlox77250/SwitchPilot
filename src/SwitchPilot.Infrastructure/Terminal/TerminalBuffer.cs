using System.Text;

namespace SwitchPilot.Infrastructure.Terminal;

// Incremental terminal decoding: work is proportional to bytes received, not transcript size.
// Keep the transcript only once. Prompt recognition uses a bounded tail.
internal sealed class TerminalBuffer
{
    private readonly StringBuilder text = new();
    private int escapeState, received;
    private bool pagerLatched;
    private const int MaxReceived = 8 * 1024 * 1024;
    public string Tail => text.ToString(Math.Max(0, text.Length - 512), Math.Min(512, text.Length));
    public override string ToString() => text.ToString();

    // Pager signatures. Cisco IOS uses "--More--"; the AT-S95 / Cisco-SB / VCLI family prints a
    // "More: <space>, Quit: q…" line (sometimes followed by a spinning character); other devices
    // say "Press any key" or "(q)uit"; Huawei prints "---- More ----", Junos "---(more 42%)---" and
    // RouterOS "-- [Q quit|D dump|down]". A space advances all of them. Matching is
    // case-insensitive and confined to the current line.
    private static readonly string[] PagerPatterns = ["--more--", "-- more --", "more:", "press any key", "(q)uit", "---(more", "[q quit"];

    public void Append(string chunk, Action nextPage)
    {
        if (chunk.Length > MaxReceived - received) throw new CliProtocolException("Réponse CLI trop volumineuse ; session arrêtée.");
        received += chunk.Length;
        foreach (var c in chunk)
        {
            if (escapeState != 0)
            {
                escapeState = escapeState switch
                {
                    1 => c == '[' ? 2 : c == ']' ? 3 : 0,
                    2 => c is >= '@' and <= '~' ? 0 : 2,
                    3 => c == '\a' ? 0 : c == '\x1b' ? 4 : 3,
                    4 => c == '\\' ? 0 : 3,
                    _ => 0
                };
                continue;
            }
            if (c == '\x1b') { escapeState = 1; continue; }
            if (c == '\b')
            {
                if (text.Length > 0 && text[^1] != '\n') text.Length--;
                // A backspace can erase the pager line; re-arm the latch when it no longer matches.
                if (pagerLatched && !PagerOnCurrentLine()) pagerLatched = false;
                continue;
            }
            if (c is '\r' or '\x7f' || c < ' ' && c is not ('\n' or '\t')) continue;
            text.Append(c);
            if (c == '\n') { pagerLatched = false; continue; }
            if (c == '-' && EndsWith("--More--"))
            {
                // IOS erases the pager using backspaces; retain blanks for those erasures.
                text.Length -= 8; text.Append(' ', 8);
                nextPage();
                continue;
            }
            // Generic pagers: fire once per appearance; the latch releases on a new line or when
            // the device erases the pager, so an echoed key cannot double-advance a page.
            if (!pagerLatched && PagerOnCurrentLine())
            {
                pagerLatched = true;
                nextPage();
            }
            else if (pagerLatched && !PagerOnCurrentLine())
            {
                pagerLatched = false;
            }
        }
    }

    private bool PagerOnCurrentLine()
    {
        // Current line = text after the last '\n', scanning back at most 96 chars.
        var lineStart = text.Length;
        var scanned = 0;
        while (lineStart > 0 && scanned < 96)
        {
            if (text[lineStart - 1] == '\n') break;
            lineStart--; scanned++;
        }
        if (lineStart >= text.Length) return false;
        var line = text.ToString(lineStart, text.Length - lineStart);
        foreach (var pattern in PagerPatterns)
            if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool EndsWith(string value)
    {
        if (text.Length < value.Length) return false;
        for (var i = 0; i < value.Length; i++) if (text[text.Length - value.Length + i] != value[i]) return false;
        return true;
    }
}
