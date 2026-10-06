using System.Text.RegularExpressions;

namespace SwitchPilot.Core.Parsing;

/// <summary>One record produced by a <see cref="TextFsm"/> template.</summary>
public sealed class TextFsmRecord(IReadOnlyDictionary<string, object> values)
{
    /// <summary>Scalar value ("" when absent); a List value is joined with spaces.</summary>
    public string this[string name] => values.TryGetValue(name, out var v) ? v as string ?? string.Join(' ', (IEnumerable<string>)v) : "";
    public IReadOnlyList<string> List(string name) => values.TryGetValue(name, out var v)
        ? v as IReadOnlyList<string> ?? (v is string s && s.Length > 0 ? [s] : []) : [];
}

/// <summary>
/// Minimal TextFSM engine (the template language of Google TextFSM / ntc-templates), so vendor
/// output can be described declaratively instead of hand-written loops. Supported:
/// <list type="bullet">
/// <item>Value options Filldown, Required, List, Key, Fillup;</item>
/// <item>states (Start mandatory, EOF and End reserved), rules "^regex -> Action";</item>
/// <item>line actions Next / Continue, record actions Record / NoRecord / Clear / Clearall,
/// state transitions and Error ["message"];</item>
/// <item>${NAME} substitution and $$ for an end-of-line anchor.</item>
/// </list>
/// Templates are compiled once and are immutable: <see cref="Parse"/> is thread-safe.
/// </summary>
public sealed class TextFsm
{
    private sealed record ValueDef(string Name, string Pattern, bool Filldown, bool Required, bool List, bool Fillup);
    private enum LineAction { Next, Continue }
    private enum RecordAction { NoRecord, Record, Clear, Clearall }
    private sealed record Rule(Regex Pattern, LineAction Line, RecordAction Record, string? NewState, string? Error, IReadOnlyList<string> Names);

    private readonly IReadOnlyList<ValueDef> values;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Rule>> states;
    private static readonly Regex ValueLine = new(@"^Value\s+(?:(?<opts>[A-Za-z]+(?:,[A-Za-z]+)*)\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+(?<re>\(.*\))\s*$");
    // "Next.Record State", "Record State", "Continue" or a bare "State" (implicit Next.NoRecord).
    private static readonly Regex ActionText = new(@"^(?:(?:(?<line>Next|Continue)(?:\.(?<rec>Record|NoRecord|Clear|Clearall))?|(?<rec>Record|NoRecord|Clear|Clearall))(?:\s+(?<state>[A-Za-z_][A-Za-z0-9_]*))?|(?<state>[A-Za-z_][A-Za-z0-9_]*))?$");

    public IReadOnlyList<string> Header => values.Select(v => v.Name).ToArray();

    public TextFsm(string template)
    {
        var lines = template.Replace("\r", "").Split('\n');
        var defs = new List<ValueDef>();
        var index = 0;
        for (; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith('#')) continue;
            if (line.Trim().Length == 0) { if (defs.Count > 0) break; continue; }
            var m = ValueLine.Match(line.Trim());
            if (!m.Success) throw new FormatException($"Modèle TextFSM : ligne Value invalide « {line.Trim()} ».");
            var options = m.Groups["opts"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var option in options)
                if (option is not ("Filldown" or "Required" or "List" or "Key" or "Fillup"))
                    throw new FormatException($"Modèle TextFSM : option « {option} » inconnue.");
            var name = m.Groups["name"].Value;
            if (defs.Any(d => d.Name == name)) throw new FormatException($"Modèle TextFSM : valeur « {name} » dupliquée.");
            var re = m.Groups["re"].Value;
            _ = new Regex(re); // Validates the value pattern on its own.
            defs.Add(new(name, re[1..^1], options.Contains("Filldown"), options.Contains("Required"), options.Contains("List"), options.Contains("Fillup")));
        }
        if (defs.Count == 0) throw new FormatException("Modèle TextFSM : aucune Value déclarée.");
        values = defs;

        var parsed = new Dictionary<string, List<Rule>>();
        List<Rule>? current = null;
        for (; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (!char.IsWhiteSpace(line[0]))
            {
                var state = line.Trim();
                if (!Regex.IsMatch(state, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new FormatException($"Modèle TextFSM : état « {state} » invalide.");
                if (parsed.ContainsKey(state)) throw new FormatException($"Modèle TextFSM : état « {state} » dupliqué.");
                parsed[state] = current = [];
                continue;
            }
            if (current is null) throw new FormatException("Modèle TextFSM : règle hors d'un état.");
            current.Add(ParseRule(line.Trim()));
        }
        if (!parsed.ContainsKey("Start")) throw new FormatException("Modèle TextFSM : état Start manquant.");
        foreach (var rule in parsed.Values.SelectMany(r => r))
            if (rule.NewState is { } target && target is not ("End" or "EOF") && !parsed.ContainsKey(target))
                throw new FormatException($"Modèle TextFSM : état cible « {target} » inconnu.");
        states = parsed.ToDictionary(p => p.Key, p => (IReadOnlyList<Rule>)p.Value);
    }

    private Rule ParseRule(string text)
    {
        if (!text.StartsWith('^')) throw new FormatException($"Modèle TextFSM : une règle commence par « ^ » ({text}).");
        string match = text, action = "";
        var arrow = text.LastIndexOf(" ->", StringComparison.Ordinal);
        if (arrow >= 0) { match = text[..arrow]; action = text[(arrow + 3)..].Trim(); }

        var names = new List<string>();
        var pattern = Regex.Replace(match, @"\$\$|\$\{(?<n>[A-Za-z_][A-Za-z0-9_]*)\}", m =>
        {
            if (m.Value == "$$") return "$";
            var name = m.Groups["n"].Value;
            var def = values.FirstOrDefault(v => v.Name == name) ?? throw new FormatException($"Modèle TextFSM : valeur « {name} » non déclarée.");
            names.Add(name);
            return $"(?<{name}>{def.Pattern})";
        });
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);

        if (action.StartsWith("Error", StringComparison.Ordinal))
        {
            var message = action[5..].Trim().Trim('"');
            return new(regex, LineAction.Next, RecordAction.NoRecord, null, message.Length == 0 ? "Erreur de modèle TextFSM." : message, names);
        }
        var a = ActionText.Match(action);
        if (!a.Success) throw new FormatException($"Modèle TextFSM : action « {action} » invalide.");
        var line = a.Groups["line"].Value == "Continue" ? LineAction.Continue : LineAction.Next;
        var record = a.Groups["rec"].Value switch
        {
            "Record" => RecordAction.Record, "Clear" => RecordAction.Clear, "Clearall" => RecordAction.Clearall, _ => RecordAction.NoRecord
        };
        var state = a.Groups["state"].Success ? a.Groups["state"].Value : null;
        if (line == LineAction.Continue && state is not null)
            throw new FormatException("Modèle TextFSM : « Continue » ne peut pas changer d'état.");
        return new(regex, line, record, state, null, names);
    }

    public IReadOnlyList<TextFsmRecord> Parse(string text)
    {
        var results = new List<Dictionary<string, object>>();
        var row = new Dictionary<string, object>();
        var state = "Start";

        void Assign(ValueDef def, string value)
        {
            if (def.List)
            {
                if (!row.TryGetValue(def.Name, out var existing) || existing is not List<string> list) row[def.Name] = list = [];
                list.Add(value);
                return;
            }
            row[def.Name] = value;
            if (!def.Fillup) return;
            for (var i = results.Count - 1; i >= 0; i--)
            {
                if (results[i].TryGetValue(def.Name, out var old) && old is string { Length: > 0 }) break;
                results[i][def.Name] = value;
            }
        }
        void Clear(bool all)
        {
            foreach (var def in values)
                if (all || !def.Filldown) row.Remove(def.Name);
                else if (def.List && row.TryGetValue(def.Name, out var v) && v is List<string> l) row[def.Name] = new List<string>(l);
        }
        static bool Empty(object? v) => v is null || v is string { Length: 0 } || v is List<string> { Count: 0 };
        void Record()
        {
            foreach (var def in values)
                if (def.Required && Empty(row.GetValueOrDefault(def.Name))) { Clear(false); return; }
            if (values.All(def => Empty(row.GetValueOrDefault(def.Name)))) return;
            var snapshot = new Dictionary<string, object>();
            foreach (var def in values)
                snapshot[def.Name] = row.GetValueOrDefault(def.Name) switch
                {
                    List<string> list => (object)list.ToArray(),
                    string s => s,
                    _ => def.List ? Array.Empty<string>() : ""
                };
            results.Add(snapshot);
            Clear(false);
        }

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (state == "End") break;
            foreach (var rule in states[state])
            {
                var m = rule.Pattern.Match(raw);
                if (!m.Success) continue;
                foreach (var name in rule.Names)
                    if (m.Groups[name].Success) Assign(values.First(v => v.Name == name), m.Groups[name].Value);
                if (rule.Error is not null) throw new FormatException(rule.Error);
                switch (rule.Record)
                {
                    case RecordAction.Record: Record(); break;
                    case RecordAction.Clear: Clear(false); break;
                    case RecordAction.Clearall: Clear(true); break;
                }
                if (rule.NewState is not null) state = rule.NewState;
                if (rule.Line == LineAction.Next) break;
            }
        }
        // Implicit EOF: record what is pending, unless the template declares its own EOF state
        // (TextFSM convention: an explicit empty EOF state suppresses the final record).
        if (state != "End" && !states.ContainsKey("EOF")) Record();
        return results.Select(r => new TextFsmRecord(r)).ToArray();
    }
}
