using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// A static call graph of one assembly, read from method IL. Edges are calls,
/// object creations and delegate creations (<c>ldftn</c>), plus the step from
/// an async or iterator method into its state machine. A call to a virtual or
/// interface method also reaches every override and implementation in the
/// assembly, so the graph over-approximates: "reaches" means "may reach".
/// Calls into other assemblies are leaves; a delegate the assembly hands to
/// them is still an edge from the method that created it.
/// </summary>
internal sealed class IlCallGraph
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    private const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private readonly Module _module;
    private readonly Dictionary<int, MethodBase> _methods = new();
    private readonly Dictionary<int, HashSet<int>> _edges = new();

    public IlCallGraph(Assembly assembly)
    {
        _module = assembly.ManifestModule;
        var types = LoadableTypes(assembly);
        foreach (var type in types)
        foreach (var method in type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything)))
            _methods[method.MetadataToken] = method;

        foreach (var method in _methods.Values) _edges[method.MetadataToken] = Callees(method);
        AddDispatchEdges(types);
        AddStoredDelegateEdges();
        AddCreatedObjectEdges();
    }

    public bool Contains(MethodBase method) => method.Module == _module && _methods.ContainsKey(method.MetadataToken);

    /// <summary>
    /// For every method that may reach one of <paramref name="sinks"/>, the next
    /// method on a shortest path towards a sink.
    /// </summary>
    public IReadOnlyDictionary<int, int> PathsTo(IEnumerable<MethodBase> sinks)
    {
        var reverse = new Dictionary<int, List<int>>();
        foreach (var (caller, callees) in _edges)
        foreach (var callee in callees)
        {
            if (!reverse.TryGetValue(callee, out var callers)) reverse[callee] = callers = [];
            callers.Add(caller);
        }

        var next = new Dictionary<int, int>();
        var queue = new Queue<int>();
        foreach (var sink in sinks.Where(Contains))
        {
            next[sink.MetadataToken] = sink.MetadataToken;
            queue.Enqueue(sink.MetadataToken);
        }
        while (queue.TryDequeue(out var current))
        {
            if (!reverse.TryGetValue(current, out var callers)) continue;
            foreach (var caller in callers.Where(caller => !next.ContainsKey(caller)))
            {
                next[caller] = current;
                queue.Enqueue(caller);
            }
        }
        return next;
    }

    /// <summary>The method chain from <paramref name="root"/> to a sink, or empty.</summary>
    public IReadOnlyList<MethodBase> Path(MethodBase root, IReadOnlyDictionary<int, int> paths)
    {
        if (!Contains(root) || !paths.ContainsKey(root.MetadataToken)) return [];
        var chain = new List<MethodBase> { root };
        var current = root.MetadataToken;
        while (paths[current] != current && chain.Count < 64)
        {
            current = paths[current];
            chain.Add(_methods[current]);
        }
        return chain;
    }

    /// <summary>Readable member name: compiler-generated lambdas and state machines fold into their owner.</summary>
    public static string Describe(MethodBase method)
    {
        var type = method.DeclaringType!;
        while (type.DeclaringType is not null && type.IsDefined(typeof(CompilerGeneratedAttribute)))
            type = type.DeclaringType;
        var name = method.Name;
        if (method.DeclaringType!.IsDefined(typeof(CompilerGeneratedAttribute)) && name == "MoveNext")
            name = method.DeclaringType.Name;
        var owner = method.DeclaringType.Name.StartsWith('<') && name != method.DeclaringType.Name
            ? method.DeclaringType.Name : null;
        return Clean(type.Name) + "." + Clean(owner is null ? name : owner + "." + name);
    }

    private static string Clean(string name)
    {
        var tick = name.IndexOf('`');
        if (tick > 0) name = name[..tick];
        return name.Replace("<>c__DisplayClass", "closure", StringComparison.Ordinal);
    }

    // Per method: types it creates (newobj), and whether it invokes a delegate.
    private readonly Dictionary<int, List<Type>> _objectsCreated = new();
    private readonly HashSet<int> _invokesDelegate = [];

    // Delegates passed straight into a type's constructor or stored in one of
    // its fields, keyed by that type.
    private readonly Dictionary<Type, HashSet<int>> _handedTo = new();

    /// <summary>
    /// A delegate stored in a field and invoked later (a cache's build
    /// function) has no call edge. A delegate passed straight into a type's
    /// constructor, or stored in one of its fields, is handed to that type; a
    /// method of that type that invokes a delegate may reach it.
    /// </summary>
    private void AddStoredDelegateEdges()
    {
        foreach (var token in _invokesDelegate)
        {
            if (_handedTo.TryGetValue(Owner(_methods[token].DeclaringType!), out var targets))
                _edges[token].UnionWith(targets);
        }
    }

    private void Hand(Type type, List<int> pending)
    {
        if (pending.Count == 0) return;
        type = Owner(type.IsGenericType ? type.GetGenericTypeDefinition() : type);
        if (!_handedTo.TryGetValue(type, out var set)) _handedTo[type] = set = [];
        set.UnionWith(pending);
        pending.Clear();
    }

    private static Type Owner(Type type)
    {
        while (type.DeclaringType is not null && type.IsDefined(typeof(CompilerGeneratedAttribute)))
            type = type.DeclaringType;
        return type;
    }

    /// <summary>
    /// The framework calls interface members of objects a handler returns (a
    /// custom <c>IResult</c>'s <c>ExecuteAsync</c>). Creating an object
    /// therefore reaches its interface implementations.
    /// </summary>
    private void AddCreatedObjectEdges()
    {
        foreach (var (token, types) in _objectsCreated)
        foreach (var type in types.Distinct())
        foreach (var iface in SafeInterfaces(type))
        {
            InterfaceMapping map;
            try { map = type.GetInterfaceMap(iface); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                continue;
            }
            foreach (var target in map.TargetMethods.Where(Contains)) _edges[token].Add(target.MetadataToken);
        }
    }

    private HashSet<int> Callees(MethodBase method)
    {
        var callees = new HashSet<int>();
        var stateMachine = method.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType;
        if (stateMachine?.GetMethod("MoveNext", Everything) is { } moveNext && Contains(moveNext))
            callees.Add(moveNext.MetadataToken);

        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (Exception ex) when (ex is InvalidOperationException or BadImageFormatException) { il = null; }
        if (il is null) return callees;

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericArguments() : null;
        var methodArguments = method is MethodInfo { IsGenericMethod: true } generic
            ? generic.GetGenericArguments() : null;
        var pending = new List<int>();
        for (var offset = 0; offset < il.Length;)
        {
            OpCode code;
            if (il[offset] == 0xFE && offset + 1 < il.Length)
            {
                code = OpCodesByValue[unchecked((short)(0xFE00 | il[offset + 1]))];
                offset += 2;
            }
            else
            {
                code = OpCodesByValue[il[offset]];
                offset += 1;
            }

            if (code.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, offset);
                try
                {
                    var target = _module.ResolveMethod(token, typeArguments, methodArguments);
                    if (target is not null && Contains(target)) callees.Add(target.MetadataToken);
                    Note(method, code, target, pending);
                }
                catch (Exception ex) when (ex is ArgumentException or BadImageFormatException
                                               or TypeLoadException or FileNotFoundException
                                               or MissingMethodException)
                {
                    // A token into an assembly that is not loadable here is a leaf.
                }
            }
            else if (code == OpCodes.Stfld || code == OpCodes.Stsfld)
            {
                NoteStore(method, BitConverter.ToInt32(il, offset), typeArguments, pending);
            }
            offset += OperandSize(code, il, offset);
        }
        return callees;
    }

    private void Note(MethodBase method, OpCode code, MethodBase? target, List<int> pending)
    {
        if (target is null) return;
        var declaring = target.DeclaringType;
        var isDelegate = declaring is not null && typeof(Delegate).IsAssignableFrom(declaring);
        if (code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn)
        {
            if (Contains(target)) pending.Add(target.MetadataToken);
            return;
        }
        if (isDelegate)
        {
            if (target.Name == "Invoke") _invokesDelegate.Add(method.MetadataToken);
            return;
        }
        if (code == OpCodes.Newobj && declaring is not null && declaring.Module == _module)
        {
            var type = declaring.IsGenericType ? declaring.GetGenericTypeDefinition() : declaring;
            if (!_objectsCreated.TryGetValue(method.MetadataToken, out var list))
                _objectsCreated[method.MetadataToken] = list = [];
            list.Add(type);
            Hand(type, pending);
        }
        else if (target.IsConstructor && declaring is not null && declaring.Module == _module)
        {
            Hand(declaring, pending);
        }
        else
        {
            pending.Clear();
        }
    }

    private void NoteStore(MethodBase method, int token, Type[]? typeArguments, List<int> pending)
    {
        if (pending.Count == 0) return;
        FieldInfo? field;
        try { field = _module.ResolveField(token, typeArguments, null); }
        catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or TypeLoadException)
        {
            return;
        }
        // The compiler caches static lambdas in fields of its own closure types.
        if (field?.DeclaringType is null || field.DeclaringType.IsDefined(typeof(CompilerGeneratedAttribute))) return;
        if (field.DeclaringType.Module == _module) Hand(field.DeclaringType, pending);
        else pending.Clear();
    }

    private void AddDispatchEdges(IReadOnlyList<Type> types)
    {
        foreach (var type in types.Where(type => type is { IsInterface: false }))
        {
            foreach (var iface in SafeInterfaces(type).Where(iface => iface.Module == _module))
            {
                InterfaceMapping map;
                try { map = type.GetInterfaceMap(iface); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    continue;
                }
                for (var index = 0; index < map.InterfaceMethods.Length; index++)
                    Link(map.InterfaceMethods[index], map.TargetMethods[index]);
            }

            foreach (var method in type.GetMethods(Everything).Where(method => method.IsVirtual))
            {
                MethodInfo baseDefinition;
                try { baseDefinition = method.GetBaseDefinition(); }
                catch (Exception ex) when (ex is TypeLoadException or NotSupportedException) { continue; }
                if (baseDefinition != method) Link(baseDefinition, method);
            }
        }
    }

    private void Link(MethodBase declared, MethodBase implementation)
    {
        if (!Contains(declared) || !Contains(implementation)) return;
        _edges[declared.MetadataToken].Add(implementation.MetadataToken);
    }

    private static IEnumerable<Type> SafeInterfaces(Type type)
    {
        try { return type.GetInterfaces(); }
        catch (TypeLoadException) { return []; }
    }

    private static IReadOnlyList<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>().ToArray(); }
    }

    private static int OperandSize(OpCode code, byte[] il, int offset) => code.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
        _ => 4,
    };
}
