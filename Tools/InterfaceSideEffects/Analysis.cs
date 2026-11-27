using Mono.Cecil;
using Mono.Cecil.Cil;

namespace InterfaceSideEffects;

enum SinkKind
{
    IdAllocation,   // takes a unique ID (UniqueIDsManager)
    SavedWrite,     // writes a field the game saves
    StateWrite,     // writes an unsaved field of simulation state
    CacheWrite,     // writes an unsaved field named like a cache or dirty flag
    RefWrite,       // passes a state field by ref (usually a widget editing it)
    StaticWrite,    // writes a static field of a non-interface type
    Rand,           // consumes Verse.Rand outside a PushState/PopState pair
}

[Flags]
enum PathFlags : byte
{
    None = 0,
    RandProtected = 1, // inside Rand.PushState/PopState
    OnClick = 2,       // behind a button check
}

readonly record struct Edge(MethodDefinition Target, int Offset, PathFlags Flags, string Via);

sealed record Sink(SinkKind Kind, string Member, int Offset, PathFlags Flags, string Detail);

sealed class MethodFacts
{
    public readonly List<Edge> Edges = [];
    public readonly List<Sink> Sinks = [];
    public int CappedCalls;
}

sealed record Finding(SinkKind Kind, MethodDefinition Method, Sink Sink, List<MethodDefinition> Path, string Category, PathFlags Flags)
{
    public List<string> Patches { get; init; } = [];
    public bool AlsoConsumesRand { get; init; }
}

sealed class Analysis
{
    static readonly HashSet<string> MutatingCollectionMethods =
    [
        "Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "RemoveWhere",
        "Clear", "set_Item", "TryAdd", "Sort", "Reverse", "Enqueue", "Dequeue", "Push", "Pop", "UnionWith",
        "ExceptWith", "IntersectWith", "SymmetricExceptWith", "AddFirst", "AddLast", "RemoveFirst", "RemoveLast",
        "SortBy", "SortByDescending", "SortStable", "Shuffle", "RemoveDuplicates", "TryDequeue", "TryPop",
    ];

    static readonly HashSet<string> CollectionNamespaces = ["System.Collections.Generic", "System.Collections"];

    readonly CodeModel model;
    readonly StateTypes stateTypes;
    readonly MpKnowledge mp;
    readonly IgnoreList ignore;
    readonly Options options;
    readonly Dictionary<MethodDefinition, MethodFacts> facts = new();
    readonly Dictionary<MethodDefinition, bool> consumesRand = new();
    readonly HashSet<FieldDefinition> savedFields = [];

    public int Visited => visited.Count;
    public int StoppedAtSync, StoppedByIgnore, CappedCalls;
    public readonly Dictionary<string, int> EntriesByCategory = new();
    readonly Dictionary<(MethodDefinition, PathFlags), (MethodDefinition parent, PathFlags parentFlags, string category)> visited = new();

    public Analysis(CodeModel model, StateTypes stateTypes, MpKnowledge mp, IgnoreList ignore, Options options)
    {
        this.model = model;
        this.stateTypes = stateTypes;
        this.mp = mp;
        this.ignore = ignore;
        this.options = options;

        // Fields referenced by ref in ExposeData methods are saved
        foreach (var t in model.AllTypes)
            foreach (var m in t.Methods)
                if (m.HasBody && m.Name.Contains("ExposeData"))
                    foreach (var ins in m.Body.Instructions)
                        if (ins.OpCode.Code is Code.Ldflda or Code.Ldsflda && model.Field((FieldReference)ins.Operand) is { } f)
                            savedFields.Add(f);
    }

    public int SavedFieldCount => savedFields.Count;

    // ---------------------------------------------------------------- search

    public List<Finding> Run()
    {
        var queue = new Queue<(MethodDefinition m, PathFlags flags, int depth)>();
        var baseSets = new Dictionary<string, HashSet<string>>();

        foreach (var t in model.AllTypes)
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                var rule = EntryRule.All.FirstOrDefault(r => r.Matches(model, m, baseSets));
                if (rule == null || Stops(m)) continue;
                if (visited.TryAdd((m, PathFlags.None), (null, PathFlags.None, rule.Category)))
                {
                    EntriesByCategory[rule.Category] = EntriesByCategory.GetValueOrDefault(rule.Category) + 1;
                    queue.Enqueue((m, PathFlags.None, 0));
                }
            }

        var found = new Dictionary<(SinkKind, MethodDefinition, string), Finding>();

        while (queue.Count > 0)
        {
            var (m, flags, depth) = queue.Dequeue();
            var f = Facts(m);

            foreach (var sink in f.Sinks)
            {
                var sf = flags | sink.Flags;
                if (sf.HasFlag(PathFlags.OnClick) && !options.IncludeClicks) continue;
                if (sink.Kind == SinkKind.Rand && sf.HasFlag(PathFlags.RandProtected)) continue;
                var key = (sink.Kind, m, sink.Member);
                if (found.ContainsKey(key)) continue;
                var path = PathTo(m, flags);
                found[key] = new Finding(sink.Kind, m, sink, path, CategoryOf(m, flags), sf)
                {
                    Patches = path.SelectMany(mp.PatchesOn).Distinct().ToList(),
                };
            }

            if (depth >= options.MaxDepth) continue;

            foreach (var e in f.Edges)
            {
                var ef = flags | e.Flags;
                if (ef.HasFlag(PathFlags.OnClick) && !options.IncludeClicks) continue;
                if (Dominated(e.Target, ef)) continue;
                if (Stops(e.Target)) continue;
                visited[(e.Target, ef)] = (m, flags, null);
                queue.Enqueue((e.Target, ef, depth + 1));
            }
        }

        var randMethods = found.Values.Where(x => x.Kind == SinkKind.Rand).Select(x => x.Method).ToHashSet();
        return found.Values.Select(x => x.Kind != SinkKind.Rand && randMethods.Contains(x.Method) ? x with { AlsoConsumesRand = true } : x).ToList();
    }

    /// <summary>How the interface reaches methods matching "Type.Method" (display name, substring), shortest first.</summary>
    public IEnumerable<(string category, List<MethodDefinition> path)> Why(string query) =>
        visited.Keys
            .Where(k => CodeModel.Display(k.Item1).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        $"{k.Item1.DeclaringType.FullName}::{k.Item1.Name}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(k => (CategoryOf(k.Item1, k.Item2), PathTo(k.Item1, k.Item2)))
            .OrderBy(x => x.Item2.Count)
            .Take(10);

    bool Dominated(MethodDefinition target, PathFlags flags)
    {
        // Fewer flags reach more sinks, so a state with a subset of the flags makes this one redundant
        for (int sub = 0; sub <= 3; sub++)
            if (((PathFlags)sub & ~flags) == 0 && visited.ContainsKey((target, (PathFlags)sub)))
                return true;
        return false;
    }

    bool Stops(MethodDefinition m)
    {
        if (mp.IsSynced(m)) { StoppedAtSync++; return true; }
        if (ignore.Methods.Contains($"{m.DeclaringType.FullName}::{m.Name}") || ignore.IgnoresType(m.DeclaringType))
        {
            StoppedByIgnore++;
            return true;
        }
        return false;
    }

    List<MethodDefinition> PathTo(MethodDefinition m, PathFlags flags)
    {
        var path = new List<MethodDefinition>();
        var cur = (m, flags);
        while (cur.m != null && path.Count < 64)
        {
            path.Add(cur.m);
            var p = visited[cur];
            cur = (p.parent, p.parentFlags);
        }
        path.Reverse();
        return path;
    }

    string CategoryOf(MethodDefinition m, PathFlags flags)
    {
        var cur = (m, flags);
        while (true)
        {
            var p = visited[cur];
            if (p.parent == null) return p.category;
            cur = (p.parent, p.parentFlags);
        }
    }

    // ---------------------------------------------------------------- per-method facts

    MethodFacts Facts(MethodDefinition m)
    {
        if (facts.TryGetValue(m, out var cached)) return cached;
        var f = facts[m] = new MethodFacts();
        if (!m.HasBody) return f;

        var body = m.Body;
        var targets = CodeModel.BranchTargets(body);
        var protectedRanges = RandProtectedRanges(body);
        var clickRanges = ClickRanges(body);
        var freshLocals = FreshLocals(body);

        PathFlags FlagsAt(int offset) =>
            (protectedRanges.Any(r => offset >= r.start && offset < r.end) ? PathFlags.RandProtected : 0) |
            (clickRanges.Any(r => offset >= r.start && offset < r.end) ? PathFlags.OnClick : 0);

        foreach (var ins in body.Instructions)
        {
            var at = FlagsAt(ins.Offset);
            switch (ins.OpCode.Code)
            {
                case Code.Call:
                case Code.Callvirt:
                case Code.Newobj:
                    Call(ins, (MethodReference)ins.Operand, at);
                    break;
                case Code.Ldftn:
                case Code.Ldvirtftn:
                    DelegateCreation(ins, at);
                    break;
                case Code.Stfld:
                    StoreField(ins, at, isStatic: false);
                    break;
                case Code.Stsfld:
                    StoreField(ins, at, isStatic: true);
                    break;
            }
        }
        CappedCalls += f.CappedCalls;
        return f;

        void Call(Instruction ins, MethodReference mr, PathFlags at)
        {
            var callee = model.Method(mr);

            // Collection mutation on a state field
            var declNs = mr.DeclaringType.GetElementType().Namespace;
            if (MutatingCollectionMethods.Contains(mr.Name) && ins.OpCode.Code != Code.Newobj &&
                (CollectionNamespaces.Contains(declNs) || (callee != null && callee.DeclaringType.Name is "GenCollection" or "GenList")))
            {
                bool extension = callee != null && callee.IsStatic;
                if (extension || mr.HasThis)
                {
                    var receiver = CodeModel.Producer(ins, 0, m, targets);
                    if (receiver?.Operand is FieldReference rf && receiver.OpCode.Code is Code.Ldfld or Code.Ldsfld &&
                        !(receiver.OpCode.Code == Code.Ldfld && CodeModel.Producer(receiver, 0, m, targets) is var owner &&
                          (owner != null && IsFreshReceiver(owner) || InitializesOwnObject(owner, rf))))
                        AddWrite(rf, receiver.OpCode.Code == Code.Ldsfld, at, ins.Offset, $"{CodeModel.ShortType(mr.DeclaringType)}.{mr.Name}");
                }
            }

            // State fields passed by ref
            for (int i = 0; i < mr.Parameters.Count; i++)
            {
                var p = mr.Parameters[i];
                if (p.ParameterType is not ByReferenceType || (p.IsIn && !p.IsOut)) continue;
                if (p.CustomAttributes.Any(a => a.AttributeType.Name == "IsReadOnlyAttribute")) continue;
                int argIndex = i + (mr.HasThis && ins.OpCode.Code != Code.Newobj ? 1 : 0);
                var producer = CodeModel.Producer(ins, argIndex, m, targets);
                if (producer?.Operand is FieldReference rf && producer.OpCode.Code is Code.Ldflda or Code.Ldsflda)
                    AddRefWrite(rf, at, ins.Offset, $"{CodeModel.ShortType(mr.DeclaringType)}.{mr.Name}");
            }

            if (callee == null) return;

            if (IsRandApi(callee))
            {
                if (ConsumesRand(callee))
                    f.Sinks.Add(new Sink(SinkKind.Rand, $"{CodeModel.ShortType(callee.DeclaringType)}.{callee.Name}", ins.Offset, at, null));
                return;
            }

            if (ins.OpCode.Code == Code.Newobj)
            {
                AddEdge(callee, ins.Offset, at, null);
                // Iterators and async state machines run their body in MoveNext
                if (CodeModel.IsCompilerGenerated(callee.DeclaringType) && callee.DeclaringType.Methods.FirstOrDefault(x => x.Name == "MoveNext") is { } moveNext)
                    AddEdge(moveNext, ins.Offset, at, "iterator");
                return;
            }

            // An object kept in a static field of an interface or utility class (GenDraw's field grid, say) is
            // that class's scratch space, not simulation state
            if (callee.HasThis && CodeModel.Producer(ins, 0, m, targets) is { OpCode.Code: Code.Ldsfld } src &&
                model.Field((FieldReference)src.Operand) is { } sf && !stateTypes.IsState(sf.DeclaringType) &&
                !CodeModel.IsCompilerGenerated(sf.DeclaringType))
                return;

            AddVirtual(callee, ins.OpCode.Code == Code.Callvirt, ins.Offset, at);
        }

        void DelegateCreation(Instruction ins, PathFlags at)
        {
            if (model.Method((MethodReference)ins.Operand) is not { } target) return;
            if (!options.FollowAllDelegates && IsDeferred(ins)) return;
            AddVirtual(target, ins.OpCode.Code == Code.Ldvirtftn, ins.Offset, at, "delegate");
        }

        // A delegate stored for later (a gizmo's or float menu option's action) runs on click, which the mod syncs
        bool IsDeferred(Instruction ldftn)
        {
            var ctor = ldftn.Next;
            return ctor?.OpCode.Code == Code.Newobj && DeferredFrom(ctor.Next, 3);
        }

        // Follows the delegate on top of the stack from 'start' to whatever consumes it
        bool DeferredFrom(Instruction start, int budget)
        {
            int pos = 0;
            int copies = 0;
            for (var p = start; p != null; p = p.Next)
            {
                var code = p.OpCode.Code;
                if (p.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)
                    return false;
                if (code == Code.Dup && pos == 0) { copies++; continue; }
                if (code == Code.Castclass && pos == 0) continue;
                int pop = CodeModel.Pops(p, m), push = CodeModel.Pushes(p);
                if (pop < 0) return false;
                if (pop > pos)
                {
                    if (code == Code.Stsfld && ((FieldReference)p.Operand).Name.StartsWith('<') && copies > 0) { copies--; continue; }
                    if (code is Code.Stfld or Code.Stsfld or Code.Stelem_Ref) return true;
                    // Action action = () => ...; later handed to a menu option: judge by the local's uses
                    if (StoreLocalIndex(p) is int local)
                    {
                        if (budget <= 0) return false;
                        return body.Instructions.Any(l => l.Offset > p.Offset && LocalIndex(l) == local && DeferredFrom(l.Next, budget - 1));
                    }
                    if (p.Operand is MethodReference consumer)
                    {
                        if (consumer.Name.StartsWith("set_")) return true;
                        // toggleAction += () => ...
                        if (consumer.DeclaringType.FullName == "System.Delegate" && consumer.Name == "Combine") { pos = 0; continue; }
                        int args = pop;
                        int argIndex = args - 1 - pos;
                        int paramIndex = argIndex - (consumer.HasThis && code != Code.Newobj ? 1 : 0);
                        if (paramIndex < 0 || paramIndex >= consumer.Parameters.Count) return false;
                        var ns = consumer.DeclaringType.GetElementType().Namespace;
                        if (ns.StartsWith("System.Linq") || CollectionNamespaces.Contains(ns)) return false;
                        var pt = consumer.Parameters[paramIndex].ParameterType.GetElementType();
                        return pt.Namespace == "System" && pt.Name.StartsWith("Action");
                    }
                    return false;
                }
                pos = pos - pop + push;
            }
            return false;
        }

        void StoreField(Instruction ins, PathFlags at, bool isStatic)
        {
            var fr = (FieldReference)ins.Operand;
            if (!isStatic)
            {
                var receiver = CodeModel.Producer(ins, 0, m, targets);
                if (receiver != null && IsFreshReceiver(receiver)) return;
                if (InitializesOwnObject(receiver, fr)) return;
            }
            else if (m.IsConstructor && m.IsStatic) return;
            AddWrite(fr, isStatic, at, ins.Offset, null);
        }

        // Constructors initialize their own object. When the receiver is unclear (a ternary in the value), assume 'this'.
        bool InitializesOwnObject(Instruction receiver, FieldReference fr) =>
            m.IsConstructor && !m.IsStatic &&
            (receiver == null ? model.Type(fr.DeclaringType) is { } dt && model.SelfAndBases(m.DeclaringType).Contains(dt)
                : receiver.OpCode.Code == Code.Ldarg_0);

        bool IsFreshReceiver(Instruction receiver)
        {
            if (IsFreshValue(receiver)) return true;
            // A struct on the stack (a local, or a parameter passed by value) is a copy
            if (receiver.OpCode.Code is Code.Ldloca or Code.Ldloca_S) return true;
            if (receiver.OpCode.Code is Code.Ldarga or Code.Ldarga_S && receiver.Operand is ParameterDefinition pd &&
                pd.ParameterType is not ByReferenceType) return true;
            if (LocalIndex(receiver) is int idx && freshLocals.Contains(idx)) return true;
            // Locals hoisted into an iterator or closure class
            if (receiver.OpCode.Code == Code.Ldfld && model.Field((FieldReference)receiver.Operand) is { } hf &&
                CodeModel.IsCompilerGenerated(hf.DeclaringType) && FreshHoistedField(hf)) return true;
            return false;
        }

        void AddWrite(FieldReference fr, bool isStatic, PathFlags at, int offset, string via)
        {
            var kind = Classify(fr, isStatic, out var fd);
            if (kind == null) return;
            // A getter that fills in its own field: lazy initialization
            if (kind is SinkKind.SavedWrite or SinkKind.StateWrite && m.Name.StartsWith("get_") &&
                body.Instructions.Any(i => i.OpCode.Code == Code.Ldfld && i.Operand is FieldReference r && r.Name == fd.Name))
            {
                kind = SinkKind.CacheWrite;
                via = via == null ? "lazy init" : $"{via}, lazy init";
            }
            f.Sinks.Add(new Sink(kind.Value, FieldName(fd), offset, at, via));
        }

        void AddRefWrite(FieldReference fr, PathFlags at, int offset, string via)
        {
            var kind = Classify(fr, fr.Resolve()?.IsStatic == true, out var fd);
            if (kind == null) return;
            var detail = savedFields.Contains(fd) ? $"{via}, saved field" : via;
            f.Sinks.Add(new Sink(kind == SinkKind.IdAllocation ? kind.Value : SinkKind.RefWrite, FieldName(fd), offset, at, detail));
        }

        void AddVirtual(MethodDefinition callee, bool virtualCall, int offset, PathFlags at, string via = null)
        {
            if (virtualCall && callee.IsVirtual && !callee.IsFinal)
            {
                var overriders = model.Overriders(callee);
                if (overriders.Count > options.MaxFanOut)
                {
                    f.CappedCalls++;
                    AddEdge(callee, offset, at, via);
                    return;
                }
                AddEdge(callee, offset, at, via);
                foreach (var o in overriders) AddEdge(o, offset, at, via ?? "override");
            }
            else AddEdge(callee, offset, at, via);
        }

        void AddEdge(MethodDefinition target, int offset, PathFlags at, string via)
        {
            if (target.HasBody) f.Edges.Add(new Edge(target, offset, at, via));
        }
    }

    SinkKind? Classify(FieldReference fr, bool isStatic, out FieldDefinition fd)
    {
        fd = model.Field(fr);
        if (fd == null) return null;
        var t = fd.DeclaringType;
        if (CodeModel.IsCompilerGenerated(t) || fd.Name.StartsWith('<') && !fd.Name.Contains("k__BackingField")) return null;
        if (IsScratch(fd.Name)) return null;
        if (ignore.Fields.Contains($"{t.FullName}::{fd.Name}") || ignore.IgnoresType(t)) return null;
        if (mp.SyncFieldNames.Contains(fd.Name)) return null;
        if (t.FullName == "Verse.Rand") return null; // reported as Rand at the call site
        if (t.Name == "UniqueIDsManager") return SinkKind.IdAllocation;
        if (stateTypes.IsInterfaceOrVisual(t)) return null;
        // A field holding a gizmo, graphic or material (a cached Command, say) is interface state even on a simulation object
        if (model.Type(fd.FieldType) is { } ftype && stateTypes.IsInterfaceOrVisual(ftype)) return null;
        if (fd.FieldType.Namespace.StartsWith("UnityEngine")) return null;

        if (isStatic || fd.IsStatic) return options.IncludeStatics ? SinkKind.StaticWrite : null;
        if (!stateTypes.IsState(t)) return null;
        if (savedFields.Contains(fd)) return SinkKind.SavedWrite;
        var n = fd.Name.ToLowerInvariant();
        if (n.Contains("cache") || n.Contains("dirty") || n.Contains("recalc") || n.StartsWith("last")) return SinkKind.CacheWrite;
        return SinkKind.StateWrite;
    }

    static bool IsScratch(string name)
    {
        var n = name.TrimStart('_');
        return n.StartsWith("tmp", StringComparison.OrdinalIgnoreCase) || n.StartsWith("temp", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("workingList", StringComparison.OrdinalIgnoreCase) || n.StartsWith("scratch", StringComparison.OrdinalIgnoreCase);
    }

    static string FieldName(FieldDefinition fd) => $"{CodeModel.ShortType(fd.DeclaringType)}.{fd.Name}";

    // ---------------------------------------------------------------- Rand

    static bool IsRandType(TypeDefinition t) => t.FullName == "Verse.Rand";

    /// <summary>
    /// Rand itself and static helpers built on it (RandomElement, Shuffle, InRandomOrder) are reported where they're
    /// called instead of being followed, so each use gets its own finding.
    /// </summary>
    bool IsRandApi(MethodDefinition m) =>
        IsRandType(m.DeclaringType) ||
        (m.IsStatic && (m.Name.Contains("Random") || m.Name.Contains("Shuffle")) && ConsumesRand(m));

    bool ConsumesRand(MethodDefinition m)
    {
        if (m.Name is "PushState" or "PopState" && IsRandType(m.DeclaringType)) return false;
        if (consumesRand.TryGetValue(m, out var v)) return v;
        consumesRand[m] = false; // cycle guard
        v = false;
        if (m.HasBody)
        {
            var ranges = RandProtectedRanges(m.Body);
            foreach (var ins in m.Body.Instructions)
            {
                if (ranges.Any(r => ins.Offset >= r.start && ins.Offset < r.end)) continue;
                if (ins.OpCode.Code == Code.Stsfld && IsRandType(model.Field((FieldReference)ins.Operand)?.DeclaringType ?? m.DeclaringType)
                    && IsRandType(m.DeclaringType))
                { v = true; break; }
                if (ins.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && model.Method((MethodReference)ins.Operand) is { } c)
                {
                    if (IsRandType(c.DeclaringType) && ConsumesRand(c)) { v = true; break; }
                    if (ins.OpCode.Code == Code.Newobj && CodeModel.IsCompilerGenerated(c.DeclaringType) &&
                        c.DeclaringType.Methods.FirstOrDefault(x => x.Name == "MoveNext") is { } mn && ConsumesRand(mn))
                    { v = true; break; }
                    if (c.IsStatic && (c.Name.Contains("Random") || c.Name.Contains("Shuffle")) && ConsumesRand(c)) { v = true; break; }
                }
            }
        }
        return consumesRand[m] = v;
    }

    static List<(int start, int end)> RandProtectedRanges(MethodBody body)
    {
        var ranges = new List<(int, int)>();
        int depth = 0, start = 0;
        foreach (var ins in body.Instructions)
        {
            if (ins.OpCode.Code is not (Code.Call or Code.Callvirt) || ins.Operand is not MethodReference mr) continue;
            if (mr.DeclaringType.FullName != "Verse.Rand") continue;
            if (mr.Name == "PushState") { if (depth++ == 0) start = ins.Offset; }
            else if (mr.Name == "PopState" && depth > 0) { if (--depth == 0) ranges.Add((start, ins.Offset)); }
        }
        if (depth > 0) ranges.Add((start, int.MaxValue));
        return ranges;
    }

    // ---------------------------------------------------------------- click gating and fresh objects

    /// <summary>Code inside <c>if (Widgets.ButtonX(...)) { ... }</c> runs on click, not every frame.</summary>
    static List<(int start, int end)> ClickRanges(MethodBody body)
    {
        var ranges = new List<(int, int)>();
        foreach (var ins in body.Instructions)
        {
            if (ins.OpCode.Code is not (Code.Call or Code.Callvirt) || ins.Operand is not MethodReference mr) continue;
            if (mr.ReturnType.MetadataType != MetadataType.Boolean) continue;
            var n = mr.Name;
            bool click = n.StartsWith("Button") || n.StartsWith("RadioButton") || n is "ClickedInsideRect" or "get_KeyDownEvent"
                or "get_JustPressed" or "CheckboxLabeledSelectable" or "ListSeparatorClicked" or "SelectableText";
            if (!click) continue;
            var next = ins.Next;
            if (next?.OpCode.Code is Code.Brfalse or Code.Brfalse_S && next.Operand is Instruction target && target.Offset > next.Offset)
                ranges.Add((next.Next.Offset, target.Offset));
            // if (Button(...) || other) { body }: brtrue jumps to the body, the last condition's brfalse skips it
            else if (next?.OpCode.Code is Code.Brtrue or Code.Brtrue_S && next.Operand is Instruction bodyStart && bodyStart.Offset > next.Offset)
            {
                for (var p = next.Next; p != null && p != bodyStart; p = p.Next)
                    if (p.OpCode.Code is Code.Brfalse or Code.Brfalse_S && p.Operand is Instruction end && end.Offset > bodyStart.Offset)
                    {
                        ranges.Add((bodyStart.Offset, end.Offset));
                        break;
                    }
            }
        }
        return ranges;
    }

    static readonly string[] FactoryPrefixes = ["Make", "Generate", "Create"];

    static bool IsFreshValue(Instruction p) =>
        p != null && (p.OpCode.Code == Code.Newobj ||
            // Pooled objects (JobMaker takes jobs from SimplePool<Job>) are handed out unused
            (p.Operand is MethodReference pool && pool.Name == "Get" && pool.DeclaringType.Name.StartsWith("SimplePool")) ||
            (p.OpCode.Code is Code.Call && p.Operand is MethodReference mr && mr.ReturnType.MetadataType != MetadataType.Void &&
             FactoryPrefixes.Any(x => mr.Name.StartsWith(x)) && !mr.HasThis));

    readonly Dictionary<FieldDefinition, bool> freshHoisted = new();

    /// <summary>A field of an iterator or closure class that only ever holds new objects (a hoisted local).</summary>
    bool FreshHoistedField(FieldDefinition hf)
    {
        if (freshHoisted.TryGetValue(hf, out var v)) return v;
        int stores = 0;
        bool allFresh = true;
        var owner = hf.DeclaringType.DeclaringType ?? hf.DeclaringType;
        foreach (var t in new[] { owner }.Concat(owner.NestedTypes))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                HashSet<Instruction> targets = null;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode.Code != Code.Stfld || ins.Operand is not FieldReference fr || fr.Name != hf.Name ||
                        fr.DeclaringType.GetElementType().FullName != hf.DeclaringType.FullName) continue;
                    targets ??= CodeModel.BranchTargets(m.Body);
                    stores++;
                    if (!IsFreshValue(CodeModel.Producer(ins, 1, m, targets))) allFresh = false;
                }
            }
        return freshHoisted[hf] = stores > 0 && allFresh;
    }

    /// <summary>Locals only ever assigned new objects (or results of factories), so writes through them don't touch existing state.</summary>
    HashSet<int> FreshLocals(MethodBody body)
    {
        var fresh = new HashSet<int>();
        var notFresh = new HashSet<int>();
        foreach (var ins in body.Instructions)
        {
            if (StoreLocalIndex(ins) is not int idx) continue;
            (IsFreshValue(ins.Previous) ? fresh : notFresh).Add(idx);
        }
        fresh.ExceptWith(notFresh);
        return fresh;
    }

    static int? LocalIndex(Instruction ins) => ins.OpCode.Code switch
    {
        Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3,
        Code.Ldloc or Code.Ldloc_S => ((VariableDefinition)ins.Operand).Index,
        _ => null,
    };

    static int? StoreLocalIndex(Instruction ins) => ins.OpCode.Code switch
    {
        Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3,
        Code.Stloc or Code.Stloc_S => ((VariableDefinition)ins.Operand).Index,
        _ => null,
    };
}
