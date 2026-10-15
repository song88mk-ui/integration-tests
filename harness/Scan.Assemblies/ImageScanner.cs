using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scan.Assemblies;

public sealed record Publication(string Key, string Assembly, string Method);

public sealed record ImageScan(string? Entry, bool Subscribes, IReadOnlyList<Publication> Publications);

/// <summary>
/// Scans one extracted image (its /app folder). The entry assembly is the one with a
/// runtimeconfig.json. First-party assemblies are the entry, every "project" library in its
/// deps.json, and the shared packages from the private feed (OE.Shared in bet-genius).
/// Publications come from every first-party assembly; "subscribes" only from the entry, as the
/// source scan only looked in the service's own project.
/// </summary>
public static partial class ImageScanner
{
    [GeneratedRegex(@"^(OE|BG|BO|Platform|Backoffice)\.")]
    private static partial Regex FirstPartyPackage();

    public static ImageScan Scan(string appDir)
    {
        var runtimeConfig = Directory.GetFiles(appDir, "*.runtimeconfig.json").SingleOrDefault();
        if (runtimeConfig is null)
            return new ImageScan(null, false, []);
        var entry = Path.GetFileName(runtimeConfig)[..^".runtimeconfig.json".Length];

        var firstParty = new SortedSet<string>(StringComparer.Ordinal) { entry };
        var deps = Path.Combine(appDir, entry + ".deps.json");
        if (File.Exists(deps))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(deps));
            foreach (var library in json.RootElement.GetProperty("libraries").EnumerateObject())
            {
                var name = library.Name.Split('/')[0];
                var type = library.Value.GetProperty("type").GetString();
                if (type == "project" || FirstPartyPackage().IsMatch(name))
                    firstParty.Add(name);
            }
        }

        var publications = new List<Publication>();
        var subscribes = false;
        foreach (var name in firstParty)
        {
            var path = Path.Combine(appDir, name + ".dll");
            if (!File.Exists(path)) continue;
            var result = ScanAssembly(path);
            publications.AddRange(result.Publications);
            if (name == entry) subscribes = result.Subscribes;
        }

        return new ImageScan(entry, subscribes,
            publications.Distinct().OrderBy(p => p.Key, StringComparer.Ordinal)
                .ThenBy(p => p.Method, StringComparer.Ordinal).ToList());
    }

    public static (List<Publication> Publications, bool Subscribes) ScanAssembly(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return ([], false);
        var reader = pe.GetMetadataReader();
        var assembly = reader.GetString(reader.GetAssemblyDefinition().Name);
        var provider = new TypeNameProvider();

        // SCAN_TRACE=<part of Type.Method> prints the symbolic stack at each instruction of the
        // matching methods, to see why a key was or was not found.
        var trace = Environment.GetEnvironmentVariable("SCAN_TRACE");
        var names = new IlInterpreter(reader, provider);

        var publications = new List<Publication>();
        var subscribes = false;
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                var where = names.TypeName(typeHandle) + "." + reader.GetString(method.Name);
                var traced = !string.IsNullOrEmpty(trace) && where.Contains(trace, StringComparison.Ordinal);
                var interpreter = new IlInterpreter(reader, provider) { Trace = traced ? Console.Error : null };
                interpreter.Run(pe.GetMethodBody(method.RelativeVirtualAddress));
                subscribes |= interpreter.Subscribes;
                publications.AddRange(interpreter.Keys.Select(k => new Publication(k, assembly, where)));
            }
        }
        return (publications, subscribes);
    }
}
