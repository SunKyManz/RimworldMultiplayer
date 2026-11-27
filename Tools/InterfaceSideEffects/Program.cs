using System.Diagnostics;
using System.Text;
using Mono.Cecil;

namespace InterfaceSideEffects;

/// <summary>
/// Static analysis for desync sources: walks the call graph from the code the game runs in the interface and while
/// drawing (gizmos, inspect strings, float menus, tabs, windows, alerts, stat explanations, designators, pawn tables,
/// frame updates, drawing) and reports what it can reach that changes simulation state or takes unique IDs. The
/// multiplayer mod runs the interface between ticks on every client independently, so any such change desyncs.
///
/// It's an over-approximation (virtual calls go to every override, branches are all taken) with heuristics against the
/// worst noise. Findings are leads to check in code, not proof. Runs on IL, so no game process is needed.
/// </summary>
sealed class Options
{
    public string GameDir;
    public string MpDll;
    public string RepoDir;
    public List<string> Mods = [];
    public string OutDir;
    public string IgnoreFile;
    public int MaxDepth = 12;
    public int MaxFanOut = 30;
    public int Limit = 150;
    public bool IncludeClicks;
    public bool IncludeStatics;
    public bool FollowAllDelegates;
    public string Why;
    public string Il;
}

static class Program
{
    const string Usage = """
        Finds interface and drawing code that can change simulation state (static analysis of the game's IL).

        dotnet run --project Tools/InterfaceSideEffects -- [options]

          --game <dir>        RimWorld folder (default: Steam's)
          --mp <dll>          Multiplayer.dll, to mark patched methods and stop at synced ones (default: newest build)
          --mod <dll>         Also analyze a mod assembly (repeatable)
          --out <dir>         Output folder (default: Tools/InterfaceSideEffects/out)
          --ignore <file>     Ignore list (default: ignore.txt next to the tool)
          --depth <n>         Max call depth from an entry point (default 12)
          --fanout <n>        Don't expand virtual calls with more overrides than this (default 30)
          --limit <n>         Findings listed per kind in the report (default 150; the TSV has all)
          --clicks            Also follow code behind button checks (on-click, usually synced or intended)
          --statics           Also report writes to static fields (noisy)
          --all-delegates     Also follow delegates stored for later (gizmo and float menu actions)
          --why <Type.Method> Print how the interface reaches a method instead of writing the report
        """;

    static int Main(string[] args)
    {
        Options o;
        try { o = Parse(args); }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (o == null) { Console.WriteLine(Usage); return 0; }

        Console.OutputEncoding = Encoding.UTF8;
        var watch = Stopwatch.StartNew();
        var gameAsm = Path.Combine(o.GameDir, "Assembly-CSharp.dll");
        if (!File.Exists(gameAsm))
        {
            var managed = Directory.EnumerateFiles(o.GameDir, "Assembly-CSharp.dll", SearchOption.AllDirectories).FirstOrDefault();
            if (managed == null) { Console.Error.WriteLine($"Assembly-CSharp.dll not found under {o.GameDir}"); return 1; }
            gameAsm = managed;
        }

        // Multiplayer.dll's patch attributes need 0Harmony to decode their enum arguments
        var harmonyDirs = o.RepoDir == null ? [] : Directory.EnumerateFiles(Path.Combine(o.RepoDir, "Source"), "0Harmony.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).Take(1);
        var model = new CodeModel([Path.GetDirectoryName(gameAsm), .. o.Mods.Select(Path.GetDirectoryName), Path.GetDirectoryName(o.MpDll ?? gameAsm), .. harmonyDirs]);
        model.Load(gameAsm, analyzed: true);
        foreach (var mod in o.Mods) model.Load(mod, analyzed: true);
        Console.WriteLine($"Loaded {string.Join(", ", model.Modules.Select(m => m.Assembly.Name.Name))}");

        model.BuildOverrides();
        var ignore = IgnoreList.Load(o.IgnoreFile);
        ignore.Check(model);

        // Type names the rules depend on: a rename in a game update would silently drop entry points
        foreach (var name in EntryRule.All.SelectMany(r => r.Bases ?? []).Concat(StateTypes.InterfaceBases).Distinct())
            if (model.FindType(name) == null)
            {
                var simple = name[(name.LastIndexOf('.') + 1)..];
                var guess = model.AllTypes.Where(t => t.Name == simple).Select(t => t.FullName).FirstOrDefault();
                Console.WriteLine($"Warning: type {name} not found{(guess != null ? $" (did you mean {guess}?)" : "")}");
            }
        foreach (var u in ignore.Unknown)
        {
            var simple = u[(u.LastIndexOf('.') + 1)..];
            var guess = model.AllTypes.Where(t => t.Name == simple).Select(t => t.FullName).FirstOrDefault();
            Console.WriteLine($"Warning: ignore entry {u} matches nothing{(guess != null ? $" (did you mean {guess}?)" : "")}");
        }
        var mp = MpKnowledge.Load(model, o.MpDll);
        Console.WriteLine($"Multiplayer.dll: {mp.Patches.Count} patch targets, {mp.SyncMethods.Count / 2} sync methods, {mp.SyncFieldNames.Count} sync field names ({o.MpDll ?? "none"})");

        var state = new StateTypes(model, ignore);
        var analysis = new Analysis(model, state, mp, ignore, o);
        Console.WriteLine($"{state.Count} simulation state types, {analysis.SavedFieldCount} saved fields");

        var findings = analysis.Run();
        Console.WriteLine($"Visited {analysis.Visited} method states from {analysis.EntriesByCategory.Values.Sum()} entry points in {watch.Elapsed.TotalSeconds:F1}s");

        if (o.Il != null)
        {
            int sep = o.Il.LastIndexOf('.');
            // Type part: full name, simple name, or a fragment of the full name ("Foo/<Bar>d__")
            var typePart = o.Il[..sep];
            var types = model.AllTypes.Where(t => t.FullName == typePart || t.Name == typePart).ToList();
            if (types.Count == 0) types = model.AllTypes.Where(t => t.FullName.Contains(typePart, StringComparison.Ordinal)).ToList();
            foreach (var m in types.SelectMany(t => t.Methods).Where(x => x.Name == o.Il[(sep + 1)..] && x.HasBody))
            {
                Console.WriteLine(m.FullName);
                foreach (var ins in m.Body.Instructions) Console.WriteLine($"  {ins}");
            }
            return 0;
        }

        if (o.Why != null)
        {
            var paths = analysis.Why(o.Why).ToList();
            Console.WriteLine(paths.Count == 0 ? $"The interface doesn't reach '{o.Why}' (within the limits)." : $"How the interface reaches '{o.Why}':");
            foreach (var (category, path) in paths)
                Console.WriteLine($"  [{category}] {string.Join(" → ", path.Select(CodeModel.Display))}");
            return 0;
        }

        Directory.CreateDirectory(o.OutDir);
        var md = Path.Combine(o.OutDir, "report.md");
        var tsv = Path.Combine(o.OutDir, "findings.tsv");
        File.WriteAllText(md, Report.Markdown(findings, analysis, model, mp, ignore, o, gameAsm));
        File.WriteAllText(tsv, Report.Tsv(findings));

        foreach (var g in findings.GroupBy(f => f.Kind).OrderBy(g => g.Key))
            Console.WriteLine($"  {Report.Title(g.Key),-34} {g.Count(),5}  ({g.Count(f => f.Patches.Count == 0)} not through an MP patch)");
        Console.WriteLine($"Report: {md}");
        Console.WriteLine($"All findings: {tsv}");
        return 0;
    }

    static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "-h": case "--help": return null;
                case "--game": o.GameDir = Next(); break;
                case "--mp": o.MpDll = Next(); break;
                case "--mod": o.Mods.Add(Path.GetFullPath(Next())); break;
                case "--out": o.OutDir = Next(); break;
                case "--ignore": o.IgnoreFile = Next(); break;
                case "--depth": o.MaxDepth = int.Parse(Next()); break;
                case "--fanout": o.MaxFanOut = int.Parse(Next()); break;
                case "--limit": o.Limit = int.Parse(Next()); break;
                case "--clicks": o.IncludeClicks = true; break;
                case "--statics": o.IncludeStatics = true; break;
                case "--all-delegates": o.FollowAllDelegates = true; break;
                case "--why": o.Why = Next(); break;
                case "--il": o.Il = Next(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }

        var toolDir = FindUp(AppContext.BaseDirectory, d => File.Exists(Path.Combine(d, "InterfaceSideEffects.csproj")));
        var repo = o.RepoDir = FindUp(toolDir ?? Environment.CurrentDirectory, d => File.Exists(Path.Combine(d, "Source", "Multiplayer.sln")));

        o.GameDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "RimWorld");
        o.OutDir ??= Path.Combine(toolDir ?? Environment.CurrentDirectory, "out");
        o.IgnoreFile ??= Path.Combine(toolDir ?? AppContext.BaseDirectory, "ignore.txt");
        if (o.MpDll == null && repo != null)
        {
            o.MpDll = Directory.EnumerateDirectories(repo)
                .SelectMany(d => Directory.EnumerateFiles(d, "Multiplayer.dll", SearchOption.AllDirectories))
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        return o;
    }

    static string FindUp(string start, Func<string, bool> pred)
    {
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (pred(d.FullName)) return d.FullName;
        return null;
    }
}

static class Report
{
    public static string Title(SinkKind k) => k switch
    {
        SinkKind.IdAllocation => "Takes unique IDs",
        SinkKind.SavedWrite => "Writes saved state",
        SinkKind.StateWrite => "Writes unsaved simulation state",
        SinkKind.CacheWrite => "Writes caches or dirty flags",
        SinkKind.RefWrite => "Passes state fields by ref",
        SinkKind.StaticWrite => "Writes static fields",
        SinkKind.Rand => "Consumes Rand",
        _ => k.ToString(),
    };

    static string Explain(SinkKind k) => k switch
    {
        SinkKind.IdAllocation => "Taking an ID on one client shifts every later ID there. The worst kind: it desyncs every thing created afterwards.",
        SinkKind.SavedWrite => "A field `ExposeData` saves. Changing it from the interface is a desync unless every client does the same.",
        SinkKind.StateWrite => "An unsaved field of an object the simulation uses. Desyncs if the simulation reads it, and a client that reloads loses it.",
        SinkKind.CacheWrite => "Lazy caches are usually harmless when the value only depends on state. They desync when the cached value depends on when it's computed, or when filling it has other effects.",
        SinkKind.RefWrite => "Usually a widget (checkbox, slider, text field) editing the field in place. Needs a sync field unless the mod already watches it some other way.",
        SinkKind.StaticWrite => "Static fields of non-interface types. Mostly caches.",
        SinkKind.Rand => "Informational: the mod gives each map and the world their own saved random state during ticks, so the interface's Rand use doesn't shift the simulation's. It matters when the random result is stored (see \"+ Rand\" on writes) or when the same code also runs during ticks with different call counts.",
        _ => "",
    };

    public static string Markdown(List<Finding> findings, Analysis a, CodeModel model, MpKnowledge mp, IgnoreList ignore, Options o, string gameAsm)
    {
        mpForPatches = mp;
        var sb = new StringBuilder();
        var version = Path.Combine(Path.GetDirectoryName(gameAsm), "Version.txt");
        sb.AppendLine("# Interface side effects");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} from {string.Join(", ", model.Modules.Select(m => m.Assembly.Name.Name))}" +
                      (File.Exists(version) ? $" (RimWorld {File.ReadAllText(version).Trim()})" : "") + ".");
        sb.AppendLine();
        sb.AppendLine("Code reachable from the interface or from drawing that changes the simulation. These are static-analysis leads: " +
                      "virtual calls go to every override and every branch counts as taken, so check each one in code before you act on it.");
        sb.AppendLine();
        sb.AppendLine($"- Entry points: {string.Join(", ", a.EntriesByCategory.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"))}");
        sb.AppendLine($"- Method states visited: {a.Visited}; depth limit {o.MaxDepth}; virtual calls with more than {o.MaxFanOut} overrides not expanded ({a.CappedCalls} call sites)");
        sb.AppendLine($"- Multiplayer.dll: `{mp.Path ?? "not found"}`: {mp.Patches.Count} patch targets ({mp.PatchesWithComputedTargets} more patches with computed targets aren't recognized), " +
                      $"search stopped {a.StoppedAtSync} times at a synced method");
        sb.AppendLine($"- Not followed: code behind button checks{(o.IncludeClicks ? " (followed: --clicks)" : "")}, delegates stored for later such as gizmo actions{(o.FollowAllDelegates ? " (followed: --all-delegates)" : "")}" +
                      $"{(o.IncludeStatics ? "" : ", static fields (--statics)")}");
        if (ignore.Unknown.Count > 0)
            sb.AppendLine($"- Ignore entries matching nothing: {string.Join(", ", ignore.Unknown.Select(x => $"`{x}`"))}");
        sb.AppendLine();
        sb.AppendLine("| Kind | Findings | Not through an MP patch | Meaning |");
        sb.AppendLine("|---|---:|---:|---|");
        foreach (var g in findings.GroupBy(f => f.Kind).OrderBy(g => g.Key))
            sb.AppendLine($"| {Title(g.Key)} | {g.Count()} | {g.Count(f => f.Patches.Count == 0)} | {Explain(g.Key)} |");
        sb.AppendLine();

        // How many existing fixes the tool rediscovers says how much to trust it
        var patchedOnPaths = findings.SelectMany(f => f.Patches).Distinct().OrderBy(x => x).ToList();
        sb.AppendLine($"Mod patches lying on some finding's path: {patchedOnPaths.Count}. {string.Join(", ", patchedOnPaths.Take(60).Select(x => $"`{x}`"))}{(patchedOnPaths.Count > 60 ? ", …" : "")}");
        sb.AppendLine();

        var groups = Leads(findings);
        sb.AppendLine($"## Leads ({groups.Count})");
        sb.AppendLine();
        sb.AppendLine("Findings grouped by the first method on their path that changes state itself: fixing or patching that method usually removes everything below it. " +
                      "Order: not through an MP patch first, then display and drawing code before windows, tabs and tables (which edit things on purpose and are often synced), " +
                      "then worst kind, then shortest path. *MP patch* means a method on the path is patched by Multiplayer.dll, so it's probably handled already.");
        sb.AppendLine();

        int n = 0;
        foreach (var g in groups.Take(o.Limit))
        {
            n++;
            var tags = new List<string> { g.Head.Category, $"depth {g.HeadPath.Count - 1}" };
            if (g.Own.Any(f => f.Kind == SinkKind.Rand) && g.Own.Any(f => f.Kind != SinkKind.Rand)) tags.Add("+ Rand");
            if (g.Patches.Count > 0) tags.Add($"MP patch: {string.Join(", ", g.Patches)}");
            sb.AppendLine($"{n}. **{CodeModel.Display(g.Gateway)}**: {Title(g.Worst).ToLowerInvariant()} · {string.Join(" · ", tags)}");
            sb.AppendLine($"   <br>`{string.Join(" → ", g.HeadPath.Select(CodeModel.Display))}`");
            var own = g.Own.OrderBy(f => f.Kind).ThenBy(f => f.Sink.Offset).ToList();
            sb.AppendLine($"   <br>Here: {string.Join("; ", own.Take(8).Select(Describe))}{(own.Count > 8 ? $"; … {own.Count - 8} more" : "")}");
            if (g.Deeper.Count > 0)
            {
                var kinds = g.Deeper.GroupBy(f => f.Kind).OrderBy(k => k.Key).Select(k => $"{k.Count()} {Title(k.Key).ToLowerInvariant()}");
                var examples = g.Deeper.OrderBy(f => f.Kind).ThenBy(f => f.Path.Count).Take(3)
                    .Select(f => $"{Describe(f)} in `{string.Join(" → ", f.Path.SkipWhile(m => m != g.Gateway).Skip(1).Select(CodeModel.Display))}`");
                sb.AppendLine($"   <br>Below it: {g.Deeper.Count} ({string.Join(", ", kinds)}), e.g. {string.Join("; ", examples)}");
            }
        }
        if (groups.Count > o.Limit) sb.AppendLine($"\n… {groups.Count - o.Limit} more leads in findings.tsv (column `lead`).");
        return sb.ToString();
    }

    static string Describe(Finding f)
    {
        var what = f.Kind == SinkKind.Rand ? $"calls `{f.Sink.Member}`" : $"`{f.Sink.Member}`";
        var notes = new List<string> { ShortKind(f.Kind) };
        if (f.Sink.Detail != null) notes.Add(f.Sink.Detail);
        return $"{what} ({string.Join(", ", notes)}, IL_{f.Sink.Offset:x4})";
    }

    static string ShortKind(SinkKind k) => k switch
    {
        SinkKind.IdAllocation => "unique ID",
        SinkKind.SavedWrite => "saved",
        SinkKind.StateWrite => "unsaved",
        SinkKind.CacheWrite => "cache",
        SinkKind.RefWrite => "by ref",
        SinkKind.StaticWrite => "static",
        SinkKind.Rand => "Rand",
        _ => k.ToString(),
    };

    // Windows, tabs and tables edit things on purpose (often synced); code that merely displays or draws never should
    static readonly HashSet<string> InteractiveCategories = ["window", "tab", "pawn-table"];

    sealed record Lead(MethodDefinition Gateway, Finding Head, List<MethodDefinition> HeadPath, List<Finding> Own, List<Finding> Deeper, List<string> Patches)
    {
        public SinkKind Worst => (Own.Any(f => f.Kind != SinkKind.Rand) ? Own.Where(f => f.Kind != SinkKind.Rand) : Own)
            .Concat(Deeper.Where(f => f.Kind != SinkKind.Rand)).Min(f => f.Kind);
    }

    static Dictionary<Finding, MethodDefinition> gateways;

    static MethodDefinition GatewayOf(Finding f) => gateways[f];

    static List<Lead> Leads(List<Finding> findings)
    {
        // Rand alone doesn't change state under the mod, so it doesn't make a method a gateway for other findings
        var changing = findings.Where(f => f.Kind != SinkKind.Rand).Select(f => f.Method).ToHashSet();
        gateways = findings.ToDictionary(f => f, f => f.Path.FirstOrDefault(changing.Contains) ?? f.Method);

        return findings.GroupBy(GatewayOf).Select(g =>
            {
                var own = g.Where(f => f.Method == g.Key).ToList();
                var head = own.OrderBy(f => f.Path.Count).FirstOrDefault() ?? g.OrderBy(f => f.Path.Count).First();
                var headPath = head.Path.TakeWhile(m => m != g.Key).Append(g.Key).ToList();
                var deeper = g.Where(f => f.Method != g.Key).ToList();
                if (own.Count == 0) { own = [head]; deeper.Remove(head); }
                return new Lead(g.Key, head, headPath, own, deeper, head.Patches.Where(p => headPath.Any(m => PatchedBy(m, p, head))).ToList());
            })
            .OrderBy(l => l.Patches.Count > 0)
            .ThenBy(l => InteractiveCategories.Contains(l.Head.Category))
            .ThenBy(l => l.Worst)
            .ThenBy(l => l.HeadPath.Count)
            .ThenBy(l => CodeModel.Display(l.Gateway), StringComparer.Ordinal)
            .ToList();
    }

    static MpKnowledge mpForPatches;
    static bool PatchedBy(MethodDefinition m, string patch, Finding f) => mpForPatches?.PatchesOn(m).Contains(patch) ?? true;

    public static string Tsv(List<Finding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("kind\tmethod\tmember\tvia\til\tcategory\tdepth\tplusRand\tonClick\tmpPatches\tlead\tpath");
        foreach (var f in findings.OrderBy(f => f.Kind).ThenBy(f => f.Patches.Count > 0).ThenBy(f => f.Path.Count))
            sb.AppendLine(string.Join('\t',
                f.Kind, CodeModel.Display(f.Method), f.Sink.Member, f.Sink.Detail ?? "", $"IL_{f.Sink.Offset:x4}", f.Category,
                f.Path.Count - 1, f.AlsoConsumesRand, f.Flags.HasFlag(PathFlags.OnClick), string.Join(",", f.Patches),
                gateways != null && gateways.TryGetValue(f, out var g) ? CodeModel.Display(g) : "",
                string.Join(" > ", f.Path.Select(CodeModel.Display))));
        return sb.ToString();
    }
}
