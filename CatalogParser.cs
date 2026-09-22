using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ParserCodeReference;

public static class CatalogParser
{
    static readonly Regex AtValue = new(@"^@([A-Za-z0-9_]+)=(.*)$", RegexOptions.Compiled);
    static readonly Regex Kv = new(@"\b([A-Za-z][A-Za-z0-9_]*)=(?<val>[^\s,]+)", RegexOptions.Compiled);
    static readonly Regex LoopTest = new(@"Loop Test:\s*(\d+)\s*(?:of|/)\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Duration = new(@"(?:(\d+)\s*h)?\s*(?:(\d+)\s*m)?\s*(?:(\d+)\s*s)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FilenameCode = new(@"([A-Za-z0-9]{6})", RegexOptions.Compiled);
    static readonly Regex LinpackNums = new(@"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?", RegexOptions.Compiled);
    static readonly Regex ProgramRe = new(@"\b(R[A-Z0-9.]+-(?:DDR\d|CXL))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TempRe = new(@"(min_temp|max_temp|avg_temp)\s*[:=]\s*([-+]?\d+\.?\d*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex VoltRe = new(@"([A-Za-z0-9_]+)\s*[:=]\s*([0-9.]+)\s*V", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CxlDimmLine = new(
        @"dimmserial:\s*(?<sn>[^,]+),\s*dimmdensity:\s*(?<den>[^,]+),\s*dimmtype:\s*(?<type>[^,]+),\s*dimmmanuf:\s*(?<manu>[^,]+),\s*dimmpn:\s*(?<pn>[^,]+),\s*dimmslotnumber:\s*(?<slot>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CxlDieTemp = new(
        @"Leo Die Temperature:\s*([0-9.]+)|die temperature\s*-\s*([0-9.]+)\s*'?C|Device temp\s*([0-9.]+)\s*'?C|sensor temp\s*([0-9.]+)\s*'?C",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Dictionary<string, List<Dictionary<string, object?>>> Parse(ExtractedLog log, ExtrasPack extras)
    {
        extras ??= new ExtrasPack();
        var lines = SplitLines(log.Content);
        var grammar = DetectGrammar(log.Content);
        var at = AtMap(lines);
        var sessionId = Convert.ToHexString(SHA256.HashData(log.RawBytes)).ToLowerInvariant();
        var filenameCode = FileCode(log.FileName);
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var session = Schema.DefaultRow("test_sessions");
        session["session_id"] = sessionId;
        session["log_file"] = log.FileName;
        session["filename_code"] = filenameCode;
        session["grammar"] = grammar;
        session["platform"] = grammar == "A" ? "SMART" : grammar == "B" ? "OpenPower" : null;
        session["start_local"] = Clean(Get(at, "start"));
        session["start_ts"] = Clean(LineValue(lines, "Start time UTC:")) ?? session["start_local"];
        session["ts_source"] = LineValue(lines, "Start time UTC:") is not null ? "utc_echo" : "local_offset";
        session["end_ts"] = Clean(Get(at, "end")) ?? LineValue(lines, "Completion time UTC:");
        session["wtstation"] = Clean(Get(at, "wtstation"));
        session["station_prefix"] = StationPrefix(session["wtstation"] as string);
        session["operator"] = Clean(Get(at, "operator"));
        session["work_order"] = Clean(Get(at, "job"));
        session["part_number"] = Clean(Get(at, "part"));
        session["ip_address"] = Clean(Get(at, "ip"));
        session["mac_or_host"] = Clean(Get(at, "mac"));
        session["overall_result"] = Result(Get(at, "result"), lines);
        session["test_program"] = TestProgram(lines);
        session["test_class"] = TestClass(log.Content);
        session["test_stage"] = TestStage(session["wtstation"] as string, log.FileName, log.Content);
        session["ddr_gen"] = DdrGen(log.Content, session["test_program"] as string);
        session["site"] = Site(session["wtstation"] as string, log.Content);
        session["duration_min"] = DurationMin(LineValue(lines, "Total Test time Duration:"));
        session["start_tz_offset"] = null;
        var temps = BoardTemps(lines);
        var cxlTemps = CxlDieTemps(lines);
        session["min_temp_c"] = temps.min ?? cxlTemps.min;
        session["max_temp_reached_c"] = temps.max ?? cxlTemps.max;
        session["avg_temp_c"] = temps.avg ?? cxlTemps.avg;
        var (selC, selU, selEvents) = Sel(lines);
        session["sel_correctable_count"] = selC;
        session["sel_uncorrectable_count"] = selU;
        session["sel_memory_events"] = selEvents;
        var serials = Serials(Get(at, "serial"));
        session["module_serials"] = serials;
        session["total_modules"] = serials.Count;
        session["serial_format"] = SerialFormat(serials);
        session["system_info"] = SystemInfo(lines);
        session["temperature_readings"] = new Dictionary<string, object?>
        {
            ["min"] = session["min_temp_c"],
            ["max"] = session["max_temp_reached_c"],
            ["avg"] = session["avg_temp_c"]
        };
        session["parser_versions"] = new Dictionary<string, object?>
        {
            ["catalog"] = "1.0",
            ["extras"] = extras.Version
        };
        session["diagnostics"] = new Dictionary<string, object?>
        {
            ["engine"] = "Parser Code Reference",
            ["zip_path"] = log.ZipPath,
            ["grammar"] = grammar
        };
        session["attributes"] = new Dictionary<string, object?>();
        session["source_chunk_ids"] = new List<object?>();
        session["extraction_metadata"] = new Dictionary<string, object?>();
        session["processed_at"] = now;

        var identity = new Dictionary<string, object?>
        {
            ["log_file"] = session["log_file"],
            ["filename_code"] = filenameCode,
            ["test_stage"] = session["test_stage"],
            ["wtstation"] = session["wtstation"],
            ["operator"] = session["operator"],
            ["work_order"] = session["work_order"],
            ["start_ts"] = session["start_ts"]
        };

        var dimms = DimmRows(sessionId, serials, lines, identity, session);
        var loops = LoopRows(sessionId, lines, identity, session);
        var ecc = EccRows(sessionId, lines, identity, session, dimms);

        var dimmSerials = dimms
            .Select(d => d.GetValueOrDefault("serial_number") as string)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
        if (dimmSerials.Count > 0 && dimmSerials.Any(s => !serials.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            session["module_serials"] = dimmSerials;
            session["total_modules"] = dimmSerials.Count;
            session["serial_format"] = SerialFormat(dimmSerials);
        }

        session["loops_completed"] = loops.Count == 0 ? null : loops.Count;
        session["ecc_coverage"] = ecc.Count > 0 ? "per_dimm" : (selC > 0 || selU > 0 ? "sel_only" : "none");
        session["temp_coverage"] = session["min_temp_c"] is not null || session["max_temp_reached_c"] is not null ? "full" : "none";
        session["slot_coverage"] = dimms.Any(d => d.GetValueOrDefault("slot") is not null) ? "full" : "none";
        session["dq_present"] = false;
        session["modules_pass"] = dimms.Count(d => string.Equals(d.GetValueOrDefault("result") as string, "PASS", StringComparison.OrdinalIgnoreCase));
        session["modules_fail"] = dimms.Count(d => string.Equals(d.GetValueOrDefault("result") as string, "FAIL", StringComparison.OrdinalIgnoreCase));
        session["modules_not_tested"] = dimms.Count == 0 ? 0 : dimms.Count(d => string.Equals(d.GetValueOrDefault("result") as string, "NOT_TESTED", StringComparison.OrdinalIgnoreCase));

        var manifest = Schema.DefaultRow("extraction_manifest");
        manifest["session_id"] = sessionId;
        manifest["log_file"] = log.FileName;
        manifest["filename_code"] = filenameCode;
        manifest["grammar"] = grammar;
        manifest["test_class"] = session["test_class"];
        manifest["test_stage"] = session["test_stage"];
        manifest["ecc_coverage"] = session["ecc_coverage"];
        manifest["temp_coverage"] = session["temp_coverage"];
        manifest["slot_coverage"] = session["slot_coverage"];
        manifest["dq_present"] = false;
        manifest["has_end_ts"] = session["end_ts"] is not null;
        manifest["work_order_source"] = at.ContainsKey("job") ? "at_job" : "none";
        manifest["sel_correctable_count"] = selC;
        manifest["sel_uncorrectable_count"] = selU;
        manifest["chunks_total"] = 0;
        manifest["chunks_parsed"] = 0;
        manifest["chunks_unknown"] = 0;
        manifest["gap_count"] = 0;
        manifest["quarantined"] = grammar is null;
        manifest["stage2_version"] = 1;
        manifest["extraction_report"] = new Dictionary<string, object?>();
        manifest["processed_at"] = now;

        var bundle = new Dictionary<string, List<Dictionary<string, object?>>>
        {
            ["test_sessions"] = new() { session },
            ["dimm_results"] = dimms,
            ["hpl_loop_metrics"] = loops,
            ["ecc_events"] = ecc,
            ["extraction_manifest"] = new() { manifest }
        };
        ApplyExtras(bundle, lines, extras);
        ExtraExtract.Apply(bundle, lines);
        return bundle;
    }

    public static string? DetectGrammar(string text)
    {
        if (text.Contains("Host GUI version:"))
            return "A";
        if (text.Contains("Test directory is:"))
            return "B";
        return null;
    }

    static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    static string? Clean(string? value)
    {
        if (value is null) return null;
        var text = value.Trim().Trim('"').Trim('\'');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static string? Get(Dictionary<string, string> at, string key) =>
        at.TryGetValue(key, out var v) ? v : null;

    static Dictionary<string, string> AtMap(string[] lines)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var m = AtValue.Match(line.Trim());
            if (m.Success && !found.ContainsKey(m.Groups[1].Value))
                found[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value.Trim();
        }
        return found;
    }

    static string? LineValue(IEnumerable<string> lines, string prefix)
    {
        foreach (var line in lines)
        {
            var i = line.IndexOf(prefix, StringComparison.Ordinal);
            if (i >= 0)
            {
                var v = line[(i + prefix.Length)..].Trim();
                return string.IsNullOrEmpty(v) ? null : v;
            }
        }
        return null;
    }

    static string? Result(string? atResult, string[] lines)
    {
        var raw = Clean(atResult) ?? LineValue(lines, "Results:");
        if (raw is null) return null;
        var upper = raw.ToUpperInvariant().Replace(" ", "_");
        foreach (var token in new[] { "PASS", "FAIL", "PARTIAL", "NOT_TESTED", "INCOMPLETE" })
        {
            if (upper.Contains(token))
                return token;
        }
        if (raw.Contains("PASS", StringComparison.OrdinalIgnoreCase)) return "PASS";
        if (raw.Contains("FAIL", StringComparison.OrdinalIgnoreCase)) return "FAIL";
        return raw;
    }

    static string? TestProgram(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Contains("test program name", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                return line.Split(':', 2)[1].Trim();
            var m = ProgramRe.Match(line);
            if (m.Success)
                return m.Groups[1].Value;
        }
        return null;
    }

    static string? TestClass(string text)
    {
        // Same order as parse_registry.yml test_class_map: NVDIMM, CXL, Burn-In, SLT.
        var upper = text.ToUpperInvariant();
        if (upper.Contains("NVDIMM")) return "NVDIMM";
        if (Regex.IsMatch(text, @"\bCXL\b", RegexOptions.IgnoreCase)) return "CXL";
        if (upper.Contains("BURN_IN") || upper.Contains("BURN-IN") || upper.Contains("BURNIN"))
            return "BURN_IN";
        if (upper.Contains("ECC_SLT") || upper.Contains("SYSTEM LEVEL") || Regex.IsMatch(text, @"\bSLT\b"))
            return "ECC_SLT";
        if (upper.Contains("DDIMM")) return "DDIMM";
        return null;
    }

    static string? TestStage(string? wtstation, string fileName, string text)
    {
        var blob = $"{wtstation} {fileName} {text[..Math.Min(4000, text.Length)]}".ToUpperInvariant()
            .Replace("-", "").Replace("_", "");
        foreach (var name in new[] { "BBURNIN", "BURNIN", "SYSHOT", "SYSCOLD", "SYSTEST" })
        {
            if (!blob.Contains(name)) continue;
            if (name == "BBURNIN")
            {
                var m = Regex.Match(blob, @"BBURNIN(\d+)");
                return m.Success ? $"BBURNIN{m.Groups[1].Value}" : "BBURNIN";
            }
            return name;
        }
        return null;
    }

    static string? DdrGen(string text, string? program)
    {
        var blob = $"{text} {program}";
        if (Regex.IsMatch(blob, @"\bCXL\b", RegexOptions.IgnoreCase)) return "CXL";
        if (Regex.IsMatch(blob, "DDR5", RegexOptions.IgnoreCase)) return "DDR5";
        if (Regex.IsMatch(blob, "DDR4", RegexOptions.IgnoreCase)) return "DDR4";
        if (Regex.IsMatch(blob, "DDR3", RegexOptions.IgnoreCase)) return "DDR3";
        if (Regex.IsMatch(blob, "LPDDR", RegexOptions.IgnoreCase)) return "DDR4";
        return null;
    }

    static string? Site(string? wtstation, string text)
    {
        var blob = $"{wtstation} {text[..Math.Min(2000, text.Length)]}".ToLowerInvariant();
        if (blob.Contains("shqwtester") || Regex.IsMatch(blob, @"\bshq\b")) return "SHQ";
        if (blob.Contains("pngwtester") || Regex.IsMatch(blob, @"\bpng\b")) return "PNG";
        return null;
    }

    static string? StationPrefix(string? wtstation)
    {
        if (string.IsNullOrWhiteSpace(wtstation)) return null;
        var m = Regex.Match(wtstation, @"^[A-Za-z]+");
        return m.Success ? m.Value : wtstation[..Math.Min(8, wtstation.Length)];
    }

    static string? FileCode(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = FilenameCode.Match(stem.Replace("-", ""));
        if (m.Success) return m.Groups[1].Value[..Math.Min(6, m.Groups[1].Value.Length)];
        return string.IsNullOrEmpty(stem) ? null : stem[..Math.Min(6, stem.Length)];
    }

    static double? DurationMin(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Duration.Match(text.Replace(".", ""));
        if (!m.Success || (!m.Groups[1].Success && !m.Groups[2].Success && !m.Groups[3].Success))
        {
            var num = Regex.Match(text, @"([\d.]+)");
            return num.Success ? double.Parse(num.Groups[1].Value) : null;
        }
        var hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
        var mins = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        var secs = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        return Math.Round(hours * 60 + mins + secs / 60.0, 4);
    }

    static (double? min, double? max, double? avg) BoardTemps(IEnumerable<string> lines)
    {
        double? min = null, max = null, avg = null;
        foreach (var line in lines)
        {
            foreach (Match m in TempRe.Matches(line))
            {
                var v = double.Parse(m.Groups[2].Value);
                switch (m.Groups[1].Value.ToLowerInvariant())
                {
                    case "min_temp" when min is null: min = v; break;
                    case "max_temp" when max is null: max = v; break;
                    case "avg_temp" when avg is null: avg = v; break;
                }
            }
        }
        return (min, max, avg);
    }

    static (int c, int u, List<string> events) Sel(string[] lines)
    {
        var events = new List<string>();
        var c = 0;
        var u = 0;
        foreach (var line in lines)
        {
            if (line.Contains("Correctable ECC") || line.Contains("Uncorrectable ECC"))
            {
                events.Add(line.Trim());
                if (line.Contains("Uncorrectable")) u++;
                else if (line.Contains("Correctable")) c++;
            }
        }
        return (c, u, events.Take(50).ToList());
    }

    static List<string> Serials(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        return Regex.Split(raw.Trim(), @"[,\s;]+").Where(p => p.Length > 0).ToList();
    }

    static string? SerialFormat(List<string> serials)
    {
        if (serials.Count == 0) return null;
        var s = serials[0];
        foreach (var prefix in new[] { "SPG", "ST", "SFR", "11S", "2C-", "80AD", "80CE", "AD-", "CE-", "MY09" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return prefix;
        }
        return s[..Math.Min(3, s.Length)];
    }

    static Dictionary<string, string> SystemInfo(string[] lines)
    {
        var keys = new[] { "cpu_model", "board_vendor", "board_name", "board_serial", "bios_vendor", "bios_date", "bios_version" };
        var info = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            foreach (var key in keys)
            {
                var token = key + ":";
                if (line.Contains(token))
                    info[key] = line.Split(token, 2)[1].Trim();
                var eq = key + "=";
                if (line.Contains(eq) && !info.ContainsKey(key))
                    info[key] = line.Split(eq, 2)[1].Trim();
            }
        }
        return info;
    }

    static void CopyIdentity(Dictionary<string, object?> row, Dictionary<string, object?> identity, Dictionary<string, object?> session, string table)
    {
        foreach (var kv in identity)
            row[kv.Key] = kv.Value;
        row["processed_at"] = session["processed_at"];
        row["attributes"] = new Dictionary<string, object?>();
        row["diagnostics"] = session.GetValueOrDefault("diagnostics");
        row["source_chunk_ids"] = new List<object?>();
        row["extraction_metadata"] = new Dictionary<string, object?>();
        if (table == "dimm_results")
        {
            row["test_program"] = session.GetValueOrDefault("test_program");
            row["test_class"] = session.GetValueOrDefault("test_class");
            row["overall_result"] = session.GetValueOrDefault("overall_result");
            row["ddr_gen"] = session.GetValueOrDefault("ddr_gen");
        }
        if (table is "hpl_loop_metrics" or "ecc_events")
        {
            row["test_class"] = session.GetValueOrDefault("test_class");
            row["ddr_gen"] = session.GetValueOrDefault("ddr_gen");
        }
    }

    static List<Dictionary<string, object?>> DimmRows(
        string sessionId, List<string> serials, string[] lines,
        Dictionary<string, object?> identity, Dictionary<string, object?> session)
    {
        var bySn = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sn in serials)
            EnsureDimm(bySn, sessionId, sn, identity, session);

        var jsonSerials = ApplyCxlDimms(bySn, lines, sessionId, identity, session);
        if (jsonSerials.Count > 0)
        {
            foreach (var cardSn in serials)
            {
                if (jsonSerials.Contains(cardSn)) continue;
                if (bySn.TryGetValue(cardSn, out var stub)
                    && stub.GetValueOrDefault("mod_type") is null
                    && stub.GetValueOrDefault("manufacturer") is null)
                    bySn.Remove(cardSn);
            }
        }

        foreach (var line in lines)
        {
            if (line.Contains("DIMM ECC Error") || line.Contains("Error_Count="))
            {
                var kv = KvMap(line, upper: true);
                kv.TryGetValue("SN", out var sn);
                if (string.IsNullOrEmpty(sn))
                {
                    var m = Regex.Match(line, @"S/N=([^\s,]+)");
                    sn = m.Success ? m.Groups[1].Value : null;
                }
                if (string.IsNullOrEmpty(sn)) continue;
                var row = EnsureDimm(bySn, sessionId, sn, identity, session);
                if (kv.TryGetValue("SLOT", out var slot)) row["slot"] = slot;
                if (kv.TryGetValue("ERROR_COUNT", out var err) && double.TryParse(err, out var errN))
                    row["total_ecc_errors"] = (int)errN;
                var up = line.ToUpperInvariant();
                if (up.Contains("PASS")) row["result"] = "PASS";
                else if (up.Contains("FAIL")) row["result"] = "FAIL";
                else if (up.Contains("NOT_TESTED") || up.Contains("NOT TESTED")) row["result"] = "NOT_TESTED";
            }

            if (line.Contains("DIMMINFO", StringComparison.OrdinalIgnoreCase)
                || line.Contains("manu=", StringComparison.OrdinalIgnoreCase)
                || (line.Contains("CPU=") && line.Contains("SN=", StringComparison.OrdinalIgnoreCase)))
            {
                var kv = KvMap(line, upper: false);
                var sm = Regex.Match(line, @"SN=([^\s,]+)", RegexOptions.IgnoreCase);
                if (sm.Success)
                {
                    var row = EnsureDimm(bySn, sessionId, sm.Groups[1].Value, identity, session);
                    ApplyDimmFacts(row, kv, overwrite: false);
                }
            }

            var slotM = Regex.Match(line, @"(?:BOARD_SLOT|Slot)=([A-Za-z0-9\-]+)", RegexOptions.IgnoreCase);
            var snM = Regex.Match(line, @"(?:SN|S/N)=([^\s,]+)", RegexOptions.IgnoreCase);
            if (slotM.Success && snM.Success)
            {
                var row = EnsureDimm(bySn, sessionId, snM.Groups[1].Value, identity, session);
                row["slot"] = slotM.Groups[1].Value;
            }
        }
        return bySn.Values.ToList();
    }

    static Dictionary<string, object?> EnsureDimm(
        Dictionary<string, Dictionary<string, object?>> bySn,
        string sessionId, string sn,
        Dictionary<string, object?> identity, Dictionary<string, object?> session)
    {
        if (bySn.TryGetValue(sn, out var existing))
            return existing;
        var row = Schema.DefaultRow("dimm_results");
        row["session_id"] = sessionId;
        row["serial_number"] = sn;
        row["serial_prefix"] = sn.Length >= 3 ? sn[..3] : sn;
        row["part_number"] = session.GetValueOrDefault("part_number");
        CopyIdentity(row, identity, session, "dimm_results");
        bySn[sn] = row;
        return row;
    }

    static HashSet<string> ApplyCxlDimms(
        Dictionary<string, Dictionary<string, object?>> bySn,
        string[] lines,
        string sessionId,
        Dictionary<string, object?> identity,
        Dictionary<string, object?> session)
    {
        var jsonSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dieTemp = CxlDieTemps(lines).max;

        foreach (var line in lines)
        {
            var m = CxlDimmLine.Match(line);
            if (!m.Success) continue;
            var sn = m.Groups["sn"].Value.Trim().Trim('"');
            if (string.IsNullOrEmpty(sn)) continue;
            jsonSerials.Add(sn);
            var row = EnsureDimm(bySn, sessionId, sn, identity, session);
            ApplyDimmFacts(row, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dimmserial"] = sn,
                ["dimmdensity"] = m.Groups["den"].Value.Trim(),
                ["dimmtype"] = m.Groups["type"].Value.Trim(),
                ["dimmmanuf"] = m.Groups["manu"].Value.Trim(),
                ["dimmpn"] = m.Groups["pn"].Value.Trim(),
                ["dimmslotnumber"] = m.Groups["slot"].Value.Trim().TrimEnd(',')
            }, overwrite: true);
            if (dieTemp is not null && row.GetValueOrDefault("final_temp_c") is null)
                row["final_temp_c"] = dieTemp;
            if (row.GetValueOrDefault("result") is null)
                row["result"] = session.GetValueOrDefault("overall_result");
        }

        var json = ExtractCxlJson(lines);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var testResult = root.TryGetProperty("testresult", out var tr) ? tr.GetString() : null;
                if (root.TryGetProperty("dimmdetail", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in details.EnumerateArray())
                    {
                        var sn = JsonStr(item, "dimmserial");
                        if (string.IsNullOrWhiteSpace(sn)) continue;
                        jsonSerials.Add(sn);
                        var row = EnsureDimm(bySn, sessionId, sn, identity, session);
                        ApplyDimmFacts(row, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["dimmserial"] = sn,
                            ["dimmdensity"] = JsonStr(item, "dimmdensity") ?? "",
                            ["dimmtype"] = JsonStr(item, "dimmtype") ?? "",
                            ["dimmmanuf"] = JsonStr(item, "dimmmanuf") ?? "",
                            ["dimmpn"] = JsonStr(item, "dimmpn") ?? "",
                            ["dimmslotnumber"] = JsonStr(item, "dimmslotnumber") ?? ""
                        }, overwrite: true);
                        if (!string.IsNullOrWhiteSpace(testResult))
                            row["result"] = testResult.ToUpperInvariant() is "PASS" or "FAIL" or "PARTIAL" or "NOT_TESTED"
                                ? testResult.ToUpperInvariant() : testResult;
                        if (dieTemp is not null && row.GetValueOrDefault("final_temp_c") is null)
                            row["final_temp_c"] = dieTemp;
                    }
                }
            }
            catch (JsonException)
            {
                // formDimmInfoJson lines above still apply if the JSON block is malformed.
            }
        }
        return jsonSerials;
    }

    static string? ExtractCxlJson(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("cxlserial", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("Full JSON contents", StringComparison.OrdinalIgnoreCase))
                continue;
            var buf = new List<string>();
            var start = line.IndexOf('{');
            if (start >= 0) buf.Add(line[start..]);
            var depth = buf.Count == 0 ? 0 : buf[0].Count(c => c == '{') - buf[0].Count(c => c == '}');
            for (var j = i + 1; j < lines.Length && j <= i + 500; j++)
            {
                if (buf.Count == 0)
                {
                    var open = lines[j].IndexOf('{');
                    if (open < 0) continue;
                    buf.Add(lines[j][open..]);
                }
                else
                    buf.Add(lines[j]);
                depth = string.Join('\n', buf).Count(c => c == '{') - string.Join('\n', buf).Count(c => c == '}');
                if (depth <= 0 && buf.Count > 0) break;
            }
            if (buf.Count == 0) continue;
            var raw = string.Join('\n', buf);
            var sanitized = Regex.Replace(raw, @",(\s*[}\]])", "$1");
            if (sanitized.Contains("cxlserial", StringComparison.OrdinalIgnoreCase))
                return sanitized;
        }
        return null;
    }

    static string? JsonStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()
        : el.TryGetProperty(name, out v) && v.ValueKind != JsonValueKind.Undefined && v.ValueKind != JsonValueKind.Null
            ? v.ToString() : null;

    static void ApplyDimmFacts(Dictionary<string, object?> row, Dictionary<string, string> kv, bool overwrite)
    {
        string? Get(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (kv.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim().Trim('"');
            }
            return null;
        }

        void Put(string name, object? value)
        {
            if (value is null) return;
            if (value is string s && string.IsNullOrWhiteSpace(s)) return;
            if (!overwrite && row.GetValueOrDefault(name) is not null) return;
            row[name] = value;
        }

        Put("manufacturer", Get("manu", "manufacturer", "dimmmanuf"));
        Put("mod_type", Get("type", "mod_type", "dimmtype", "module_type"));
        Put("part_number", Get("dimmpn", "pn", "part"));
        Put("slot", Get("slot", "board_slot", "dimmslotnumber"));
        Put("rated_speed", Get("spd_speed", "speed", "rated_speed"));
        Put("device_width", Get("device", "device_width", "width"));
        var rank = Get("logrank", "rank", "ranks");
        if (int.TryParse(rank, out var r)) Put("ranks", r);
        Put("size_gb", ParseSizeGb(Get("size", "size_gb", "density", "dimmdensity", "gb")));
        Put("voltage_v", ToDouble(Get("volt", "voltage", "voltage_v", "vdd")));
        Put("final_temp_c", ToDouble(Get("temp", "temperature", "final_temp_c")));
    }

    static double? ParseSizeGb(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim().TrimEnd('B', 'b');
        var num = Regex.Match(t, @"([0-9.]+)");
        if (!num.Success || !double.TryParse(num.Groups[1].Value, out var n)) return null;
        if (t.Contains("M", StringComparison.OrdinalIgnoreCase) && !t.Contains("G", StringComparison.OrdinalIgnoreCase))
            return Math.Round(n / 1024.0, 4);
        if (n >= 1024) return Math.Round(n / 1024.0, 4);
        return n;
    }

    static (double? min, double? max, double? avg) CxlDieTemps(string[] lines)
    {
        var vals = new List<double>();
        foreach (var line in lines)
        {
            foreach (Match m in CxlDieTemp.Matches(line))
            {
                for (var i = 1; i < m.Groups.Count; i++)
                {
                    if (m.Groups[i].Success && double.TryParse(m.Groups[i].Value, out var v))
                        vals.Add(v);
                }
            }
        }
        if (vals.Count == 0) return (null, null, null);
        return (vals.Min(), vals.Max(), Math.Round(vals.Average(), 4));
    }

    static List<Dictionary<string, object?>> LoopRows(
        string sessionId, string[] lines,
        Dictionary<string, object?> identity, Dictionary<string, object?> session)
    {
        // PK is session_id + loop_num. Burn-in logs often print "Loop Test: 1 of N"
        // more than once; keep one row per loop number.
        var byLoop = new Dictionary<int, Dictionary<string, object?>>();
        if (!lines.Any(l => l.Contains("Loop Test:")))
            return new List<Dictionary<string, object?>>();

        Dictionary<string, object?>? current = null;
        double? gflopsAvg = null, gflopsMax = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lm = LoopTest.Match(line);
            if (lm.Success)
            {
                var loopNum = int.Parse(lm.Groups[1].Value);
                if (!byLoop.TryGetValue(loopNum, out current) || current is null)
                {
                    current = Schema.DefaultRow("hpl_loop_metrics");
                    current["session_id"] = sessionId;
                    current["loop_num"] = loopNum;
                    CopyIdentity(current, identity, session, "hpl_loop_metrics");
                    byLoop[loopNum] = current;
                }
                current["loop_total"] = int.Parse(lm.Groups[2].Value);
                var slice = lines.Skip(i).Take(80).ToArray();
                var temps = BoardTemps(slice);
                current["loop_min_temp_c"] = temps.min ?? current.GetValueOrDefault("loop_min_temp_c");
                current["loop_max_temp_c"] = temps.max ?? current.GetValueOrDefault("loop_max_temp_c");
                current["loop_avg_temp_c"] = temps.avg ?? current.GetValueOrDefault("loop_avg_temp_c");
                current["elapsed_time_min"] = DurationMin(LineValue(slice, "Elapsed Time:"))
                    ?? current.GetValueOrDefault("elapsed_time_min");
                var volts = VddRails(slice);
                if (volts.Count > 0)
                    current["vdd_rails"] = volts;
                continue;
            }
            if (line.Contains("Performance Summary (GFlops)"))
            {
                var nxt = string.Join(" ", lines.Skip(i).Take(5));
                var nums = LinpackNums.Matches(nxt).Select(m => double.Parse(m.Value)).ToList();
                if (nums.Count > 0)
                {
                    gflopsAvg = nums[0];
                    if (nums.Count > 1) gflopsMax = nums[1];
                }
            }
            if (current is not null && line.Contains("GFlops") && Regex.IsMatch(line, @"\d") && !line.Contains("Performance Summary"))
            {
                var nums = LinpackNums.Matches(line).Select(m => m.Value).ToList();
                if (nums.Count >= 4)
                {
                    if (double.TryParse(nums[0], out var sz)) current["problem_size"] = (int)sz;
                    if (double.TryParse(nums[1], out var lda)) current["lda"] = (int)lda;
                    if (double.TryParse(nums[2], out var al)) current["align_kb"] = (int)al;
                    if (double.TryParse(nums[3], out var t)) current["time_sec"] = t;
                    if (nums.Count >= 5 && double.TryParse(nums[4], out var gf)) current["gflops"] = gf;
                    if (nums.Count >= 6 && double.TryParse(nums[5], out var res)) current["residual"] = res;
                    if (nums.Count >= 7 && double.TryParse(nums[6], out var rn)) current["residual_norm"] = rn;
                }
                if (line.Contains("PASSED", StringComparison.OrdinalIgnoreCase)) current["hpl_check"] = "PASSED";
                else if (line.Contains("FAILED", StringComparison.OrdinalIgnoreCase)) current["hpl_check"] = "FAILED";
            }
        }
        var rows = byLoop.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        Dictionary<string, object?>? prev = null;
        foreach (var row in rows)
        {
            row["gflops_avg"] = gflopsAvg;
            row["gflops_max"] = gflopsMax;
            if (prev is not null && row.GetValueOrDefault("gflops") is double g && prev.GetValueOrDefault("gflops") is double pg)
                row["gflops_delta"] = g - pg;
            prev = row;
        }
        return rows;
    }

    static Dictionary<string, double> VddRails(IEnumerable<string> lines)
    {
        var rails = new Dictionary<string, double>();
        foreach (var line in lines)
        {
            if (!line.Contains("Volts") && !line.Contains("VDD", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (Match m in VoltRe.Matches(line))
                rails[m.Groups[1].Value] = double.Parse(m.Groups[2].Value);
        }
        return rails;
    }

    static List<Dictionary<string, object?>> EccRows(
        string sessionId, string[] lines,
        Dictionary<string, object?> identity, Dictionary<string, object?> session,
        List<Dictionary<string, object?>> dimms)
    {
        var rows = new List<Dictionary<string, object?>>();
        var seq = new Dictionary<string, int>();
        var currentLoop = 0;
        foreach (var line in lines)
        {
            var lm = LoopTest.Match(line);
            if (lm.Success) currentLoop = int.Parse(lm.Groups[1].Value);
            if (!line.Contains("set_ecc_fail_xml_result") && !line.Contains("DIMMINFO_SECC_COUNT"))
                continue;
            var kv = KvMap(line, upper: true);
            kv.TryGetValue("SN", out var sn);
            if (string.IsNullOrEmpty(sn))
            {
                var m = Regex.Match(line, @"SN=([^\s,]+)", RegexOptions.IgnoreCase);
                sn = m.Success ? m.Groups[1].Value : null;
            }
            if (string.IsNullOrEmpty(sn)) continue;
            var key = $"{sn}|{currentLoop}";
            seq[key] = seq.GetValueOrDefault(key) + 1;
            var row = Schema.DefaultRow("ecc_events");
            row["session_id"] = sessionId;
            row["serial_number"] = sn;
            row["loop_num"] = currentLoop;
            row["event_seq"] = seq[key];
            row["board_slot"] = kv.GetValueOrDefault("BOARD_SLOT");
            row["dimminfo_secc_count"] = ToInt(kv.GetValueOrDefault("DIMMINFO_SECC_COUNT"));
            row["internal_secc_count"] = ToInt(kv.GetValueOrDefault("INTERNAL_SECC_COUNT"));
            row["internal_secc_shadow_count"] = ToInt(kv.GetValueOrDefault("INTERNAL_SECC_SHADOW_COUNT"));
            row["temperature_c"] = ToDouble(kv.GetValueOrDefault("TEMP") ?? kv.GetValueOrDefault("TEMPERATURE"));
            row["voltage_v"] = ToDouble(kv.GetValueOrDefault("VOLT") ?? kv.GetValueOrDefault("VOLTAGE"));
            var fft = kv.GetValueOrDefault("FIRST_FAILURE_TIME") ?? LineKv(line, "First_Failure_Time");
            row["first_failure_elapsed"] = fft;
            var mins = DurationMin(fft);
            row["first_failure_sec"] = mins is null ? null : (int)(mins.Value * 60);
            string? state = null;
            foreach (var token in new[] { "First", "Unchanged", "Increase", "Decrease" })
            {
                if (Regex.IsMatch(line, $@"\b{token}\b")) { state = token; break; }
            }
            row["ecc_state"] = state;
            row["is_first_error"] = state == "First";
            CopyIdentity(row, identity, session, "ecc_events");
            rows.Add(row);
            if (!dimms.Any(d => string.Equals(d.GetValueOrDefault("serial_number") as string, sn, StringComparison.OrdinalIgnoreCase)))
            {
                var drow = Schema.DefaultRow("dimm_results");
                drow["session_id"] = sessionId;
                drow["serial_number"] = sn;
                drow["slot"] = row.GetValueOrDefault("board_slot");
                CopyIdentity(drow, identity, session, "dimm_results");
                dimms.Add(drow);
            }
        }
        return rows;
    }

    static Dictionary<string, string> KvMap(string line, bool upper)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Kv.Matches(line))
        {
            var key = upper ? m.Groups[1].Value.ToUpperInvariant() : m.Groups[1].Value.ToLowerInvariant();
            map[key] = m.Groups["val"].Value;
        }
        return map;
    }

    static string? LineKv(string line, string key)
    {
        var m = Regex.Match(line, $@"{Regex.Escape(key)}\s*[=:]\s*([^\s,]+(?:\s+[^\s,]+)?)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    static int? ToInt(string? value) => double.TryParse(value, out var n) ? (int)n : null;
    static double? ToDouble(string? value) => double.TryParse(value, out var n) ? n : null;

    static void ApplyExtras(
        Dictionary<string, List<Dictionary<string, object?>>> bundle,
        string[] lines,
        ExtrasPack extras)
    {
        foreach (var spec in extras.Columns)
        {
            if (string.IsNullOrWhiteSpace(spec.Table) || string.IsNullOrWhiteSpace(spec.Name))
                continue;
            var value = TokenValue(lines, spec.Token);
            if (!bundle.TryGetValue(spec.Table, out var rows) || rows.Count == 0)
                continue;
            var grain = spec.Grain?.ToLowerInvariant() ?? "";
            if (grain == "session" || spec.Table is "test_sessions" or "extraction_manifest")
                rows[0][spec.Name] = value;
            else
            {
                foreach (var row in rows)
                {
                    if (!row.ContainsKey(spec.Name) || row[spec.Name] is null)
                        row[spec.Name] = value;
                }
            }
        }
    }

    static string? TokenValue(string[] lines, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        foreach (var line in lines)
        {
            var i = line.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var after = line[(i + token.Length)..].Trim();
            while (after.StartsWith('=') || after.StartsWith(':'))
                after = after[1..].Trim();
            if (string.IsNullOrEmpty(after)) continue;
            var value = after.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(value) || value is ":" or "=") continue;
            return value;
        }
        return null;
    }
}
