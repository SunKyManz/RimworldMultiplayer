# Determinism testing: status and plan

Where the proactive desync work stands on `play-1.7-next`, how to set up another computer to continue it, and what to
do next. Written to hand the work over; remove or fold into the README once it's done.

## The idea

Most desyncs aren't caused by randomness itself: each map and the world have their own saved random state during
ticks, so the simulation's random numbers are the same on every client. Desyncs come from code that runs on only
some clients, mostly the interface (inspect strings, gizmos, tooltips, alerts, drawing), changing simulation state:
caches, saved fields, unique IDs. The random state is just where the difference usually shows first.

Two tools look for that before players hit it:

| Tool | What it does | Strength | Weakness |
|---|---|---|---|
| `Tools/Check-Determinism.ps1 -UiNoise` (dynamic) | Plays a replay twice, once quiet and once while `UiNoise` imitates a busy player between ticks, and compares fingerprints | No false positives: a difference is a real effect on the game | Only finds what the replay contains and the noise happens to touch |
| `Tools/InterfaceSideEffects` (static) | Walks the game's IL from ~1,900 interface entry points and reports reachable state writes | Covers all code, no replay needed | Over-approximates: findings are leads to check |

They work best together: the static report says where to look, and a replay that contains the thing confirms it.

## Current status (2026-11-27)

Done and pushed:

- `Source/Client/Debug/UiNoise.cs`: the simulated interface activity (`-replaynoise=seed:everyTicks`).
- `Source/Client/Debug/DeterminismFingerprint.cs`: per-batch fingerprint (world and map ticks, random states, a hash of
  every map's things) and `Dump()` for `-replaydump=tick,...`. The hash skips motes and uses id order, because
  real-time motes (`Mote_Text`) expire after wall-clock seconds and their swap-removal reordered the things list,
  which made two quiet runs look diverged.
- `Tools/Check-Determinism.ps1`: `-UiNoise`, `-NoiseEvery`, `-DumpTicks`, `-ModIds`; a fresh `-savedatafolder` per run
  (the player's own mod list and saves are never touched); handles the 1.7 engine (`RimWorld.exe`, no headless mode,
  log copied from the run's save folder).
- `Tools/InterfaceSideEffects`: the static analysis tool. First report (RimWorld 1.7.5021): 545 leads, 20 findings
  that take unique ids (12 not already covered by an MP patch).

Verified on one 1.7.5021 replay (small colony, minimal mods, no recorded commands), 20,000 ticks:

- quiet vs quiet: identical
- quiet vs UI noise every 10 ticks (2,000 passes, 116,307 interface calls, none threw): identical

Not verified: replays with recorded commands, bigger colonies, other mods, other PCs.

## Setting up another computer

1. **Clone and build.** Check out `play-1.7-next` and build the client (`dotnet build Source/Client/Multiplayer.csproj
   -c Release`). The build copies the mod's DLLs into the repo's `1.7/` folder, so the repo itself is the mod folder.

2. **Install the dev build in the game.** RimWorld 1.7 has no `Mods` folder by default. Create it in the game folder and
   link the repo into it (PowerShell; adjust both paths):

   ```powershell
   New-Item -ItemType Directory -Force "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods"
   ```

   ```powershell
   New-Item -ItemType Junction -Path "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Multiplayer" -Target "<path to the repo>"
   ```

3. **Make Harmony 3 load without Steam.** The check starts `RimWorld.exe` directly. If Steam isn't running, Workshop
   mods aren't found, and Multiplayer fails with `Could not load type 'HarmonyLib.Harmony'`. Either keep Steam running
   or link the Workshop copy of Harmony (subscribe to it first, item 2009463077) as a local mod:

   ```powershell
   New-Item -ItemType Junction -Path "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Harmony" -Target "C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2009463077"
   ```

   The Workshop copy of Multiplayer only supports up to 1.6, so it doesn't conflict with the local one on 1.7.

4. **Get a 1.7 replay.** Replays from 1.6 or with other mods load without their maps, and the check then tests nothing
   (it stops when the noise made no calls). To record one without touching your normal mod list, start the game with
   its own save folder:

   ```powershell
   & "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorld.exe" -savedatafolder=C:\RimWorld17Data
   ```

   Activate only Harmony, Core, the DLCs and the local Multiplayer, host a game, play a few minutes and save. Then copy
   the replay (`C:\RimWorld17Data\MpReplays\*.zip`) into the folder the script reads,
   `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\MpReplays`, under a name without `:` or
   `;` (the earlier one was saved as `Determinism-1.7.zip`; it isn't in the repo, so record a new one or copy it over).

5. **Run the checks.** Each run opens a game window on 1.7 and quits by itself.

   ```powershell
   ./Tools/Check-Determinism.ps1 -Replay Determinism-1.7 -Ticks 20000
   ```

   ```powershell
   ./Tools/Check-Determinism.ps1 -Replay Determinism-1.7 -Ticks 20000 -UiNoise
   ```

   Run the quiet one first: if two quiet runs already differ, the replay or the fingerprint is the problem, not the
   interface. Results go to `determinism-runs/<timestamp>/` (git-ignored).

6. **Run the static analysis.**

   ```powershell
   dotnet run --project Tools/InterfaceSideEffects -c Release
   ```

   The report is `Tools/InterfaceSideEffects/out/report.md` (git-ignored); `--why <Type.Method>` shows how the
   interface reaches a method.

## When a check reports DIVERGED

1. Note the tick in the report, and whether the random state or only the things hash differs.
2. Rerun with `-DumpTicks <tick>` (and the sample before it) and diff `runN.things-<tick>.txt` between runs. That names
   the thing, and the field (position, hit points, stack, job) that differs.
3. Check the noisy run's log for `Multiplayer UI noise:` warnings, which name the kind of call that threw.
4. To find which interface call did it, narrow the noise: a smaller `-NoiseEvery` around the tick, or temporarily
   disable groups of calls in `UiNoise.PokeThing`/`PokePawn`.
5. Cross-check the static report for that thing's methods.

## Next steps, in order

1. **Confirm the top static leads with the dynamic check.** The report's strongest leads take unique ids from
   interface code, which desyncs every thing created afterwards. For example:
   - `Comp_AtmosphericHeater.get_Condition`, reached from `CompTempControl.CompInspectStringExtra`, appears to create
     and register a `GameCondition` (with a unique id) from an inspect string.
   - `CompStatue.InitializeAsFactionLeaderStatue` from `CompStatue.DrawAt` (unique ids and Rand while drawing).
   - `CompTechprint.CompFloatMenuOptions` makes a `Job` (a job id) while building the float menu.

   For each: check the code, then record a short replay that contains the thing (an atmospheric heater, a faction
   leader statue, a techprint), and run the check with `-UiNoise`. Fix confirmed ones the way `Patches/Determinism.cs`
   does (skip or isolate in the interface), and add the method to `UiNoise` if the noise didn't already reach it.

2. **Make UiNoise reach more of what the static tool covers.** It doesn't yet run `ITab.FillTab`, window contents,
   designators, pawn table columns or drawing, because those need the GUI. On 1.7 every run has a window, so try calling
   them inside a real `OnGUI` pass (for example from a `MapInterface` postfix in replay mode) instead of between ticks.

3. **Longer and richer replays.** Record one with commands (draft, build, work priorities, trade, a raid) and run
   `-Ticks 60000` or more. Keep a few small replays as a regression set.

4. **Two PCs.** Run the same replay on another PC and compare with
   `./Tools/Check-Determinism.ps1 -Compare <mine>.ticks -Against <theirs>.ticks` (CPU float differences).

5. **Mods.** Rerun with the usual mod list (`-ModIds` in load order, plus a replay recorded with them) and pass mod
   assemblies to the static tool with `--mod <dll>`.

6. **Automate.** Once there are a few stable replays, run both checks before releases. They need a desktop session on
   1.7, so a CI runner would need a GPU or a virtual display.

## Known issues found along the way

- **Real-time motes reorder the things list.** In multiplayer, `Mote_Text` and other `realTime` motes expire at a
  different tick on each client, and their removal reorders `listerThings.AllThings` (and the per-def list). In the
  tested replay the simulation never diverged, so nothing seems to depend on that order, but it would desync any code
  that picks between things by their position in that list. The fingerprint now ignores it; the game behavior is
  unchanged.
- **The Arbiter on 1.7.** `HostUtil.StartArbiter` passes `-batchmode -nographics`, which the 1.7 engine doesn't seem to
  support, so the Arbiter probably opens a visible window there. Not tested.
- **Interface `Rand` use is mostly harmless.** Because maps and the world restore their own random state at each tick,
  the interface's `Rand` calls don't shift the simulation's numbers. They matter only when the result is stored in
  simulation state (as in the static report's leads).
