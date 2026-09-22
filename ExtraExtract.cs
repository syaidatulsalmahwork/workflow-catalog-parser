namespace ParserCodeReference;

// WORKFLOW FILE — extras only.
// Catalog parser, unzip, SQL, and the five table schemas stay out of this file.
// The Parser Extras agent replaces this whole file. Copy it into the C# project
// next to CatalogParser.cs, then Start.
//
// Empty Columns + empty Apply = catalog-only run (no extra columns).

public static class ExtraExtract
{
    public static ExtraColumn[] Columns { get; } =
    [
        // Agent adds one ExtraColumn per mapping extraColumns row, for example:
        // new ExtraColumn
        // {
        //     Table = "test_sessions",
        //     Name = "locale",
        //     DataType = "STRING",
        //     Token = "@locale=",
        //     Grain = "session"
        // },
    ];

    public static void Apply(
        Dictionary<string, List<Dictionary<string, object?>>> bundle,
        string[] lines)
    {
        // Agent fills extra values here. Do not assign catalog columns.
        //
        // Session extras:
        //   bundle["test_sessions"][0]["locale"] = LineAfter(lines, "@locale=")
        //       ?? LineAfter(lines, "locale=");
        //
        // Child-table extras: loop rows in bundle["dimm_results"] (etc.)
        // and set only the extra names from Columns.
    }

    static string? LineAfter(string[] lines, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        foreach (var line in lines)
        {
            var i = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var after = line[(i + prefix.Length)..].Trim();
            if (after.StartsWith('=')) after = after[1..].Trim();
            if (string.IsNullOrEmpty(after)) return null;
            return after.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        return null;
    }

    static string? KeyValue(string[] lines, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var needle = key.Trim().TrimEnd('=') + "=";
        return LineAfter(lines, needle);
    }
}
