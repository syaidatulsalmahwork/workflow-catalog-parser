using Microsoft.Extensions.Configuration;

namespace ParserCodeReference;

internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            var basePath = AppContext.BaseDirectory;
            var config = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("Config/appsettings.json", optional: false, reloadOnChange: false)
                .Build();

            var connection = config.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connection))
            {
                Console.WriteLine("DefaultConnection is missing in Config/appsettings.json.");
                return 1;
            }

            var inbox = config["FileProcessing:PreParsedFolder"];
            var bad = config["FileProcessing:BadFolder"];
            var archive = config["FileProcessing:MoveFolder"];
            if (string.IsNullOrWhiteSpace(inbox) || string.IsNullOrWhiteSpace(bad) || string.IsNullOrWhiteSpace(archive))
            {
                Console.WriteLine("Set FileProcessing PreParsedFolder, BadFolder, and MoveFolder in Config/appsettings.json.");
                return 1;
            }

            Directory.CreateDirectory(inbox);
            Directory.CreateDirectory(bad);
            Directory.CreateDirectory(archive);

            var extras = ExtrasLoader.Load(Path.Combine(basePath, "Extras"));
            foreach (var col in ExtraExtract.Columns)
            {
                extras.Columns.RemoveAll(c =>
                    c.Table.Equals(col.Table, StringComparison.OrdinalIgnoreCase)
                    && c.Name.Equals(col.Name, StringComparison.OrdinalIgnoreCase));
                extras.Columns.Add(col);
            }
            var files = new FileService(inbox, bad, archive);
            var sql = new SqlStore(connection);

            Console.WriteLine($"START: inbox={inbox}");
            var zips = files.FetchZipFiles();
            if (zips.Count == 0)
            {
                Console.WriteLine("No log files found to process.");
                Console.WriteLine("Put *.zip in PreParsedFolder, then run again (F5).");
                return 0;
            }

            var okZips = new List<string>();
            var parsed = 0;
            foreach (var zip in zips)
            {
                Console.WriteLine($"Unzipping {Path.GetFileName(zip)}...");
                var (logs, reason) = files.UnzipInMemory(zip);
                if (reason is not null || logs.Count == 0)
                {
                    Console.WriteLine($"BAD {Path.GetFileName(zip)}: {reason ?? "no logs"}");
                    files.MoveToBad(zip);
                    continue;
                }

                foreach (var log in logs)
                {
                    var bundle = CatalogParser.Parse(log, extras);
                    var session = bundle["test_sessions"][0];
                    Console.WriteLine(
                        $"{session["log_file"]} grammar={session["grammar"]} class={session["test_class"]} " +
                        $"dimms={bundle["dimm_results"].Count} loops={bundle["hpl_loop_metrics"].Count} ecc={bundle["ecc_events"].Count}");
                    Console.WriteLine($"session_id={session["session_id"]}");
                    sql.Save(bundle, extras);
                    parsed++;
                }
                okZips.Add(zip);
            }

            if (okZips.Count > 0)
                files.MoveToArchive(okZips);

            Console.WriteLine($"{parsed} log file(s) were successfully parsed.");
            Console.WriteLine("Application has shut down.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex);
            return 1;
        }
    }
}
