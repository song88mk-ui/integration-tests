using System.Text.Json;
using Scan.Assemblies;

// Usage: Scan.Assemblies <images-env-dir>
// Scans every extracted image under the directory (one folder per image, as
// scripts/pull_images.py writes them) and writes <images-env-dir>/scan.json for routing/.
if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: Scan.Assemblies <.images/<env> directory>");
    return 2;
}

var result = new SortedDictionary<string, ImageScan>(StringComparer.Ordinal);
foreach (var dir in Directory.GetDirectories(args[0]).Order(StringComparer.Ordinal))
{
    var scan = ImageScanner.Scan(dir);
    result[Path.GetFileName(dir)] = scan;
    Console.WriteLine($"   {Path.GetFileName(dir)}: {scan.Publications.Count} publication(s)"
        + (scan.Subscribes ? ", subscribes" : "") + (scan.Entry is null ? "  (no .NET entry assembly)" : ""));
}

var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
File.WriteAllText(Path.Combine(args[0], "scan.json"), JsonSerializer.Serialize(result, options) + "\n");
return 0;
