using System.Text.Json;

namespace Contracts.Harness;

/// <summary>
/// Serializes a captured payload the way every e.central producer in the workspace does:
/// <c>JsonSerializer.Serialize(payload)</c> with default options (PascalCase CLR names, enums as
/// numbers, nulls written). See OE.Shared, Platform.Shared and Backoffice.Shared MessageBroker.
/// </summary>
public static class ProducerJson
{
    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, payload.GetType());
}
