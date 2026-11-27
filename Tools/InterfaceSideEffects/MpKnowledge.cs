using Mono.Cecil;
using Mono.Cecil.Cil;

namespace InterfaceSideEffects;

/// <summary>
/// What Multiplayer.dll already does about game methods, read from its IL: Harmony patch targets (findings through
/// them are probably handled) and registered sync methods and fields (calling them from the interface sends a
/// command instead of running them, so the analysis stops there).
/// </summary>
sealed class MpKnowledge
{
    /// <summary>"Type::method" → patch classes.</summary>
    public readonly Dictionary<string, List<string>> Patches = new();
    public readonly HashSet<string> SyncMethods = [];
    /// <summary>Method names registered for sync without a type in reach (registered in loops over types).</summary>
    public readonly HashSet<string> SyncMethodNamesAnyType = [];
    public readonly HashSet<string> SyncFieldNames = [];
    public int PatchesWithComputedTargets, UnreadablePatches;
    public string Path;

    public static MpKnowledge Load(CodeModel model, string path)
    {
        var k = new MpKnowledge { Path = path };
        if (path == null || !File.Exists(path)) return k;

        var module = model.Load(path, analyzed: false);
        foreach (var t in module.GetTypes())
        {
            try { k.ReadPatchAttributes(t); }
            catch (AssemblyResolutionException) { k.UnreadablePatches++; }
            foreach (var m in t.Methods)
                if (m.HasBody) k.ReadSyncRegistrations(m);
        }
        return k;
    }

    void ReadPatchAttributes(TypeDefinition t)
    {
        var attrs = t.CustomAttributes.Where(a => a.AttributeType.Name == "HarmonyPatch").ToList();
        if (attrs.Count == 0) return;

        TypeReference target = null;
        string method = null;
        int methodType = 0;
        foreach (var a in attrs)
            foreach (var arg in a.ConstructorArguments)
            {
                if (arg.Value is TypeReference tr && arg.Type.FullName == "System.Type") target ??= tr;
                else if (arg.Value is string s) method ??= s;
                else if (arg.Type.Name == "MethodType" && arg.Value is int mt) methodType = mt;
            }

        if (target == null || (method == null && methodType != 3))
        {
            if (t.Methods.Any(m => m.Name is "TargetMethod" or "TargetMethods")) PatchesWithComputedTargets++;
            return;
        }

        var name = methodType switch
        {
            1 => "get_" + method,
            2 => "set_" + method,
            3 => ".ctor",
            _ => method,
        };
        var key = $"{target.FullName}::{name}";
        if (!Patches.TryGetValue(key, out var list)) Patches[key] = list = [];
        list.Add(CodeModel.ShortType(t));
    }

    void ReadSyncRegistrations(MethodDefinition m)
    {
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.OpCode.Code is not (Code.Call or Code.Callvirt) || ins.Operand is not MethodReference callee) continue;
            if (!callee.DeclaringType.Name.Contains("Sync")) continue;

            bool method = callee.Name is "Register" or "RegisterSyncMethod" or "RegisterSyncDialogNodeTree" or "MethodSync";
            bool field = callee.Name is "Field" or "RegisterSyncField";
            if (!method && !field) continue;

            TypeReference type = null;
            var strings = new List<string>();
            int steps = 0;
            for (var p = ins.Previous; p != null && steps < 14; p = p.Previous, steps++)
            {
                if (p.OpCode.Code == Code.Ldtoken && p.Operand is TypeReference tr) { type ??= tr; break; }
                if (p.OpCode.Code == Code.Ldstr) strings.Add((string)p.Operand);
                if (p.OpCode.Code is Code.Call or Code.Callvirt && p.Operand is MethodReference pm && pm.Name == callee.Name) break;
            }

            if (method)
            {
                var name = strings.LastOrDefault();
                if (name == null) continue;
                if (type == null) SyncMethodNamesAnyType.Add(name);
                else
                {
                    SyncMethods.Add($"{type.FullName}::{name}");
                    SyncMethods.Add($"{type.FullName}::set_{name}");
                }
            }
            else
            {
                foreach (var s in strings)
                    foreach (var part in s.Split('/', '.'))
                        if (part.Length > 0 && !part.Contains('[')) SyncFieldNames.Add(part);
            }
        }
    }

    public IEnumerable<string> PatchesOn(MethodDefinition m)
    {
        if (Patches.TryGetValue($"{m.DeclaringType.FullName}::{m.Name}", out var list)) return list;
        var (owner, name) = CodeModel.Owner(m);
        if (owner != m.DeclaringType && Patches.TryGetValue($"{owner.FullName}::{name}", out list)) return list;
        return [];
    }

    public bool IsSynced(MethodDefinition m) =>
        SyncMethods.Contains($"{m.DeclaringType.FullName}::{m.Name}") ||
        (m.Name.StartsWith("set_") && SyncMethods.Contains($"{m.DeclaringType.FullName}::{m.Name[4..]}")) ||
        SyncMethodNamesAnyType.Contains(m.Name);
}
