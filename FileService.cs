using System.IO.Compression;
using System.Text;

namespace ParserCodeReference;

public sealed class ExtractedLog
{
    public required string FileName { get; init; }
    public required string ZipPath { get; init; }
    public required string Content { get; init; }
    public required byte[] RawBytes { get; init; }
}

public sealed class FileService
{
    public string PreParsedFolder { get; }
    public string BadFolder { get; }
    public string MoveFolder { get; }

    public FileService(string preParsed, string bad, string archive)
    {
        PreParsedFolder = preParsed;
        BadFolder = bad;
        MoveFolder = archive;
    }

    public List<string> FetchZipFiles()
    {
        if (!Directory.Exists(PreParsedFolder))
            throw new DirectoryNotFoundException($"Log directory not found: {PreParsedFolder}");

        return Directory.GetFiles(PreParsedFolder, "*.zip").ToList();
    }

    public (List<ExtractedLog> Logs, string? BadReason) UnzipInMemory(string zipPath)
    {
        var logs = new List<ExtractedLog>();
        try
        {
            using var zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            if (archive.Entries.Count == 0)
                return (logs, "ZIP file is empty.");

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;
                var name = entry.FullName.Replace('\\', '/');
                if (!(name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                      || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                    continue;

                using var entryStream = entry.Open();
                using var ms = new MemoryStream();
                entryStream.CopyTo(ms);
                var data = ms.ToArray();
                logs.Add(new ExtractedLog
                {
                    FileName = Path.GetFileName(entry.Name),
                    ZipPath = zipPath,
                    Content = Decode(data),
                    RawBytes = data
                });
            }

            if (logs.Count == 0)
                return (logs, "ZIP contains no .log or .txt files.");
            return (logs, null);
        }
        catch (Exception ex)
        {
            return (logs, $"Failed to process ZIP file: {ex.Message}");
        }
    }

    public void MoveToBad(string zipPath)
    {
        Directory.CreateDirectory(BadFolder);
        var dest = Path.Combine(BadFolder, Path.GetFileName(zipPath));
        File.Move(zipPath, dest, overwrite: true);
        Console.WriteLine($"Moved bad ZIP to Bad folder: {Path.GetFileName(zipPath)}");
    }

    public void MoveToArchive(IEnumerable<string> zipPaths)
    {
        Directory.CreateDirectory(MoveFolder);
        foreach (var zip in zipPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(zip))
            {
                Console.WriteLine($"Zip file not found for move: {zip}");
                continue;
            }
            var dest = Path.Combine(MoveFolder, Path.GetFileName(zip));
            File.Move(zip, dest, overwrite: true);
            Console.WriteLine($"Archived {Path.GetFileName(zip)}");
        }
    }

    static string Decode(byte[] data)
    {
        try
        {
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return Encoding.Latin1.GetString(data);
        }
    }
}
