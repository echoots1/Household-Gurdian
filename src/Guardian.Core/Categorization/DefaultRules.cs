using Guardian.Contracts;

namespace Guardian.Core.Categorization;

/// <summary>Parses lists/defaults.yaml (a deliberately tiny YAML subset) into rules. Editable without a rebuild.</summary>
public static class DefaultRules
{
    public static IReadOnlyList<Rule> Parse(string yaml)
    {
        // Format:
        // rules:
        //   - {match: process, pattern: steam, category: Gaming, priority: 20}
        // or block style:
        //   - match: domain
        //     pattern: khanacademy.org
        //     category: School
        var rules = new List<Rule>();
        Dictionary<string, string>? cur = null;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (t.StartsWith("- {") && t.EndsWith("}"))
            {
                Flush(ref cur, rules);
                cur = ParseFlow(t[3..^1]);
                Flush(ref cur, rules);
                continue;
            }
            if (t.StartsWith("- "))
            {
                Flush(ref cur, rules);
                cur = new(StringComparer.OrdinalIgnoreCase);
                AddKv(cur, t[2..]);
                continue;
            }
            if (cur is not null && t.Contains(':')) AddKv(cur, t);
        }
        Flush(ref cur, rules);
        return rules;
    }

    private static Dictionary<string, string> ParseFlow(string body)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(',')) AddKv(d, part.Trim());
        return d;
    }

    private static void AddKv(Dictionary<string, string> d, string kv)
    {
        var i = kv.IndexOf(':');
        if (i <= 0) return;
        d[kv[..i].Trim()] = kv[(i + 1)..].Trim().Trim('"', '\'');
    }

    private static void Flush(ref Dictionary<string, string>? cur, List<Rule> rules)
    {
        if (cur is null) return;
        if (cur.TryGetValue("pattern", out var pattern) && cur.TryGetValue("category", out var cat) && Categories.IsValid(cat))
        {
            var mt = cur.TryGetValue("match", out var m) ? m.ToLowerInvariant() switch
            {
                "process" => RuleMatch.Process,
                "domain" => RuleMatch.Domain,
                "title" or "titlecontains" => RuleMatch.TitleContains,
                _ => RuleMatch.Process,
            } : RuleMatch.Process;
            rules.Add(new Rule { MatchType = mt, Pattern = pattern, Category = cat, Priority = cur.TryGetValue("priority", out var p) && int.TryParse(p, out var pi) ? pi : 100 });
        }
        cur = null;
    }
}
