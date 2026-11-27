using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Imitates a busy player for the <c>-replay</c> command-line mode: between ticks it selects random things and
    /// runs the interface code a player would trigger (inspect strings, gizmos, inspect tabs, float menus, tooltips,
    /// stat explanations, alerts, room stats). None of it may change the simulation, so a run with noise must
    /// fingerprint the same as a run without it (see Tools/Check-Determinism.ps1 -UiNoise). A difference means some
    /// interface code changed game state, the most common cause of desyncs.
    /// Enabled with <c>-replaynoise=seed:everyTicks</c>.
    /// </summary>
    public static class UiNoise
    {
        public const int ThingsPerMapPerPass = 8;
        private const int StatsPerPawn = 6;

        // System.Random on purpose: the harness itself must never touch Verse.Rand or any game state
        private static System.Random random;

        public static int EveryTicks { get; private set; }
        public static bool Enabled => random != null;

        public static int passes;
        public static int calls;
        public static int skippedPasses;
        public static readonly Dictionary<string, int> failures = new();

        private static List<StatDef> stats;

        public static void Configure(int seed, int everyTicks)
        {
            random = new System.Random(seed);
            EveryTicks = Math.Max(1, everyTicks);
            passes = calls = skippedPasses = 0;
            failures.Clear();
        }

        public static void Run()
        {
            if (!Enabled) return;

            // Only exercise code in the state the real interface runs in, or the test would report false desyncs
            if (!Multiplayer.InInterface)
            {
                skippedPasses++;
                return;
            }

            passes++;
            stats ??= DefDatabase<StatDef>.AllDefsListForReading.Where(s => s.showOnPawns).ToList();

            var prevMap = Find.CurrentMap;
            var selector = Find.Selector;
            var prevSelected = selector.SelectedObjects.ToList();

            try
            {
                foreach (var map in Find.Maps.OrderBy(m => m.uniqueID).ToList())
                    RunOnMap(map);
            }
            finally
            {
                selector.ClearSelection();
                foreach (var obj in prevSelected)
                    selector.Select(obj, playSound: false, forceDesignatorDeselect: false);

                if (prevMap != null && Find.Maps.Contains(prevMap) && Find.CurrentMap != prevMap)
                    Current.Game.CurrentMap = prevMap;
            }
        }

        private static void RunOnMap(Map map)
        {
            // Switching maps is itself something players do
            Try("switch map", () => Current.Game.CurrentMap = map);

            // The map interface's OnGUI and update run with the map's time set (SetMapTimeForUI)
            var time = TimeSnapshot.GetAndSetFromMap(map);
            try
            {
                if (Find.UIRoot is UIRoot_Play { alerts: { } alerts })
                    Try("alerts", alerts.AlertsReadoutUpdate);

                var things = map.listerThings.AllThings;
                for (int i = 0; i < ThingsPerMapPerPass && things.Count > 0; i++)
                    PokeThing(things[random.Next(things.Count)], map);
            }
            finally
            {
                time?.Set();
            }
        }

        private static void PokeThing(Thing thing, Map map)
        {
            if (thing.Destroyed || !thing.Spawned) return;

            var selector = Find.Selector;
            Try("select", () =>
            {
                selector.ClearSelection();
                selector.Select(thing, playSound: false, forceDesignatorDeselect: false);
            });

            Try("label", () => _ = thing.LabelCap + thing.LabelMouseover);
            Try("inspect string", () => _ = thing.GetInspectString());
            Try("description", () => _ = thing.DescriptionDetailed);

            Try("gizmos", () =>
            {
                foreach (var gizmo in thing.GetGizmos().ToList())
                {
                    _ = gizmo.Disabled;
                    _ = gizmo.Visible;
                    if (gizmo is Command command)
                    {
                        _ = command.LabelCap + command.Desc;
                        _ = command.RightClickFloatMenuOptions?.ToList();
                    }
                }
            });

            Try("inspect tabs", () =>
            {
                foreach (var tab in thing.GetInspectTabs() ?? Enumerable.Empty<InspectTabBase>())
                    _ = tab.IsVisible;
            });

            Try("room stats", () =>
            {
                var room = thing.GetRoom();
                if (room is { ProperRoom: true })
                    _ = room.GetStat(RoomStatDefOf.Impressiveness);
            });

            if (thing is Pawn pawn)
                PokePawn(pawn, map);
        }

        private static void PokePawn(Pawn pawn, Map map)
        {
            Try("need tooltips", () =>
            {
                foreach (var need in pawn.needs?.AllNeeds ?? Enumerable.Empty<Need>())
                    _ = need.GetTipString();
            });

            Try("hediff tooltips", () =>
            {
                foreach (var hediff in pawn.health.hediffSet.hediffs.ToList())
                    _ = hediff.GetTooltip(pawn, false);
            });

            // What the info card shows
            for (int i = 0; i < StatsPerPawn && stats.Count > 0; i++)
            {
                var stat = stats[random.Next(stats.Count)];
                Try("stat explanation", () =>
                {
                    var req = StatRequest.For(pawn);
                    if (!stat.Worker.ShouldShowFor(req)) return;
                    float value = stat.Worker.GetValue(req);
                    _ = stat.Worker.GetExplanationFull(req, ToStringNumberSense.Absolute, value);
                });
            }

            // Right-clicking a random nearby cell with the pawn selected
            if (pawn.IsColonistPlayerControlled)
            {
                var cell = pawn.Position + new IntVec3(random.Next(-8, 9), 0, random.Next(-8, 9));
                if (cell.InBounds(map))
                    Try("float menu", () => _ = FloatMenuMakerMap.GetOptions(new List<Pawn> { pawn }, cell.ToVector3Shifted(), out _));
            }
        }

        private static void Try(string what, Action action)
        {
            calls++;
            int randDepth = Rand.stateStack.Count;

            try
            {
                action();
            }
            catch (Exception e)
            {
                // Headless runs have no textures or GUI, so some interface code throws; that's expected and only
                // counted. The point is what the code changed before (or without) throwing.
                failures.TryGetValue(what, out int count);
                failures[what] = count + 1;
                if (count == 0)
                    Log.Warning($"Multiplayer UI noise: '{what}' threw (counted, not logged again): {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                AsyncTimeComp.RestoreRandStack(randDepth, $"UI noise ({what})");
            }
        }

        public static string Summary()
        {
            var lines = new List<string>
            {
                $"passes={passes} skipped={skippedPasses} calls={calls} every={EveryTicks}"
            };
            lines.AddRange(failures.OrderByDescending(f => f.Value).Select(f => $"threw {f.Key}: {f.Value}"));
            return string.Join("\n", lines);
        }
    }
}
