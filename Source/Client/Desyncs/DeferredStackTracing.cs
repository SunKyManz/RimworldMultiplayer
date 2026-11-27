using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Patches;
using Multiplayer.Common;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Desyncs
{
    [EarlyPatch]
    [HarmonyPatch]
    public static class DeferredStackTracing
    {
        public static int ignoreTraces;
        public static long maxTraceDepth;
        public static int randCalls;

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Value));
            yield return AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Int));
        }

        public static int acc;

        private static bool loggedTraceFailure;

        public static void Postfix()
        {
            // Runs inside Rand and other game code: an exception here would abort that game code on this client
            // only and desync it, so never let one escape (ported from #996's tracer).
            try
            {
                if (!ShouldAddStackTraceForDesyncLog()) return;

                if (!DesyncTraceCapture.TryCapture(Multiplayer.settings.desyncTracingMode, out var trace)) return;

                var logItem = StackTraceLogItemRaw.GetFromPool();
                logItem.managedTrace = trace.Text;
                Multiplayer.game.sync.TryAddStackTraceForDesyncLogRaw(logItem, trace.Depth, trace.Hash);
                acc++;
            }
            catch (Exception e)
            {
                if (loggedTraceFailure) return;
                loggedTraceFailure = true;
                Log.Error($"Multiplayer: desync stack tracing failed (further failures aren't logged): {e}");
            }
        }

        public static bool ShouldAddStackTraceForDesyncLog()
        {
            if (Multiplayer.Client == null) return false;
            if (Multiplayer.settings.desyncTracingMode == DesyncTracingMode.None) return false;
            if (Multiplayer.game == null) return false;

            if (!Multiplayer.game.gameComp.logDesyncTraces) return false;

            if (Rand.stateStack.Count > 1) return false;
            if (Multiplayer.IsReplay) return false;

            if (!Multiplayer.Ticking && !Multiplayer.ExecutingCmds) return false;

            // Worker thread timing isn't deterministic
            if (!UnityData.IsInMainThread) return false;

            return ignoreTraces == 0;
        }
    }

    [HarmonyPatch(typeof(UniqueIDsManager), nameof(UniqueIDsManager.GetNextID))]
    public static class UniqueIdsPatch
    {
        static void Postfix()
        {
            DeferredStackTracing.Postfix();
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    public static class ThingSpawnPatch
    {
        static void Postfix(Thing __instance)
        {
            if (__instance.def.HasThingIDNumber)
                DeferredStackTracing.Postfix();
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
    public static class ThingDeSpawnPatch
    {
        static void Postfix(Thing __instance)
        {
            if (__instance.def.HasThingIDNumber)
                DeferredStackTracing.Postfix();
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class EndCurrentJobPatch
    {
        static void Prefix(Pawn_JobTracker __instance)
        {
            if (MpVersion.IsDebug && __instance.curJob != null && DeferredStackTracing.ShouldAddStackTraceForDesyncLog()
                && Multiplayer.settings.desyncTracingMode == DesyncTracingMode.Slow)
                Multiplayer.game.sync.TryAddInfoForDesyncLog($"EndCurrentJob for {__instance.pawn}: {__instance.curJob}", "");
        }
    }

    [HarmonyPatch(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.WildAnimalSpawnerTick))]
    static class WildAnimalSpawnerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.WildPlantSpawnerTick))]
    static class WildPlantSpawnerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.SteadyEnvironmentEffectsTick))]
    static class SteadyEnvironmentEffectsTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(TempTerrainManager), nameof(TempTerrainManager.Tick))]
    static class TempTerrainManagerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    /*[HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellForWorker))]
    static class FindBestStorageCellTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }*/

    [HarmonyPatch(typeof(IntermittentSteamSprayer), nameof(IntermittentSteamSprayer.SteamSprayerTick))]
    static class SteamSprayerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }
}
