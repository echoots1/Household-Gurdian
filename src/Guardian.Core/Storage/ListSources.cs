namespace Guardian.Core.Storage;

/// <summary>Loads the shipped lists/*.txt files (one domain per line, # comments) into category_list.</summary>
public static class ListLoader
{
    public static readonly string[] ShippedLists = { "adult", "gambling", "drugs", "weapons" };

    public static IEnumerable<string> ParseDomains(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', '\t')[^1].ToLowerInvariant()) // hosts-file style "0.0.0.0 domain" is accepted
            .Where(l => l != "0.0.0.0" && l != "127.0.0.1" && l != "localhost" && l.Contains('.'));

    public static int LoadDirectory(ListRepo repo, string dir, DateTimeOffset now, bool onlyIfEmpty)
    {
        if (!Directory.Exists(dir)) return 0;
        var counts = repo.Counts();
        var n = 0;
        foreach (var file in Directory.GetFiles(dir, "*.txt"))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (name == "custom") continue; // the parent's list is never overwritten
            if (onlyIfEmpty && counts.TryGetValue(name, out var c) && c > 0) continue;
            var domains = ParseDomains(File.ReadAllText(file)).ToList();
            repo.ReplaceList(name, domains, now);
            n += domains.Count;
        }
        return n;
    }
}
