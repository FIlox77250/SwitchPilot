using System.Text;
namespace SwitchPilot.Core.Discovery;
public record OutletEntry(string Switch, string Port, string Outlet);
public static class OutletCsv
{
    public static OutletEntry Validate(OutletEntry entry)
    {
        foreach (var value in new[] { entry.Switch, entry.Outlet })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl) || "=+-@".Contains(value.TrimStart()[0]))
                throw new ArgumentException("Nom de switch ou de prise invalide (256 caractères, sans contrôle ni formule de tableur).");
        return entry with { Switch = entry.Switch.Trim(), Port = CommandPlan.Interface(entry.Port), Outlet = entry.Outlet.Trim() };
    }
    public static string Export(IEnumerable<OutletEntry> entries)
    {
        static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        return "Switch,Port,Prise\r\n" + string.Join("\r\n", entries.Select(Validate).Select(e => string.Join(',', new[] { e.Switch, e.Port, e.Outlet }.Select(Quote)))) + "\r\n";
    }
    public static IReadOnlyList<OutletEntry> Import(string csv)
    {
        if (csv.Length > 4 * 1024 * 1024) throw new InvalidDataException("Inventaire trop volumineux.");
        var rows = new List<string[]>(); var row = new List<string>(); var field = new StringBuilder(); var quoted = false; var endedQuote = false;
        csv = csv.TrimStart('\ufeff');
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; } else { quoted = false; endedQuote = true; } }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0 && !endedQuote) quoted = true;
            else if (c is ',' or '\n' or '\r')
            {
                row.Add(field.ToString()); field.Clear(); endedQuote = false;
                if (c != ',') { if (row.Any(v => v.Length > 0)) rows.Add(row.ToArray()); row.Clear(); if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++; }
            }
            else { if (endedQuote || c == '"') throw new FormatException("CSV mal formé."); field.Append(c); }
            if (field.Length > 512 || row.Count > 3 || rows.Count > 10001) throw new InvalidDataException("Inventaire hors limites.");
        }
        if (quoted) throw new FormatException("Guillemet CSV non fermé.");
        if (field.Length > 0 || row.Count > 0 || endedQuote) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        if (rows.Count == 0 || !rows[0].SequenceEqual(new[] { "Switch", "Port", "Prise" })) throw new FormatException("En-tête CSV attendu : Switch,Port,Prise.");
        var entries = rows.Skip(1).Select(r => r.Length == 3 ? Validate(new(r[0], r[1], r[2])) : throw new FormatException("Trois colonnes CSV attendues.")).ToArray();
        if (entries.GroupBy(e => e.Switch.ToUpperInvariant() + "/" + e.Port).Any(g => g.Count() > 1)) throw new FormatException("Ports dupliqués dans le CSV. Corrigez l’inventaire avant import.");
        return entries;
    }
}
