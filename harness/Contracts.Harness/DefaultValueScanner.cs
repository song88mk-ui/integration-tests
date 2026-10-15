using System.Collections;
using System.Reflection;

namespace Contracts.Harness;

/// <summary>
/// Lists every member of a deserialized consumer object that still holds its default value.
/// Emitters populate every producer field with a non-default value, so a default on the
/// consumer side means the field did not cross the wire: renamed, retyped, or never sent.
/// </summary>
/// <remarks>
/// Paths drop list indices (<c>Markets[].Selections[].Od</c>) so one entry covers every
/// element and allow-lists stay short. Empty strings, empty collections and enum value 0 count
/// as defaults; a consumer field that legitimately stays default goes in the allow-list.
/// </remarks>
public static class DefaultValueScanner
{
    private const int MaxDepth = 32;

    public static IReadOnlyList<string> Scan(object? root, IEnumerable<string>? allowed = null)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        if (root is null)
        {
            found.Add("$");
        }
        else
        {
            var onPath = new HashSet<object>(ReferenceEqualityComparer.Instance);
            Walk(root, "", found, onPath, 0);
        }

        if (allowed is not null)
            found.ExceptWith(allowed);

        return found.ToList();
    }

    private static void Walk(object value, string path, SortedSet<string> found, HashSet<object> onPath, int depth)
    {
        if (depth > MaxDepth || !onPath.Add(value))
            return;

        try
        {
            switch (value)
            {
                case IDictionary dictionary:
                    foreach (DictionaryEntry entry in dictionary)
                        Visit(entry.Value, $"{path}[{entry.Key}]", found, onPath, depth);
                    break;

                case IEnumerable sequence and not string:
                    foreach (var item in sequence)
                        Visit(item, path + "[]", found, onPath, depth);
                    break;

                default:
                    foreach (var (name, memberValue) in Members(value))
                        Visit(memberValue, path.Length == 0 ? name : $"{path}.{name}", found, onPath, depth);
                    break;
            }
        }
        finally
        {
            onPath.Remove(value);
        }
    }

    private static void Visit(object? value, string path, SortedSet<string> found, HashSet<object> onPath, int depth)
    {
        if (IsDefault(value))
        {
            found.Add(path);
            return;
        }

        if (!IsLeaf(value!.GetType()))
            Walk(value, path, found, onPath, depth + 1);
    }

    private static IEnumerable<(string Name, object? Value)> Members(object value)
    {
        var type = value.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
                yield return (property.Name, property.GetValue(value));
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            yield return (field.Name, field.GetValue(value));
    }

    private static bool IsDefault(object? value) => value switch
    {
        null => true,
        string text => text.Length == 0,
        ICollection collection => collection.Count == 0,
        _ when value.GetType().IsValueType => value.Equals(Activator.CreateInstance(value.GetType())),
        _ => false,
    };

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(string)
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan)
        || type == typeof(Guid)
        || type.FullName == "System.Text.Json.JsonElement";
}
