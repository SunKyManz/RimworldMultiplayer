using Mono.Cecil;

namespace InterfaceSideEffects;

/// <summary>Methods the game runs from the interface or while drawing, which therefore must not change the simulation.</summary>
sealed record EntryRule(string Category, string[] Methods, string[] Bases)
{
    public static readonly EntryRule[] All =
    [
        new("gizmo", ["GetGizmos", "CompGetGizmosExtra", "CompGetWornGizmosExtra", "GetWornGizmos", "GetGizmosExtra",
            "CompGetGizmos", "GizmoOnGUI", "GizmoOnGUIInt", "get_Visible", "GizmoUpdateOnMouseover", "get_Disabled",
            "get_DisabledReason", "get_RightClickFloatMenuOptions"], null),
        new("inspect", ["GetInspectString", "CompInspectStringExtra", "GetInspectStringLowPriority",
            "CompInspectStringExtraLowPriority", "GetDescriptionPart", "SpecialDisplayStats", "CompTipStringExtra",
            "get_TipStringExtra", "GetTooltip", "GetTipString", "get_LabelInBrackets", "get_DescriptionDetailed",
            "GetInspectTabs"], null),
        new("float-menu", ["GetFloatMenuOptions", "CompFloatMenuOptions", "CompMultiSelectFloatMenuOptions",
            "GetMultiSelectFloatMenuOptions", "GetOptionsFor", "GetSingleOptionFor", "GetSingleOption", "GetOptions",
            "AppliesInt", "TargetPawnValid", "TargetThingValid"],
            ["RimWorld.FloatMenuOptionProvider", "RimWorld.FloatMenuMakerMap"]),
        new("float-menu", ["GetFloatMenuOptions", "CompFloatMenuOptions", "CompMultiSelectFloatMenuOptions",
            "GetMultiSelectFloatMenuOptions"], null),
        new("tab", ["FillTab", "UpdateSize", "get_IsVisible", "TabUpdate", "get_Hidden"], ["Verse.InspectTabBase"]),
        new("window", ["DoWindowContents", "WindowUpdate", "ExtraOnGUI", "WindowOnGUI"], ["Verse.Window"]),
        new("alert", ["GetReport", "GetExplanation", "GetLabel", "get_Active"], ["RimWorld.Alert"]),
        new("stats", ["GetExplanationUnfinalized", "GetExplanationFinalizePart", "GetStatDrawEntryLabel",
            "ExplanationPart", "GetInfoCardHyperlinks"], ["RimWorld.StatWorker", "RimWorld.StatPart"]),
        new("designator", ["CanDesignateCell", "CanDesignateThing", "SelectedUpdate", "DrawMouseAttachments",
            "DrawPanelReadout", "RenderHighlight", "get_Visible", "get_Disabled"], ["Verse.Designator"]),
        new("pawn-table", ["DoCell", "DoHeader", "GetHeaderTip", "Compare", "GetMinWidth", "GetOptimalWidth",
            "GetValueToCompare"], ["RimWorld.PawnColumnWorker"]),
        new("need", ["DrawOnGUI", "GetTipString"], ["RimWorld.Need"]),
        new("frame-update", ["MapComponentUpdate", "MapComponentOnGUI", "GameComponentUpdate", "GameComponentOnGUI",
            "WorldComponentUpdate", "WorldComponentOnGUI"], null),
        new("draw", ["DrawAt", "DynamicDrawPhaseAt", "DrawExtraSelectionOverlays", "PostDraw",
            "PostDrawExtraSelectionOverlays", "DrawGUIOverlay", "PostPrintOnto", "Print", "DrawGUIOverlayAt",
            "ExtraSelectionOverlaysOnGUI", "ProcessPostTickVisuals"],
            ["Verse.Thing", "Verse.ThingComp", "RimWorld.Planet.WorldObject", "RimWorld.Planet.WorldObjectComp"]),
    ];

    public bool Matches(CodeModel model, MethodDefinition m, Dictionary<string, HashSet<string>> baseSets)
    {
        if (!Methods.Contains(m.Name)) return false;
        if (Bases == null) return true;
        if (!baseSets.TryGetValue(Category + string.Join(",", Bases), out var set))
            baseSets[Category + string.Join(",", Bases)] = set = [.. Bases];
        return model.DerivesFrom(m.DeclaringType, set);
    }
}

/// <summary>Types, fields and methods to leave out, read from ignore.txt.</summary>
sealed class IgnoreList
{
    public readonly HashSet<string> Types = [];
    public readonly HashSet<string> Fields = [];
    public readonly HashSet<string> Methods = [];
    public readonly HashSet<string> TypePrefixes = [];
    public readonly List<string> Unknown = [];

    public static IgnoreList Load(string path)
    {
        var list = new IgnoreList();
        if (!File.Exists(path)) return list;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var kind = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (kind)
            {
                case "type": list.Types.Add(value); break;
                case "namespace": list.TypePrefixes.Add(value + "."); break;
                case "field": list.Fields.Add(value); break;
                case "method": list.Methods.Add(value); break;
            }
        }
        return list;
    }

    public void Check(CodeModel model)
    {
        foreach (var t in Types)
            if (model.FindType(t) == null) Unknown.Add("type:" + t);
        foreach (var f in Fields)
        {
            var (type, name) = Split(f);
            if (model.FindType(type)?.Fields.Any(x => x.Name == name) != true) Unknown.Add("field:" + f);
        }
        foreach (var m in Methods)
        {
            var (type, name) = Split(m);
            if (model.FindType(type)?.Methods.Any(x => x.Name == name) != true) Unknown.Add("method:" + m);
        }

        static (string, string) Split(string s)
        {
            int i = s.LastIndexOf("::", StringComparison.Ordinal);
            return i < 0 ? (s, "") : (s[..i], s[(i + 2)..]);
        }
    }

    public bool IgnoresType(TypeDefinition t) =>
        Types.Contains(t.FullName) || TypePrefixes.Any(p => t.FullName.StartsWith(p, StringComparison.Ordinal));
}

/// <summary>
/// Which types hold simulation state: anything saved (IExposable), plus classes reachable from saved state through
/// instance fields (caches such as ListerThings or the reachability cache aren't saved but the simulation reads them).
/// Interface and rendering types are excluded.
/// </summary>
sealed class StateTypes
{
    public static readonly HashSet<string> InterfaceBases =
    [
        "Verse.Window", "Verse.Gizmo", "Verse.InspectTabBase", "Verse.FloatMenuOption", "Verse.Designator",
        "RimWorld.Alert", "RimWorld.PawnColumnWorker", "RimWorld.MainButtonWorker", "Verse.Listing",
        "RimWorld.PawnTable", "Verse.Dialog_NodeTree", "Verse.DiaNode", "Verse.DiaOption",
    ];

    static readonly string[] VisualWords =
    [
        "Drawer", "Renderer", "Graphic", "Tweener", "Printer", "Overlay", "Sound", "Sustainer", "Material", "Mesh",
        "Texture", "Camera", "Fleck", "Effecter", "Silhouette", "Portrait", "Atlas", "Shader",
    ];

    readonly CodeModel model;
    readonly IgnoreList ignore;
    readonly HashSet<TypeDefinition> state = [];
    readonly Dictionary<TypeDefinition, bool> interfaceOrVisual = new();

    public int Count => state.Count;

    public StateTypes(CodeModel model, IgnoreList ignore)
    {
        this.model = model;
        this.ignore = ignore;

        foreach (var t in model.AllTypes)
            if (t.IsClass && !t.IsInterface && Eligible(t) && model.Implements(t, "Verse.IExposable"))
                state.Add(t);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var t in state.ToList())
                foreach (var f in t.Fields)
                {
                    if (f.IsStatic) continue;
                    foreach (var ft in Components(f.FieldType))
                        if (model.Type(ft) is { IsClass: true, IsInterface: false, IsEnum: false } td && Eligible(td) && state.Add(td))
                            changed = true;
                }
            // Subclasses of state types are state
            foreach (var t in model.AllTypes)
                if (!state.Contains(t) && t.IsClass && Eligible(t) && model.Base(t) is { } b && state.Contains(b) && state.Add(t))
                    changed = true;
        }
    }

    static IEnumerable<TypeReference> Components(TypeReference tr)
    {
        yield return tr;
        if (tr is TypeSpecification spec)
            foreach (var x in Components(spec.ElementType)) yield return x;
        if (tr is GenericInstanceType git)
            foreach (var arg in git.GenericArguments)
                foreach (var x in Components(arg)) yield return x;
    }

    bool Eligible(TypeDefinition t) => !CodeModel.IsCompilerGenerated(t) && !IsInterfaceOrVisual(t);

    public bool IsInterfaceOrVisual(TypeDefinition t)
    {
        if (interfaceOrVisual.TryGetValue(t, out var v)) return v;
        v = ignore.IgnoresType(t)
            || model.DerivesFrom(t, InterfaceBases)
            || VisualWords.Any(w => t.Name.Contains(w, StringComparison.Ordinal))
            || (t.DeclaringType != null && IsInterfaceOrVisual(t.DeclaringType));
        return interfaceOrVisual[t] = v;
    }

    public bool IsState(TypeDefinition t) => t != null && state.Contains(t);
}
