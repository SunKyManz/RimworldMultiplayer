# Interface side effects: status and next steps

Plan for continuing the static analysis of desyncs caused by interface and drawing code (started 2026-11-27, branch
`play-1.7-next`). See [README.md](README.md) for what the tool does and how to read its report.

## Setting up on another computer

1. Install the .NET 10 SDK (`global.json` pins 10.0.x) and RimWorld 1.7 (Steam). The tool reads
   `Assembly-CSharp.dll` from the RimWorld folder; pass `--game <dir>` if it's not in the default Steam location.
2. Build the mod once (`Source/Multiplayer.sln`) so a `Multiplayer.dll` exists. The tool picks the newest one in the
   repo (`1.7/AssembliesCustom` or `Source/Client/bin`); pass `--mp <dll>` to choose. It also needs a `0Harmony.dll`
   somewhere under `Source/` (any test or server build output has one) to decode the mod's patch attributes.
3. Run it:

   ```powershell
   dotnet run --project Tools/InterfaceSideEffects -c Release
   ```

   Expect about 545 leads, finishing in a few seconds. The report goes to `Tools/InterfaceSideEffects/out/` (gitignored).

Helpers for checking a lead: `--why <Type.Method>` (how the interface reaches it), `--il <Type.Method>` (its IL;
`Type.*` dumps every method of a type, and the type part can be a fragment like `Foo/<Bar>d__`).

## What the mod already handles (context for triage)

- **Interface Rand:** `AsyncTimeComp.PreContext` / `AsyncWorldTimeComp` run each map's and the world's ticks on their
  own saved random state, so interface `Rand` calls don't shift the simulation's stream. Interface `Rand` only matters
  when its result is stored, or when the same lazy code runs in a tick on another client.
- **Unique IDs:** `Patches/UniqueIDsManagerGuard.cs` snapshots every `UniqueIDsManager.next*` counter around
  `UIRoot_Play.UIRootOnGUI` and restores them afterwards. IDs taken in OnGUI (gizmos, inspect strings, float menus,
  windows) are rolled back; IDs taken in `Update` (drawing, `*ComponentUpdate`, `UIRootUpdate`) are not. Only the
  counters are restored: objects created and registered during OnGUI stay.
- `Patches/Determinism.cs` cancels or neutralizes many interface side effects. The tool rediscovers 12 of its 13
  interface-related patches (it misses `FloodUnfogPatch`), which is the main evidence its leads are worth reading.

## Triage so far (top ~35 leads, verified in IL, not reproduced in game)

### Confirmed desyncs: patch these first

1. **`Comp_AtmosphericHeater.get_Condition`** (Anomaly). The getter creates and registers a permanent
   `UnnaturalHeat` game condition when none is active. `CompTick` only reaches it through `get_Working`, which checks
   `Powered` and `HasFuel` first; the inspect string (`CompTempControl.CompInspectStringExtra → get_TargetTemperature`)
   reaches it unconditionally. Selecting an unpowered or unfueled heater registers the condition on that client only,
   and the ID guard then rolls back its ID, so a later condition gets a duplicate.
   *Fix idea:* outside ticks and commands (`!Multiplayer.Ticking && !Multiplayer.ExecutingCmds`), make `get_Condition`
   (or the interface reads of `TargetTemperature`) return the active condition without creating one, falling back to
   the default target temperature when there is none.
2. **`CompStatue.DrawAt → InitializeAsFactionLeaderStatue`** (Ideology/Odyssey statues). When `fakePawn` and `name`
   are both null, the first draw picks a faction leader with `GenCollection.RandomElement`, writes the saved
   `CompArt.titleInt`, `descriptionSeed` and pawn snapshot, and creates a fake pawn (genes take IDs). It runs in
   `Update`, so the ID guard doesn't cover it, with the interface's random state, and only on clients drawing that map.
   The mod already patches `CompStatue.InitFakePawn` (`Odyssey.cs`, only to avoid syncing the name).
   *Fix idea:* in MP, skip the initialization in `DrawAt` and do it in a tick or spawn path (`PostSpawnSetup` or
   `CompTick`) under a seeded `Rand.PushState` (for example thing ID combined with the world seed).
3. **`GeneSet.get_Label → GenerateName`** (Biotech genepacks). The name is generated lazily by the grammar system,
   which uses `Rand`. Whichever client first reads the label in the interface uses interface `Rand`; a client that
   first reads it during a tick (letter text, a job report) consumes the simulation's stream there, so the streams diverge.
   *Fix idea:* wrap `GenerateName` in `Rand.PushState(seed)` with a seed from the gene list, like the patches in
   `Patches/Seeds.cs`.

For each fix: add a regression check if one fits. `Tools/Check-Determinism.ps1 -UiNoise` exercises inspect strings,
gizmos and float menus but not drawing; a replay with an unpowered heater selected would cover lead 1.

### Probably real: look closer before patching

4. **`PawnFlyer.RecomputePosition`.** `Position` only advances when the flyer is drawn (`DynamicDrawPhaseAt`) or
   its `DrawPos` is read; its tick doesn't call it. Clients not viewing that map keep the old position. Check
   whether any tick code reads `DrawPos` (that would make it deterministic) and what reads `Position` mid-flight.
5. **`MonumentMarker.DrawGhost → Sketch.Rotate`** (Royalty). While drawing, it rotates the saved sketch to match a
   `Blueprint_Install` found on `Find.CurrentMap`. Check whether installing the marker rotates the sketch anyway
   (then the draw only applies it early) and whether `MonumentMarker` completion checks read the rotation.
6. **`Pawn_MeleeVerbs.GetUpdatedAvailableVerbsList`** reached from `StatWorker_MeleeDPS.GetExplanationUnfinalized`.
   It rebuilds the saved `terrainVerbs` from the interface, bypassing the existing `TryGetMeleeVerbPatch`. A cheap
   fix is probably to give it the same interface guard.
7. **`Blueprint_Door.DrawAt`** sets the saved rotation of 1×1 door blueprints to `DoorRotationAt` each draw, only on
   clients drawing the map. `Blueprint.TryReplaceWithSolidThing` passes the blueprint's rotation to the frame. Check
   whether doors recompute their rotation when spawned; if they do, this is harmless.

### Harmless or already handled (no action)

- Jobs and job IDs created while building float menus (techprints, childcare, work givers, styling station): the
  jobs come fresh from `SimplePool<Job>` and the ID guard rolls back `nextJobID`.
- `CompRefuelable.TargetFuelLevel`: synced (`SyncFields.cs`, `MpPrefix` on `Gizmo_SetFuelLevel.GizmoOnGUI`).
- `Skyfaller.angle` (and the passenger shuttle subclasses): saved, but only drawing reads it. It will still differ
  in saves, so expect noise if a tool compares save files.
- `CompToxifier.UpdateNextPolluteCell`: deterministic choice from map state, so timing doesn't matter.
- `ActiveLessonHandler` / `TutorSystem`: tutorial only.
- Text caches: `Book.EnsureDescriptionUpToDate`, `AbilityDef.GetTooltip`.
- Error-recovery paths: `CompArt.InitializeArt` from `GenerateImageDescription`, `Pawn_WorkSettings.ConfirmInitializedDebug`.
- `WildPlantSpawner.CacheWholeMapNumDesiredPlants` (via `CompAnimalPenMarker.PenFoodCalculator`): computed once per
  session from current state.
- Low, unverified: `ResearchManager.GetProgress` adds zero entries to `progress` and `anomalyKnowledge` (only
  matters if something iterates them); `HumanEmbryo.EnsureImplantTargetValid` clears an invalid target, an
  idempotent cleanup.

### Not triaged yet

Leads after about #35 in the current report, starting with `BoolGrid.Clear`/`ClearAndResizeTo` (likely
`GenDraw` scratch grids), `CompBiosculpterPod.CanReachRequiredIngredients`, `Game.set_CurrentMap` via
`CameraJumper` (on click, probably local), `Faction.get_AllegianceColor` (lazy color, possibly `Rand`), and
`RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms` from `Alert_AnimalFilth`. The mod blocks that rebuild
during `MapUpdate` (`AsyncTimePatches.cs`) but not from alerts. Then the "unsaved simulation state" leads (#39 on).

## Analyzer improvements to make next

1. **Model `UniqueIDsManagerGuard`:** tag entry categories that run inside `UIRootOnGUI` (gizmo, inspect,
   float-menu, window, tab `FillTab`, stats, pawn-table, need `DrawOnGUI`) and rank their ID-only findings as
   "rolled back". Keep ranking ID allocation from draw and frame-update entries at the top.
2. **Rank lazy random initializers first:** a getter or `Ensure*`/`Initialize*` method that both consumes `Rand` and
   writes saved state (leads 2 and 3 were both this). Today that's only a `+ Rand` tag.
3. **Count tick reachability:** a second search from tick entry points (`Tick`, `CompTick`, `TickRare`,
   `MapComponentTick` …). Lead 1 was only real because the tick reaches the same method under stricter conditions.
   At least show whether each lead's gateway is also reachable from ticks.
4. **Narrow virtual calls on `this`:** for `callvirt` with `ldarg.0` as receiver, only consider overrides in the
   declaring type's subclasses. That would shrink fan-outs like `CompStatue → Thing.Destroy → every override`.
5. Remaining unrecognized patches: 52 with computed targets that don't use `ldtoken` + `ldstr` (`AccessTools` by
   `MethodInfo`, loops over types).
6. Optional: run it in CI or in `Check-Determinism.ps1` against the committed `findings.tsv` to flag new leads after
   a game update.
