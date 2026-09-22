using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ParserCodeReference;

public sealed class SqlStore
{
    readonly string _connectionString;

    public SqlStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void Save(Dictionary<string, List<Dictionary<string, object?>>> bundle, ExtrasPack extras)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            EnsureTables(conn, tx);
            EnsureExtraColumns(conn, tx, extras);
            var sessionId = bundle["test_sessions"][0]["session_id"]?.ToString()
                ?? throw new InvalidOperationException("session_id missing");
            DeleteSession(conn, tx, sessionId);
            foreach (var table in Schema.TableOrder)
                InsertRows(conn, tx, table, bundle.GetValueOrDefault(table) ?? new(), extras.Columns);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    static void EnsureTables(SqlConnection conn, SqlTransaction tx)
    {
        foreach (var table in Schema.TableOrder)
        {
            var lines = new List<string>();
            foreach (var col in Schema.Tables[table])
            {
                var nullSql = col.Pk || col.NotNull ? "NOT NULL" : "NULL";
                var def = "";
                if (col.Default is bool b)
                    def = $" DEFAULT {(b ? 1 : 0)}";
                else if (col.Default is int i)
                    def = $" DEFAULT {i}";
                lines.Add($"    [{col.Name}] {Schema.SqlType(col)} {nullSql}{def}");
            }
            var pk = string.Join(", ", Schema.PrimaryKeys[table].Select(k => $"[{k}]"));
            lines.Add($"    CONSTRAINT PK_{table} PRIMARY KEY ({pk})");
            var body = string.Join(",\n", lines);
            Exec(conn, tx, $"""
IF OBJECT_ID(N'dbo.{table}', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[{table}] (
{body}
    );
END
""");
        }

        Exec(conn, tx, """
IF OBJECT_ID(N'dbo.FK_dimm_results_session', N'F') IS NULL
    ALTER TABLE dbo.dimm_results ADD CONSTRAINT FK_dimm_results_session
    FOREIGN KEY (session_id) REFERENCES dbo.test_sessions(session_id);
""");
        Exec(conn, tx, """
IF OBJECT_ID(N'dbo.FK_hpl_loop_metrics_session', N'F') IS NULL
    ALTER TABLE dbo.hpl_loop_metrics ADD CONSTRAINT FK_hpl_loop_metrics_session
    FOREIGN KEY (session_id) REFERENCES dbo.test_sessions(session_id);
""");
        Exec(conn, tx, """
IF OBJECT_ID(N'dbo.FK_ecc_events_session', N'F') IS NULL
    ALTER TABLE dbo.ecc_events ADD CONSTRAINT FK_ecc_events_session
    FOREIGN KEY (session_id) REFERENCES dbo.test_sessions(session_id);
""");
        Exec(conn, tx, """
IF OBJECT_ID(N'dbo.FK_extraction_manifest_session', N'F') IS NULL
    ALTER TABLE dbo.extraction_manifest ADD CONSTRAINT FK_extraction_manifest_session
    FOREIGN KEY (session_id) REFERENCES dbo.test_sessions(session_id);
""");
    }

    static void EnsureExtraColumns(SqlConnection conn, SqlTransaction tx, ExtrasPack extras)
    {
        foreach (var col in extras.Columns)
        {
            if (!IsSafeName(col.Table) || !IsSafeName(col.Name))
                continue;
            var dtype = ExtraSqlType(col.DataType);
            Exec(conn, tx, $"""
IF COL_LENGTH('dbo.{col.Table}', '{col.Name}') IS NULL
    ALTER TABLE dbo.[{col.Table}] ADD [{col.Name}] {dtype} NULL;
""");
        }
    }

    static void DeleteSession(SqlConnection conn, SqlTransaction tx, string sessionId)
    {
        foreach (var table in Schema.TableOrder.Reverse())
        {
            using var cmd = new SqlCommand($"DELETE FROM dbo.[{table}] WHERE session_id = @id", conn, tx);
            cmd.Parameters.AddWithValue("@id", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    static void InsertRows(
        SqlConnection conn,
        SqlTransaction tx,
        string table,
        List<Dictionary<string, object?>> rows,
        List<ExtraColumn> extras)
    {
        if (rows.Count == 0)
            return;
        var catalog = Schema.Tables[table].ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
        var names = Schema.ColumnNames(table);
        var extraForTable = extras.Where(c => c.Table.Equals(table, StringComparison.OrdinalIgnoreCase)).ToList();
        var extraNames = extraForTable.Select(c => c.Name).Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var allNames = names.Concat(extraNames).ToList();
        var extraTypes = extraForTable.ToDictionary(c => c.Name, c => (c.DataType ?? "STRING").ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
        var colSql = string.Join(", ", allNames.Select(n => $"[{n}]"));
        var placeholders = string.Join(", ", allNames.Select((_, i) => $"@p{i}"));
        var sql = $"INSERT INTO dbo.[{table}] ({colSql}) VALUES ({placeholders})";
        foreach (var row in rows)
        {
            using var cmd = new SqlCommand(sql, conn, tx);
            for (var i = 0; i < allNames.Count; i++)
            {
                var name = allNames[i];
                catalog.TryGetValue(name, out var col);
                extraTypes.TryGetValue(name, out var extraType);
                cmd.Parameters.AddWithValue($"@p{i}", ToSql(col, row.GetValueOrDefault(name), extraType) ?? DBNull.Value);
            }
            cmd.ExecuteNonQuery();
        }
    }

    static object? ToSql(ColumnDef? col, object? value, string? extraType)
    {
        var dataType = (extraType ?? col?.DataType ?? "STRING").ToUpperInvariant();
        if (value is null)
            return col?.Default;
        if (dataType is "ARRAY" or "VARIANT")
        {
            if (value is string s)
                return s;
            return JsonSerializer.Serialize(value);
        }
        if (dataType == "BOOLEAN")
            return value is true or 1 ? 1 : 0;
        if (dataType == "INT")
            return CoerceInt(value) ?? (object?)col?.Default ?? DBNull.Value;
        if (dataType == "FLOAT")
            return CoerceFloat(value) ?? (object?)col?.Default ?? DBNull.Value;
        return value;
    }

    // ExtraExtract always yields strings. FLOAT/INT extras (e.g. dimmdensity) must be
    // parsed here or SQL throws "nvarchar to float". Non-numeric text → NULL.
    static int? CoerceInt(object value)
    {
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value is double d) return (int)d;
        if (value is float f) return (int)f;
        if (value is decimal m) return (int)m;
        var text = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (int.TryParse(text, out var n)) return n;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var dbl))
            return (int)dbl;
        var num = System.Text.RegularExpressions.Regex.Match(text, @"-?\d+");
        return num.Success && int.TryParse(num.Value, out var fromText) ? fromText : null;
    }

    static double? CoerceFloat(object value)
    {
        if (value is double d) return d;
        if (value is float f) return f;
        if (value is int i) return i;
        if (value is long l) return l;
        if (value is decimal m) return (double)m;
        var text = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n;
        var num = System.Text.RegularExpressions.Regex.Match(text, @"-?\d+(\.\d+)?");
        return num.Success && double.TryParse(num.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var fromText)
            ? fromText
            : null;
    }

    static string ExtraSqlType(string? dataType) =>
        (dataType ?? "STRING").ToUpperInvariant() switch
        {
            "INT" => "INT",
            "FLOAT" => "FLOAT",
            "BOOLEAN" => "BIT",
            "ARRAY" or "VARIANT" => "NVARCHAR(MAX)",
            _ => "NVARCHAR(4000)"
        };

    static bool IsSafeName(string name) =>
        name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    static void Exec(SqlConnection conn, SqlTransaction tx, string sql)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.ExecuteNonQuery();
    }
}
