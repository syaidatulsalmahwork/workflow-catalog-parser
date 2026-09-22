namespace ParserCodeReference;

public sealed class ColumnDef
{
    public string Name { get; }
    public string DataType { get; }
    public bool Pk { get; }
    public bool NotNull { get; }
    public object? Default { get; }

    public ColumnDef(string name, string dataType, bool pk = false, bool notNull = false, object? defaultValue = null)
    {
        Name = name;
        DataType = dataType;
        Pk = pk;
        NotNull = notNull;
        Default = defaultValue;
    }
}

public static class Schema
{
    public static readonly string[] TableOrder =
    {
        "test_sessions",
        "dimm_results",
        "hpl_loop_metrics",
        "ecc_events",
        "extraction_manifest"
    };

    public static readonly Dictionary<string, string[]> PrimaryKeys = new()
    {
        ["test_sessions"] = new[] { "session_id" },
        ["dimm_results"] = new[] { "session_id", "serial_number" },
        ["hpl_loop_metrics"] = new[] { "session_id", "loop_num" },
        ["ecc_events"] = new[] { "session_id", "serial_number", "loop_num", "event_seq" },
        ["extraction_manifest"] = new[] { "session_id" }
    };

    public static readonly Dictionary<string, List<ColumnDef>> Tables = Build();

    public static List<string> ColumnNames(string table) =>
        Tables[table].Select(c => c.Name).ToList();

    public static Dictionary<string, object?> DefaultRow(string table)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in Tables[table])
        {
            if (col.Default is not null)
                row[col.Name] = col.Default;
            else if (col.Pk)
                continue;
            else if (col.DataType is "ARRAY")
                row[col.Name] = new List<object?>();
            else if (col.DataType is "VARIANT")
                row[col.Name] = new Dictionary<string, object?>();
            else
                row[col.Name] = null;
        }
        return row;
    }

    public static string SqlType(ColumnDef col)
    {
        return col.DataType switch
        {
            "INT" => "INT",
            "FLOAT" => "FLOAT",
            "BOOLEAN" => "BIT",
            "ARRAY" or "VARIANT" => "NVARCHAR(MAX)",
            _ when col.Pk => "NVARCHAR(128)",
            _ => "NVARCHAR(4000)"
        };
    }

    static List<ColumnDef> Bookkeeping() =>
    [
        new("attributes", "VARIANT"),
        new("diagnostics", "VARIANT"),
        new("source_chunk_ids", "ARRAY"),
        new("extraction_metadata", "VARIANT"),
        new("processed_at", "STRING")
    ];

    static List<ColumnDef> Identity(string table)
    {
        var cols = new List<ColumnDef>
        {
            new("log_file", "STRING"),
            new("filename_code", "STRING"),
            new("test_stage", "STRING"),
            new("wtstation", "STRING"),
            new("operator", "STRING"),
            new("work_order", "STRING"),
            new("start_ts", "STRING")
        };
        if (table == "dimm_results")
        {
            cols.Add(new("test_program", "STRING"));
            cols.Add(new("test_class", "STRING"));
            cols.Add(new("overall_result", "STRING"));
        }
        else if (table is "hpl_loop_metrics" or "ecc_events")
        {
            cols.Add(new("test_class", "STRING"));
            cols.Add(new("ddr_gen", "STRING"));
        }
        return cols;
    }

    static Dictionary<string, List<ColumnDef>> Build()
    {
        return new Dictionary<string, List<ColumnDef>>
        {
            ["test_sessions"] =
            [
                new("session_id", "STRING", pk: true, notNull: true),
                new("filename_code", "STRING"),
                new("log_file", "STRING"),
                new("platform", "STRING"),
                new("grammar", "STRING"),
                new("wtstation", "STRING"),
                new("station_prefix", "STRING"),
                new("test_class", "STRING"),
                new("test_stage", "STRING"),
                new("test_program", "STRING"),
                new("ddr_gen", "STRING"),
                new("site", "STRING"),
                new("operator", "STRING"),
                new("ip_address", "STRING"),
                new("mac_or_host", "STRING"),
                new("work_order", "STRING"),
                new("part_number", "STRING"),
                new("serial_format", "STRING"),
                new("start_ts", "STRING"),
                new("start_local", "STRING"),
                new("start_tz_offset", "STRING"),
                new("end_ts", "STRING"),
                new("ts_source", "STRING"),
                new("duration_min", "FLOAT"),
                new("overall_result", "STRING"),
                new("total_modules", "INT", notNull: true, defaultValue: 0),
                new("modules_pass", "INT", notNull: true, defaultValue: 0),
                new("modules_fail", "INT", notNull: true, defaultValue: 0),
                new("modules_not_tested", "INT", notNull: true, defaultValue: 0),
                new("loops_completed", "INT"),
                new("max_temp_reached_c", "FLOAT"),
                new("min_temp_c", "FLOAT"),
                new("avg_temp_c", "FLOAT"),
                new("critical_temp_breach", "BOOLEAN", notNull: true, defaultValue: false),
                new("termination_cause", "STRING"),
                new("ecc_coverage", "STRING"),
                new("temp_coverage", "STRING"),
                new("slot_coverage", "STRING"),
                new("dq_present", "BOOLEAN", notNull: true, defaultValue: false),
                new("sel_correctable_count", "INT"),
                new("sel_uncorrectable_count", "INT"),
                new("sel_memory_events", "ARRAY"),
                new("temperature_readings", "VARIANT"),
                new("module_serials", "ARRAY"),
                new("parser_versions", "VARIANT"),
                new("system_info", "VARIANT"),
                .. Bookkeeping()
            ],
            ["dimm_results"] =
            [
                new("session_id", "STRING", pk: true, notNull: true),
                new("serial_number", "STRING", pk: true, notNull: true),
                new("slot", "STRING"),
                new("part_number", "STRING"),
                new("ddr_gen", "STRING"),
                new("size_gb", "FLOAT"),
                new("mod_type", "STRING"),
                new("result", "STRING"),
                new("duration_min", "FLOAT"),
                new("hpl_loops_completed", "INT"),
                new("total_ecc_errors", "INT"),
                new("final_temp_c", "FLOAT"),
                new("voltage_v", "FLOAT"),
                new("manufacturer", "STRING"),
                new("rated_speed", "STRING"),
                new("device_width", "STRING"),
                new("ranks", "INT"),
                new("not_tested_code", "STRING"),
                new("not_tested_reason", "STRING"),
                new("serial_prefix", "STRING"),
                new("manufacturing_week", "STRING"),
                new("is_board_level", "BOOLEAN", notNull: true, defaultValue: false),
                new("failure_modes", "ARRAY"),
                new("dq_detail", "ARRAY"),
                .. Identity("dimm_results"),
                .. Bookkeeping()
            ],
            ["hpl_loop_metrics"] =
            [
                new("session_id", "STRING", pk: true, notNull: true),
                new("loop_num", "INT", pk: true, notNull: true),
                new("loop_total", "INT"),
                new("problem_size", "INT"),
                new("lda", "INT"),
                new("align_kb", "INT"),
                new("time_sec", "FLOAT"),
                new("gflops", "FLOAT"),
                new("gflops_avg", "FLOAT"),
                new("gflops_max", "FLOAT"),
                new("residual", "FLOAT"),
                new("residual_norm", "FLOAT"),
                new("hpl_check", "STRING"),
                new("gflops_delta", "FLOAT"),
                new("elapsed_time_min", "FLOAT"),
                new("elapsed_sec", "INT"),
                new("event_ts", "STRING"),
                new("loop_min_temp_c", "FLOAT"),
                new("loop_max_temp_c", "FLOAT"),
                new("loop_avg_temp_c", "FLOAT"),
                new("vdd_rails", "VARIANT"),
                .. Identity("hpl_loop_metrics"),
                .. Bookkeeping()
            ],
            ["ecc_events"] =
            [
                new("session_id", "STRING", pk: true, notNull: true),
                new("serial_number", "STRING", pk: true, notNull: true),
                new("loop_num", "INT", pk: true, notNull: true, defaultValue: 0),
                new("event_seq", "INT", pk: true, notNull: true),
                new("elapsed_sec", "INT"),
                new("event_ts", "STRING"),
                new("board_slot", "STRING"),
                new("dimminfo_secc_count", "INT"),
                new("internal_secc_count", "INT"),
                new("internal_secc_shadow_count", "INT"),
                new("per_poll_secc", "INT"),
                new("temperature_c", "FLOAT"),
                new("voltage_v", "FLOAT"),
                new("is_first_error", "BOOLEAN", notNull: true, defaultValue: false),
                new("first_failure_elapsed", "STRING"),
                new("first_failure_sec", "INT"),
                new("critical_temp_flag", "BOOLEAN", notNull: true, defaultValue: false),
                new("dq_detail", "ARRAY"),
                new("ecc_state", "STRING"),
                .. Identity("ecc_events"),
                .. Bookkeeping()
            ],
            ["extraction_manifest"] =
            [
                new("session_id", "STRING", pk: true, notNull: true),
                new("log_file", "STRING"),
                new("filename_code", "STRING"),
                new("grammar", "STRING"),
                new("test_class", "STRING"),
                new("test_stage", "STRING"),
                new("ecc_coverage", "STRING"),
                new("temp_coverage", "STRING"),
                new("slot_coverage", "STRING"),
                new("dq_present", "BOOLEAN", notNull: true, defaultValue: false),
                new("has_end_ts", "BOOLEAN", notNull: true, defaultValue: false),
                new("work_order_source", "STRING"),
                new("sel_correctable_count", "INT"),
                new("sel_uncorrectable_count", "INT"),
                new("chunks_total", "INT", notNull: true, defaultValue: 0),
                new("chunks_parsed", "INT", notNull: true, defaultValue: 0),
                new("chunks_unknown", "INT", notNull: true, defaultValue: 0),
                new("gap_count", "INT", notNull: true, defaultValue: 0),
                new("quarantined", "BOOLEAN", notNull: true, defaultValue: false),
                new("stage2_version", "INT"),
                new("extraction_report", "VARIANT"),
                new("processed_at", "STRING")
            ]
        };
    }
}
