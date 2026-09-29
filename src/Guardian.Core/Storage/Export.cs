using System.Globalization;
using System.IO.Compression;
using System.Text;
using Dapper;

namespace Guardian.Core.Storage;

/// <summary>Zip of CSVs the parent can hand to the child. One file per table that holds activity data.</summary>
public static class Export
{
    public static byte[] ToZip(Db db)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, db, "activity.csv", "SELECT id, datetime(started_at,'unixepoch','localtime') AS started, datetime(ended_at,'unixepoch','localtime') AS ended, kind, process, title, domain, subdomain, idle, browser, category FROM activity_event ORDER BY started_at");
            Add(zip, db, "daily_totals.csv", "SELECT date, category, seconds FROM rollup_daily ORDER BY date, category");
            Add(zip, db, "items.csv", "SELECT date, kind, name, category, seconds FROM rollup_item ORDER BY date, seconds DESC");
            Add(zip, db, "alerts.csv", "SELECT id, type, payload, datetime(created_at,'unixepoch','localtime') AS created, datetime(acknowledged_at,'unixepoch','localtime') AS acknowledged FROM alert ORDER BY id");
            Add(zip, db, "enforcement.csv", "SELECT id, datetime(at,'unixepoch','localtime') AS at, reason, step, policy_version, detail FROM enforcement_event ORDER BY at");
            Add(zip, db, "rules.csv", "SELECT id, match_type, pattern, category, priority FROM rule ORDER BY priority");
            Add(zip, db, "policy.csv", "SELECT version, json, datetime(saved_at,'unixepoch','localtime') AS saved FROM policy ORDER BY version");
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, Db db, string name, string sql)
    {
        using var c = db.Open();
        using var reader = c.ExecuteReader(sql);
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        w.WriteLine(string.Join(',', cols.Select(Csv)));
        while (reader.Read())
        {
            var vals = new string[reader.FieldCount];
            for (var i = 0; i < vals.Length; i++) vals[i] = Csv(reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
            w.WriteLine(string.Join(',', vals));
        }
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
