using Mono.Cecil;
using Mono.Cecil.Cil;

namespace InterfaceSideEffects;

/// <summary>Resolver that serves the assemblies already loaded for analysis before looking on disk.</summary>
sealed class Resolver : DefaultAssemblyResolver
{
    public void Register(AssemblyDefinition asm) => RegisterAssembly(asm);
}

/// <summary>
/// The analyzed assemblies (the game and any mods): a type index, override/implementation maps for virtual dispatch,
/// and IL stack helpers. Everything outside these assemblies (the BCL, the engine) is treated as opaque.
/// </summary>
sealed class CodeModel
{
    public readonly Resolver Resolver = new();
    public readonly List<ModuleDefinition> Modules = [];
    readonly Dictionary<string, TypeDefinition> types = new();
    readonly Dictionary<MethodDefinition, List<MethodDefinition>> directOverriders = new();
    readonly Dictionary<MethodDefinition, List<MethodDefinition>> allOverriders = new();

    public CodeModel(IEnumerable<string> searchDirs)
    {
        foreach (var dir in searchDirs.Distinct())
            Resolver.AddSearchDirectory(dir);
    }

    public ModuleDefinition Load(string path, bool analyzed)
    {
        var module = ModuleDefinition.ReadModule(path, new ReaderParameters
        {
            AssemblyResolver = Resolver,
            ReadingMode = ReadingMode.Deferred,
        });
        Resolver.Register(module.Assembly);
        if (analyzed)
        {
            Modules.Add(module);
            foreach (var t in module.GetTypes())
                types.TryAdd(t.FullName, t);
        }
        return module;
    }

    public IEnumerable<TypeDefinition> AllTypes => Modules.SelectMany(m => m.GetTypes());

    public TypeDefinition FindType(string fullName) => types.GetValueOrDefault(fullName);

    /// <summary>The definition of an analyzed type, or null for external types and generic parameters.</summary>
    public TypeDefinition Type(TypeReference tr)
    {
        if (tr == null || tr is GenericParameter) return null;
        while (tr is TypeSpecification spec) tr = spec.ElementType;
        if (tr is GenericParameter) return null;
        return types.GetValueOrDefault(tr.FullName);
    }

    public MethodDefinition Method(MethodReference mr)
    {
        if (mr is MethodDefinition md) return md;
        if (Type(mr.DeclaringType) == null) return null;
        try { return mr.Resolve(); }
        catch { return null; }
    }

    public FieldDefinition Field(FieldReference fr)
    {
        if (fr is FieldDefinition fd) return fd;
        if (Type(fr.DeclaringType) == null) return null;
        try { return fr.Resolve(); }
        catch { return null; }
    }

    public TypeDefinition Base(TypeDefinition t) => Type(t.BaseType);

    public IEnumerable<TypeDefinition> SelfAndBases(TypeDefinition t)
    {
        for (; t != null; t = Base(t)) yield return t;
    }

    public bool DerivesFrom(TypeDefinition t, HashSet<string> baseNames) =>
        SelfAndBases(t).Any(b => baseNames.Contains(b.FullName));

    public bool Implements(TypeDefinition t, string interfaceName)
    {
        foreach (var b in SelfAndBases(t))
            foreach (var i in b.Interfaces)
            {
                if (i.InterfaceType.FullName == interfaceName) return true;
                var it = Type(i.InterfaceType);
                if (it != null && Implements(it, interfaceName)) return true;
            }
        return false;
    }

    // ---------------------------------------------------------------- virtual dispatch

    public void BuildOverrides()
    {
        foreach (var t in AllTypes)
        {
            foreach (var m in t.Methods)
            {
                if (!m.IsVirtual) continue;

                foreach (var ov in m.Overrides)
                    if (Method(ov) is { } explicitBase)
                        Add(explicitBase, m);

                if (!m.IsNewSlot)
                {
                    for (var b = Base(t); b != null; b = Base(b))
                    {
                        var baseMethod = b.Methods.FirstOrDefault(bm => bm.IsVirtual && SameSignature(bm, m));
                        if (baseMethod != null) { Add(baseMethod, m); break; }
                    }
                }
            }

            // Implicit interface implementations, including ones inherited from a base class
            foreach (var iface in AllInterfaces(t))
            {
                foreach (var im in iface.Methods)
                {
                    if (t.Methods.Any(m => m.Overrides.Any(o => o.Name == im.Name && Method(o) == im))) continue;
                    var impl = SelfAndBases(t).SelectMany(b => b.Methods)
                        .FirstOrDefault(m => m.IsVirtual && !m.IsStatic && SameSignature(im, m));
                    if (impl != null) Add(im, impl);
                }
            }
        }

        void Add(MethodDefinition baseMethod, MethodDefinition overrider)
        {
            if (baseMethod == overrider) return;
            if (!directOverriders.TryGetValue(baseMethod, out var list))
                directOverriders[baseMethod] = list = [];
            if (!list.Contains(overrider)) list.Add(overrider);
        }
    }

    IEnumerable<TypeDefinition> AllInterfaces(TypeDefinition t)
    {
        var seen = new HashSet<TypeDefinition>();
        var stack = new Stack<TypeDefinition>();
        foreach (var b in SelfAndBases(t))
            foreach (var i in b.Interfaces)
                if (Type(i.InterfaceType) is { } it) stack.Push(it);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (!seen.Add(i)) continue;
            yield return i;
            foreach (var sub in i.Interfaces)
                if (Type(sub.InterfaceType) is { } st) stack.Push(st);
        }
    }

    static bool SameSignature(MethodDefinition a, MethodDefinition b)
    {
        if (a.Name != b.Name || a.Parameters.Count != b.Parameters.Count || a.HasGenericParameters != b.HasGenericParameters)
            return false;
        // Loose on purpose: generic base classes substitute their type parameters, so only compare plain names
        for (int i = 0; i < a.Parameters.Count; i++)
        {
            var pa = a.Parameters[i].ParameterType;
            var pb = b.Parameters[i].ParameterType;
            if (pa.ContainsGenericParameter || pb.ContainsGenericParameter) continue;
            if (pa.Name != pb.Name) return false;
        }
        return true;
    }

    /// <summary>Every override or implementation of the method, transitively.</summary>
    public List<MethodDefinition> Overriders(MethodDefinition m)
    {
        if (allOverriders.TryGetValue(m, out var cached)) return cached;
        var result = new List<MethodDefinition>();
        var seen = new HashSet<MethodDefinition> { m };
        var stack = new Stack<MethodDefinition>();
        stack.Push(m);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!directOverriders.TryGetValue(cur, out var list)) continue;
            foreach (var o in list)
                if (seen.Add(o)) { result.Add(o); stack.Push(o); }
        }
        return allOverriders[m] = result;
    }

    // ---------------------------------------------------------------- IL stack helpers

    public static int Pops(Instruction ins, MethodDefinition method)
    {
        var op = ins.OpCode;
        switch (op.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1:
            case StackBehaviour.Popi:
            case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1:
            case StackBehaviour.Popi_pop1:
            case StackBehaviour.Popi_popi:
            case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4:
            case StackBehaviour.Popi_popr8:
            case StackBehaviour.Popref_pop1:
            case StackBehaviour.Popref_popi: return 2;
            case StackBehaviour.Popi_popi_popi:
            case StackBehaviour.Popref_popi_popi:
            case StackBehaviour.Popref_popi_popi8:
            case StackBehaviour.Popref_popi_popr4:
            case StackBehaviour.Popref_popi_popr8:
            case StackBehaviour.Popref_popi_popref: return 3;
            case StackBehaviour.PopAll: return -1;
            case StackBehaviour.Varpop:
                if (op.Code == Code.Ret) return method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
                if (ins.Operand is MethodReference mr)
                    return mr.Parameters.Count + (mr.HasThis && op.Code != Code.Newobj ? 1 : 0);
                if (ins.Operand is CallSite cs)
                    return cs.Parameters.Count + (cs.HasThis ? 1 : 0) + 1;
                return 0;
        }
        return 0;
    }

    public static int Pushes(Instruction ins)
    {
        switch (ins.OpCode.StackBehaviourPush)
        {
            case StackBehaviour.Push0: return 0;
            case StackBehaviour.Push1_push1: return 2;
            case StackBehaviour.Varpush:
                if (ins.Operand is MethodReference mr)
                    return ins.OpCode.Code == Code.Newobj || mr.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
                if (ins.Operand is CallSite cs)
                    return cs.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
                return 0;
            default: return 1;
        }
    }

    /// <summary>
    /// The instruction that pushed the given argument of a call-like instruction (0 = first pushed, i.e. 'this'
    /// for instance calls), looking through dup. Null when control flow makes it ambiguous.
    /// </summary>
    public static Instruction Producer(Instruction consumer, int argIndex, MethodDefinition method, HashSet<Instruction> branchTargets)
    {
        int argCount = Pops(consumer, method);
        if (argCount <= argIndex) return null;
        int pos = argCount - 1 - argIndex; // items above the wanted one, just before the consumer
        var after = consumer;
        for (var p = consumer.Previous; p != null; after = p, p = p.Previous)
        {
            if (branchTargets.Contains(after)) return null;
            var flow = p.OpCode.FlowControl;
            if (flow is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw) return null;
            int push = Pushes(p), pop = Pops(p, method);
            if (pop < 0) return null;
            if (pos < push)
            {
                if (p.OpCode.Code == Code.Dup) { pos = 0; continue; } // either copy is the dup's input
                return p;
            }
            pos = pos - push + pop;
        }
        return null;
    }

    public static HashSet<Instruction> BranchTargets(MethodBody body)
    {
        var set = new HashSet<Instruction>();
        foreach (var ins in body.Instructions)
        {
            if (ins.Operand is Instruction t) set.Add(t);
            else if (ins.Operand is Instruction[] ts) foreach (var x in ts) set.Add(x);
        }
        foreach (var h in body.ExceptionHandlers)
        {
            if (h.HandlerStart != null) set.Add(h.HandlerStart);
            if (h.FilterStart != null) set.Add(h.FilterStart);
        }
        return set;
    }

    // ---------------------------------------------------------------- names

    public static bool IsCompilerGenerated(TypeDefinition t) => t.Name.StartsWith('<') || (t.DeclaringType != null && IsCompilerGenerated(t.DeclaringType));

    /// <summary>"Type.Method", naming lambdas, local functions and iterators after the method they're written in.</summary>
    public static string Display(MethodDefinition m)
    {
        var t = m.DeclaringType;
        string suffix = "";
        string name = m.Name;

        if (name.StartsWith('<'))
        {
            int close = name.IndexOf('>');
            var outer = close > 1 ? name[1..close] : name;
            suffix = name.Contains(">g__") ? $" (local {LocalName(name)})" : " (lambda)";
            name = outer;
        }

        while (t.Name.StartsWith('<'))
        {
            int close = t.Name.IndexOf('>');
            if (close > 1)
            {
                // Iterator or async state machine: <GetGizmos>d__12
                if (m.Name is "MoveNext" or ".ctor" or "System.Collections.IEnumerator.MoveNext")
                {
                    name = t.Name[1..close];
                    suffix = " (iterator)";
                }
            }
            else if (suffix == "") suffix = " (lambda)";
            if (t.DeclaringType == null) break;
            t = t.DeclaringType;
        }

        return $"{ShortType(t)}.{name}{suffix}";

        static string LocalName(string n)
        {
            int start = n.IndexOf(">g__", StringComparison.Ordinal) + 4;
            int end = n.IndexOf('|', start);
            return end > start ? n[start..end] : n[start..];
        }
    }

    public static string ShortType(TypeReference t)
    {
        var name = t.Name;
        int tick = name.IndexOf('`');
        if (tick > 0) name = name[..tick];
        return t.DeclaringType != null && !t.DeclaringType.Name.StartsWith('<') ? $"{ShortType(t.DeclaringType)}.{name}" : name;
    }

    /// <summary>The method a lambda, local function or iterator body belongs to, or the method itself.</summary>
    public static (TypeDefinition type, string name) Owner(MethodDefinition m)
    {
        var t = m.DeclaringType;
        string name = m.Name;
        if (name.StartsWith('<') && name.IndexOf('>') is var c and > 1) name = name[1..c];
        while (t.Name.StartsWith('<') && t.DeclaringType != null)
        {
            if (m.Name is "MoveNext" or ".ctor" && t.Name.IndexOf('>') is var cc and > 1) name = t.Name[1..cc];
            t = t.DeclaringType;
        }
        return (t, name);
    }
}
