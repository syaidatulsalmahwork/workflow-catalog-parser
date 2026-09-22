using System.Text.Json;

namespace ParserCodeReference;

public sealed class ExtraColumn
{
    public string Table { get; set; } = "";
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "STRING";
    public string Token { get; set; } = "";
    public string Grain { get; set; } = "";
}

public sealed class ExtrasPack
{
    public object? Version { get; set; }
    public List<ExtraColumn> Columns { get; set; } = new();
}

public static class ExtrasLoader
{
    public static ExtrasPack Load(string extrasDir)
    {
        var merged = new Dictionary<(string Table, string Name), ExtraColumn>();
        object? lastVersion = null;
        if (!Directory.Exists(extrasDir))
            return new ExtrasPack();

        foreach (var path in Directory.GetFiles(extrasDir, "v*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var ver))
                lastVersion = ver.ToString();
            if (!root.TryGetProperty("columns", out var cols) || cols.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var col in cols.EnumerateArray())
            {
                var extra = new ExtraColumn
                {
                    Table = col.TryGetProperty("table", out var t) ? t.GetString() ?? "" : "",
                    Name = col.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    DataType = col.TryGetProperty("data_type", out var d) ? d.GetString() ?? "STRING" : "STRING",
                    Token = col.TryGetProperty("token", out var tok) ? tok.GetString()
                        ?? (col.TryGetProperty("sourceKeyword", out var sk) ? sk.GetString() : "") ?? "" : "",
                    Grain = col.TryGetProperty("grain", out var g) ? g.GetString() ?? "" : ""
                };
                if (string.IsNullOrWhiteSpace(extra.Table) || string.IsNullOrWhiteSpace(extra.Name))
                    continue;
                merged[(extra.Table, extra.Name)] = extra;
            }
        }

        return new ExtrasPack
        {
            Version = lastVersion,
            Columns = merged.Values.ToList()
        };
    }
}
