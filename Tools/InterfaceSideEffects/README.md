# Interface side effects

Static analysis for one class of desyncs: interface or drawing code that changes simulation state. The mod runs the
interface between ticks on each client independently, so a gizmo, inspect string, tooltip, alert or draw call that
writes game state (or takes a unique ID) makes that client diverge.

It reads `Assembly-CSharp.dll` with Mono.Cecil (no game process needed), starts from about 1,900 interface entry points
(`GetGizmos`, `GetInspectString`, float menu providers, `ITab.FillTab`, `Window.DoWindowContents`, `Alert.GetReport`,
stat explanations, designators, pawn table columns, `*ComponentUpdate`, `DrawAt` and friends) and walks the call graph
looking for:

- unique ID allocation (`UniqueIDsManager`)
- writes to fields `ExposeData` saves
- writes to unsaved fields of simulation objects (anything saved, plus what saved objects hold, minus interface and
  rendering types)
- cache and lazy-init writes, state fields passed by `ref` (widgets editing them), and `Rand` use outside
  `PushState`/`PopState`

It reads Multiplayer.dll too: findings whose path crosses a method the mod patches are marked and listed last, and the
search stops at registered sync methods (calling them from the interface sends a command).

```powershell
dotnet run --project Tools/InterfaceSideEffects -c Release
```

The report is written to `Tools/InterfaceSideEffects/out/report.md` (leads), along with `findings.tsv` (every finding with
its full path). `--help` lists the options. Two help with following up a lead:

```powershell
dotnet run --project Tools/InterfaceSideEffects -c Release -- --why Comp_AtmosphericHeater.get_Condition
```

```powershell
dotnet run --project Tools/InterfaceSideEffects -c Release -- --il Comp_AtmosphericHeater.get_Condition
```

## Reading the report

Findings are grouped into *leads*: the first method on the path that changes state itself. Patching or fixing that
method usually removes everything below it.

It over-approximates: virtual calls go to every override (up to `--fanout`), every branch counts as taken, and there's
no data flow beyond a few local patterns (new objects, objects made by `Make*`/`Generate*`/`Create*` factories,
constructors). So treat each lead as a question to check in code. What it leaves out by default:

- code behind `if (Widgets.Button…(…))` and delegates stored for later (gizmo and float menu actions): these run on
  click and the mod syncs them (`--clicks`, `--all-delegates` to include them)
- static fields (`--statics`)
- whatever `ignore.txt` lists (interface-owned state such as the selector, camera and play settings)

`Rand` findings are informational: during ticks each map and the world run on their own saved random state
(`AsyncTimeComp.PreContext`), so the interface's `Rand` use doesn't shift the simulation's. It matters when the random
result gets stored, which is why writes in a method that also uses `Rand` are tagged `+ Rand`.

Out of scope: desyncs from iteration order, hash codes, floating point, reading interface state (camera, selection)
during ticks, and code that only differs between clients because of what was loaded or cached before.
