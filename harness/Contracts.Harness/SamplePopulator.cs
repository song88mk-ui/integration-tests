using System.Collections;
using System.Reflection;

namespace Contracts.Harness;

/// <summary>
/// Builds producer inputs with every settable member holding a distinct, non-default value, so
/// any field that comes out default on the consumer side was lost on the way, not left unset.
/// </summary>
/// <remarks>
/// Strings are <c>"&lt;member&gt;-&lt;n&gt;"</c>, numbers count up from 1, bools are true, enums
/// take their first non-zero value, and each collection gets one element. A type already being
/// built higher up the graph is left null, so self-referencing models terminate. Producers with
/// domain rules (a status that must be open, an id that must match) adjust the result afterwards.
/// </remarks>
public sealed class SamplePopulator
{
    private const int MaxDepth = 8;

    private readonly Stack<Type> _building = new();
    private int _next;

    public static T Fill<T>() => (T)new SamplePopulator().Create(typeof(T), typeof(T).Name, 0)!;

    private object? Create(Type type, string name, int depth)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return Create(underlying, name, depth);

        if (type == typeof(string))
            return $"{name}-{++_next}";
        if (type == typeof(bool))
            return true;
        if (type.IsEnum)
            return FirstNonZero(type);
        if (type == typeof(DateTime))
            return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(++_next);
        if (type == typeof(DateTimeOffset))
            return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(++_next);
        if (type == typeof(TimeSpan))
            return TimeSpan.FromSeconds(++_next);
        if (type == typeof(Guid))
            return new Guid(++_next, 0, 0, new byte[8]);
        if (type.IsPrimitive || type == typeof(decimal))
            return Convert.ChangeType(++_next % 100 + 1, type);

        if (depth > MaxDepth || _building.Contains(type))
            return null;

        _building.Push(type);
        try
        {
            if (type.IsArray)
                return CreateArray(type.GetElementType()!, name, depth);
            if (TryCreateCollection(type, name, depth, out var collection))
                return collection;
            if (type.IsInterface || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                return null;

            var instance = Activator.CreateInstance(type)!;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.CanWrite && property.SetMethod!.IsPublic && property.GetIndexParameters().Length == 0)
                    property.SetValue(instance, Create(property.PropertyType, property.Name, depth + 1));
            }

            return instance;
        }
        finally
        {
            _building.Pop();
        }
    }

    private Array CreateArray(Type elementType, string name, int depth)
    {
        var array = Array.CreateInstance(elementType, 1);
        array.SetValue(Create(elementType, name, depth + 1), 0);
        return array;
    }

    private bool TryCreateCollection(Type type, string name, int depth, out object? collection)
    {
        collection = null;
        if (!type.IsGenericType)
            return false;

        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();

        if (arguments.Length == 2 && (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>)
            || definition == typeof(IReadOnlyDictionary<,>)))
        {
            var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments))!;
            dictionary.Add(Create(arguments[0], name, depth + 1)!, Create(arguments[1], name, depth + 1));
            collection = dictionary;
            return true;
        }

        if (arguments.Length == 1 && (definition == typeof(List<>) || definition == typeof(IList<>)
            || definition == typeof(ICollection<>) || definition == typeof(IEnumerable<>)
            || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)))
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(arguments))!;
            list.Add(Create(arguments[0], name, depth + 1));
            collection = list;
            return true;
        }

        return false;
    }

    private static object FirstNonZero(Type enumType)
    {
        foreach (var value in Enum.GetValues(enumType))
        {
            if (Convert.ToInt64(value) != 0)
                return value;
        }

        return Enum.GetValues(enumType).GetValue(0)!;
    }
}
