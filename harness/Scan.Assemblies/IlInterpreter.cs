using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;

namespace Scan.Assemblies;

/// <summary>
/// Walks one method body with a symbolic stack that only tracks strings, the same question the
/// old source scan answered with a regex over each statement: which "r." literal reaches a
/// Publish / CPublish call or a RoutingKey assignment.
///
/// Values the IL builds from constants stay exact: const fields are inlined as ldstr, so
/// $"r.oe.sb.engine.{MessageType.ON_ODDS_CHANGE.ToLower()}" resolves fully. Anything else
/// (a parameter, a call result) becomes "*", one unknown word, as interpolations did in the
/// source scan. A local or field keeps its value only when the method assigns it exactly one
/// distinct value, so a variable set differently on two branches is "*". The walk is linear and
/// the stack is reset after every unconditional jump, which is where the C# compiler leaves it
/// empty.
/// </summary>
internal sealed partial class IlInterpreter(MetadataReader reader, TypeNameProvider provider)
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    [GeneratedRegex(@"^set_\w*[Rr]outingKey$")]
    private static partial Regex RoutingKeySetter();

    [GeneratedRegex(@"[Rr]outingKey\w*$")]
    private static partial Regex RoutingKeyField();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex FormatItem();

    public List<string> Keys { get; } = [];
    public bool Subscribes { get; private set; }

    private abstract record Value;
    private sealed record Unknown : Value { public static readonly Unknown It = new(); }
    private sealed record Null : Value { public static readonly Null It = new(); }
    private sealed record Text(string S) : Value;
    private sealed record LocalAddress(int Index) : Value;
    private sealed record Array(Value[] Items) : Value;
    private sealed record Handler(StringBuilder Builder) : Value;

    private const string Wildcard = "*";

    public TextWriter? Trace { get; init; }

    public void Run(MethodBodyBlock body)
    {
        // Pass 1 only learns which locals and fields get more than one distinct value.
        var assigned = new Dictionary<string, HashSet<Value>>();
        Walk(body, assigned, record: false);
        Walk(body, assigned, record: true);
    }

    private void Walk(MethodBodyBlock body, Dictionary<string, HashSet<Value>> assigned, bool record)
    {
        recording = record;
        var il = body.GetILReader();
        var stack = new List<Value>();
        var locals = new Dictionary<int, Value>();
        var fields = new Dictionary<string, Value>();
        var handlers = new Dictionary<int, StringBuilder>();

        Value Load(string slot, Value? current) =>
            current is not (null or Null) && (!assigned.TryGetValue(slot, out var values) || values.Count == 1)
                ? current : Unknown.It;
        void Store(string slot, Value value)
        {
            if (!assigned.TryGetValue(slot, out var values)) assigned[slot] = values = [];
            // An async method clears its hoisted locals with null when it completes; that is not
            // a second value.
            if (!record && value is not Null) values.Add(value);
        }

        Value Pop() { if (stack.Count == 0) return Unknown.It; var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v; }
        Value[] PopN(int n) { var a = new Value[n]; for (var i = n - 1; i >= 0; i--) a[i] = Pop(); return a; }
        void Push(Value v) => stack.Add(v);

        while (il.RemainingBytes > 0)
        {
            short code = il.ReadByte();
            if (code == 0xFE) code = unchecked((short)(0xFE00 | il.ReadByte()));
            var op = OpCodesByValue[code];
            if (record) Trace?.WriteLine($"  {op.Name,-12} [{string.Join(", ", stack)}]");

            int operand = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: operand = il.ReadByte(); break;
                case OperandType.InlineVar: operand = il.ReadUInt16(); break;
                case OperandType.InlineI8:
                case OperandType.InlineR: il.Offset += 8; break;
                case OperandType.InlineSwitch: { var n = il.ReadInt32(); il.Offset += 4 * n; break; }
                default: operand = il.ReadInt32(); break;
            }

            if (op == OpCodes.Ldstr)
            {
                Push(new Text(reader.GetUserString(MetadataTokens.UserStringHandle(operand))));
                continue;
            }
            if (TryLocal(op, operand, out var index, out var kind))
            {
                switch (kind)
                {
                    case LocalKind.Load: Push(Load($"local{index}", locals.GetValueOrDefault(index))); break;
                    case LocalKind.Store:
                    {
                        var value = Pop();
                        locals[index] = value;
                        Store($"local{index}", value);
                        // An interpolated string handler built with newobj (async methods) lives
                        // in a local and is used through ldloca from then on.
                        if (value is Handler h) handlers[index] = h.Builder;
                        break;
                    }
                    case LocalKind.Address: Push(new LocalAddress(index)); break;
                }
                continue;
            }
            if (op == OpCodes.Ldnull) { Push(Null.It); continue; }
            if (op == OpCodes.Dup) { var v = Pop(); Push(v); Push(v); continue; }
            if (op == OpCodes.Newarr)
            {
                Pop();
                // C# array initializers store elements in index order, so slots fill in turn.
                Push(new Array(new Value[64]));
                continue;
            }
            if (op == OpCodes.Stelem_Ref || op == OpCodes.Stelem)
            {
                var value = Pop(); Pop(); var array = Pop();
                if (array is Array a)
                {
                    var slot = System.Array.IndexOf(a.Items, null);
                    if (slot >= 0) a.Items[slot] = value;
                }
                continue;
            }
            if (op == OpCodes.Stfld || op == OpCodes.Stsfld)
            {
                var value = Pop();
                if (op == OpCodes.Stfld) Pop();
                var name = FieldName(MetadataTokens.EntityHandle(operand));
                fields[name] = value;
                Store("field " + name, value);
                if (RoutingKeyField().IsMatch(name)) Record(value);
                continue;
            }
            if (op == OpCodes.Ldfld || op == OpCodes.Ldsfld)
            {
                if (op == OpCodes.Ldfld) Pop();
                var name = FieldName(MetadataTokens.EntityHandle(operand));
                Push(Load("field " + name, fields.GetValueOrDefault(name)));
                continue;
            }
            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
            {
                Call(op, MetadataTokens.EntityHandle(operand), PopN, Push, handlers);
                continue;
            }
            if (op == OpCodes.Ret || op == OpCodes.Throw || op == OpCodes.Rethrow
                || op == OpCodes.Br || op == OpCodes.Br_S || op == OpCodes.Leave || op == OpCodes.Leave_S
                || op == OpCodes.Endfinally || op == OpCodes.Endfilter)
            {
                stack.Clear();
                continue;
            }

            PopN(PopCount(op.StackBehaviourPop));
            for (var i = PushCount(op.StackBehaviourPush); i > 0; i--) Push(Unknown.It);
        }
    }

    private void Call(OpCode op, EntityHandle handle, Func<int, Value[]> popN, Action<Value> push,
        Dictionary<int, StringBuilder> handlers)
    {
        var (type, name, signature) = Describe(handle);
        var instance = signature.Header.IsInstance && op != OpCodes.Newobj;
        var args = popN(signature.ParameterTypes.Length + (instance ? 1 : 0));
        var parameters = instance ? args[1..] : args;
        var returns = op == OpCodes.Newobj || signature.ReturnType != "System.Void";

        if (name is "add_MessageReceived" or "add_Received") Subscribes = true;

        if (name is "Publish" or "CPublish")
        {
            foreach (var arg in parameters) Record(arg);
        }
        else if (RoutingKeySetter().IsMatch(name) && parameters.Length == 1)
        {
            Record(parameters[0]);
        }

        Value result = Unknown.It;
        if (op == OpCodes.Newobj && type == "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler")
        {
            result = new Handler(new StringBuilder());
        }
        else if (type == "System.String")
        {
            result = name switch
            {
                "Concat" => Concat(parameters),
                "Format" => Format(parameters),
                "ToLower" or "ToLowerInvariant" when args[0] is Text t => new Text(t.S.ToLowerInvariant()),
                "ToUpper" or "ToUpperInvariant" when args[0] is Text t => new Text(t.S.ToUpperInvariant()),
                _ => Unknown.It,
            };
        }
        else if (type == "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler"
                 && instance && args[0] is LocalAddress handler)
        {
            switch (name)
            {
                case ".ctor": handlers[handler.Index] = new StringBuilder(); break;
                case "AppendLiteral": Append(handlers, handler.Index, parameters[0]); break;
                case "AppendFormatted": Append(handlers, handler.Index, parameters[0] is Text ? parameters[0] : Unknown.It); break;
                case "ToStringAndClear":
                    result = handlers.Remove(handler.Index, out var built) ? new Text(built.ToString()) : Unknown.It;
                    break;
            }
        }

        if (returns) push(result);
    }

    private static void Append(Dictionary<int, StringBuilder> handlers, int index, Value value)
    {
        if (handlers.TryGetValue(index, out var builder))
            builder.Append(value is Text t ? t.S : Wildcard);
    }

    private static Value Concat(Value[] parameters)
    {
        var parts = parameters is [Array a] ? a.Items.Where(i => i is not null).ToArray() : parameters;
        if (!parts.Any(p => p is Text)) return Unknown.It;
        return new Text(string.Concat(parts.Select(p => p is Text t ? t.S : Wildcard)));
    }

    private static Value Format(Value[] parameters)
    {
        if (parameters.Length == 0 || parameters[0] is not Text format) return Unknown.It;
        var items = parameters is [_, Array a] ? a.Items : parameters[1..];
        return new Text(FormatItem().Replace(format.S, m =>
        {
            var i = int.Parse(m.Groups[1].Value);
            return i < items.Length && items[i] is Text t ? t.S : Wildcard;
        }));
    }

    private bool recording;

    private void Record(Value value)
    {
        if (!recording || value is not Text { S: var key } || !key.StartsWith("r.", StringComparison.Ordinal) || key.Length < 3)
            return;
        // "r.bet.betstatus." + clientCode with an unknown suffix already ends in "*"; a
        // trailing dot left by a dropped part means the same.
        Keys.Add(key.EndsWith('.') ? key + Wildcard : key);
    }

    private (string Type, string Name, MethodSignature<string> Signature) Describe(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return (TypeName(method.GetDeclaringType()), reader.GetString(method.Name),
                    method.DecodeSignature(provider, null));
            }
            case HandleKind.MemberReference:
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                return (ParentName(member.Parent), reader.GetString(member.Name),
                    member.DecodeMethodSignature(provider, null));
            }
            case HandleKind.MethodSpecification:
                return Describe(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
            default:
                throw new InvalidOperationException($"Unexpected call target {handle.Kind}");
        }
    }

    private string ParentName(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => TypeName((TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => TypeName((TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)parent)
            .DecodeSignature(provider, null),
        _ => "?",
    };

    private string FieldName(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.FieldDefinition => reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
        HandleKind.MemberReference => reader.GetString(reader.GetMemberReference((MemberReferenceHandle)handle).Name),
        _ => "?",
    };

    public string TypeName(TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return TypeName(declaring) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private string TypeName(TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private enum LocalKind { Load, Store, Address }

    private static bool TryLocal(OpCode op, int operand, out int index, out LocalKind kind)
    {
        (index, kind) = op.Value switch
        {
            0x06 or 0x07 or 0x08 or 0x09 => (op.Value - 0x06, LocalKind.Load),   // ldloc.0-3
            0x0A or 0x0B or 0x0C or 0x0D => (op.Value - 0x0A, LocalKind.Store),  // stloc.0-3
            0x11 => (operand, LocalKind.Load),     // ldloc.s
            0x12 => (operand, LocalKind.Address),  // ldloca.s
            0x13 => (operand, LocalKind.Store),    // stloc.s
            unchecked((short)0xFE0C) => (operand, LocalKind.Load),     // ldloc
            unchecked((short)0xFE0D) => (operand, LocalKind.Address),  // ldloca
            unchecked((short)0xFE0E) => (operand, LocalKind.Store),    // stloc
            _ => (-1, LocalKind.Load),
        };
        return index >= 0;
    }

    private static int PopCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Varpop => 0,
        _ => behaviour.ToString().Split('_').Length,
    };

    private static int PushCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Push0 or StackBehaviour.Varpush => 0,
        StackBehaviour.Push1_push1 => 2,
        _ => 1,
    };
}

/// <summary>Type names as strings, enough to recognise System.String and count parameters.</summary>
internal sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode switch
    {
        PrimitiveTypeCode.Void => "Void",
        PrimitiveTypeCode.String => "String",
        PrimitiveTypeCode.Object => "Object",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = r.GetTypeDefinition(handle);
        return r.GetString(type.Namespace) + "." + r.GetString(type.Name);
    }

    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = r.GetTypeReference(handle);
        return r.GetString(type.Namespace) + "." + r.GetString(type.Name);
    }

    public string GetTypeFromSpecification(MetadataReader r, object? context, TypeSpecificationHandle handle, byte rawTypeKind)
        => r.GetTypeSpecification(handle).DecodeSignature(this, context);

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType;
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
    public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? context, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
}
