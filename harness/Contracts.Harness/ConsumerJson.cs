using System.Text.Json;

namespace Contracts.Harness;

/// <summary>
/// Reads a golden the way the consumers on e.central do: <c>JsonSerializer.Deserialize&lt;T&gt;(payload)</c>
/// with default options, which match property names case-sensitively and skip unknown members.
/// OE.Job.Cleanup and OE.Translation.Job set <c>PropertyNameCaseInsensitive</c>; their checkers
/// pass those options.
/// </summary>
public static class ConsumerJson
{
    public static T Deserialize<T>(Golden golden, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(golden.Json, options)
        ?? throw new InvalidOperationException($"Golden {golden.RoutingKey}/{golden.MessageType} deserialized to null.");
}
