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

        // Catches divergence that hasn't reached the random state yet. Order-dependent on purpose: the order of
        // the things list is itself simulation state. Uses only ids, defs' shortHash and ints, never object hashes.
        private static int StateHash(Map map)
        {
            int hash = 0;

            foreach (var thing in map.listerThings.AllThings)
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
