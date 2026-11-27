using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// One line summarizing the simulation state, written by the <c>-replay</c> command-line mode so two runs
    /// of the same replay can be compared line by line (see Tools/Check-Determinism.ps1).
    /// Format: <c>tick=T world=ticks/rand mapID=ticks/rand/stateHash ...</c>
    /// </summary>
    public static class DeterminismFingerprint
    {
        public static string Capture()
        {
            var line = new StringBuilder();
            var world = Multiplayer.AsyncWorldTime;
            line.Append($"tick={TickPatch.Timer} world={world.worldTicks}/{world.randState}");

            foreach (var map in Find.Maps.OrderBy(m => m.uniqueID))
            {
                var time = map.AsyncTime();
                line.Append($" map{map.uniqueID}={time.mapTicks}/{time.randState}/{StateHash(map):X8}");
            }

            return line.ToString();
        }

        /// <summary>
        /// Everything <see cref="StateHash"/> covers, one thing per line in the same order, so two runs that
        /// fingerprint differently can be diffed to find the thing that differs (-replaydump).
        /// </summary>
        public static string Dump()
        {
            var text = new StringBuilder();

            foreach (var map in Find.Maps.OrderBy(m => m.uniqueID))
            {
                text.AppendLine($"map{map.uniqueID}");
                foreach (var thing in ComparableThings(map))
                {
                    text.Append($"{thing.thingIDNumber} {thing.def.defName} {thing.Position.x},{thing.Position.z} hp={thing.HitPoints} stack={thing.stackCount}");
                    if (thing is Pawn { jobs.curJob: { } job })
                        text.Append($" job={job.def.defName} {job.targetA} {job.targetB}");
                    text.AppendLine();
                }
            }

            return text.ToString();
        }

        // Motes are visual, and real-time ones (e.g. Mote_Text) expire after wall-clock seconds, so they leave the
        // things list at a different tick on every machine. Removal swaps the list's last thing into the gap, so
        // the list's order isn't comparable either: hash the things in id order instead. A real effect of a
        // different order still shows up, in the random state, once it changes what the game does.
        private static IEnumerable<Thing> ComparableThings(Map map) =>
            map.listerThings.AllThings.Where(t => t.def.category != ThingCategory.Mote).OrderBy(t => t.thingIDNumber);

        // Catches divergence that hasn't reached the random state yet.
        // Uses only ids, defs' shortHash and ints, never object hashes.
        private static int StateHash(Map map)
        {
            int hash = 0;

            foreach (var thing in ComparableThings(map))
            {
                hash = Gen.HashCombineInt(hash, thing.thingIDNumber);
                hash = Gen.HashCombineInt(hash, thing.Position.x);
                hash = Gen.HashCombineInt(hash, thing.Position.z);
                hash = Gen.HashCombineInt(hash, thing.HitPoints);
                hash = Gen.HashCombineInt(hash, thing.stackCount);

                if (thing is Pawn { jobs.curJob: { } job })
                    hash = Gen.HashCombineInt(hash, job.def.shortHash);
            }

            return hash;
        }
    }
}
