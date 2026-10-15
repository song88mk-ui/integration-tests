using System.Text.RegularExpressions;

namespace Contracts.Harness;

/// <summary>
/// One message as a producer actually put it on e.central: the routing key, the AMQP
/// <c>type</c> property consumers dispatch on, and the serialized body.
/// </summary>
/// <param name="Variant">
/// Names one of several producer paths that publish the same routing key and type, for
/// example OE.Sportsbook.Service's two OnBetSettlement builders. Empty for the only one.
/// </param>
public sealed record Golden(string RoutingKey, string MessageType, string Json, string Variant = "");

/// <summary>
/// Golden payloads on disk, laid out as <c>&lt;root&gt;/&lt;routing key&gt;/&lt;message type&gt;[~&lt;variant&gt;].json</c>.
/// Emitters write them from the real producer code path; checkers read every variant back into
/// the consumer's own class.
/// </summary>
public sealed partial class GoldenStore
{
    public const string DirectoryVariable = "CONTRACT_GOLDEN_DIR";
    private const char VariantSeparator = '~';

    public GoldenStore(string root) => Root = root;

    public string Root { get; }

    /// <summary>The store named by <see cref="DirectoryVariable"/>, or null when it is unset.</summary>
    public static GoldenStore? FromEnvironment()
    {
        var root = Environment.GetEnvironmentVariable(DirectoryVariable);
        return string.IsNullOrWhiteSpace(root) ? null : new GoldenStore(root);
    }

    public string Write(Golden golden)
    {
        Validate(golden.RoutingKey, nameof(golden.RoutingKey));
        Validate(golden.MessageType, nameof(golden.MessageType));
        if (golden.Variant.Length > 0)
            Validate(golden.Variant, nameof(golden.Variant));

        var name = golden.Variant.Length == 0
            ? golden.MessageType
            : $"{golden.MessageType}{VariantSeparator}{golden.Variant}";
        var path = Path.Combine(Root, golden.RoutingKey, name + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, golden.Json);
        return path;
    }

    /// <summary>
    /// Every golden for this routing key and type, one per producer variant, in name order.
    /// Throws when there is none, so a checker can never pass because the emitter stopped
    /// producing the message.
    /// </summary>
    public IReadOnlyList<Golden> ReadAll(string routingKey, string messageType)
    {
        Validate(routingKey, nameof(routingKey));
        Validate(messageType, nameof(messageType));

        var directory = Path.Combine(Root, routingKey);
        var goldens = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.json")
                .Select(path => (path, name: Path.GetFileNameWithoutExtension(path)))
                .Select(f => (f.path, parts: f.name.Split(VariantSeparator, 2)))
                .Where(f => f.parts[0] == messageType)
                .OrderBy(f => f.path, StringComparer.Ordinal)
                .Select(f => new Golden(routingKey, messageType, File.ReadAllText(f.path), f.parts.Length > 1 ? f.parts[1] : ""))
                .ToList()
            : [];

        if (goldens.Count == 0)
            throw new FileNotFoundException(
                $"No golden for routing key '{routingKey}', type '{messageType}'. Run the emitters first.",
                Path.Combine(directory, messageType + ".json"));

        return goldens;
    }

    // Routing keys are dotted words (r.oe.bet.settlement), types and variants are identifiers,
    // so all are safe path segments once separators, "~" and "." / ".." are ruled out.
    private static void Validate(string segment, string name)
    {
        if (!SafeSegment().IsMatch(segment) || segment is "." or "..")
            throw new ArgumentException($"'{segment}' is not a valid golden path segment.", name);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeSegment();
}
