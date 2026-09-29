using System.Text;

namespace SwitchPilot.Infrastructure.Ssh;

// Incremental terminal decoding: work is proportional to bytes received, not transcript size.
// Keep the transcript only once. Prompt recognition uses a bounded tail.
internal sealed class TerminalBuffer
{
    private readonly StringBuilder text = new();
    private int escapeState, received;
    private const int MaxReceived = 8 * 1024 * 1024;
    public string Tail => text.ToString(Math.Max(0, text.Length - 512), Math.Min(512, text.Length));
    public override string ToString() => text.ToString();

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
            if (c == '\b') { if (text.Length > 0 && text[^1] != '\n') text.Length--; continue; }
            if (c is '\r' or '\x7f' || c < ' ' && c is not ('\n' or '\t')) continue;
            text.Append(c);
            if (c == '-' && EndsWith("--More--"))
            {
                // IOS erases the pager using backspaces; retain blanks for those erasures.
                text.Length -= 8; text.Append(' ', 8); nextPage();
            }
        }
    }
    private bool EndsWith(string value)
    {
        if (text.Length < value.Length) return false;
        for (var i = 0; i < value.Length; i++) if (text[text.Length - value.Length + i] != value[i]) return false;
        return true;
    }
}
