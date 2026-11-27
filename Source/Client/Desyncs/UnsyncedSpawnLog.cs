using System.Collections.Generic;
using System.Text;

namespace Multiplayer.Client.Desyncs;

/// <summary>
/// Things spawned outside the synced simulation this session (see UnsyncedSpawnTripwire), kept for desync reports.
/// One entry per def, the first occurrence. No game types, so it can be unit tested.
/// </summary>
public class UnsyncedSpawnLog
{
    public const int MaxEntries = 20;

    private readonly HashSet<string> seenDefs = new();
    private readonly List<string> entries = new();

    /// Every def seen, also past <see cref="MaxEntries"/>
    public int Count => seenDefs.Count;
    public string FirstDef { get; private set; }

    public void Record(string defName, int thingId, int tick, string stack)
    {
        if (!seenDefs.Add(defName)) return;
        FirstDef ??= defName;
        if (entries.Count < MaxEntries)
            entries.Add($"{defName} (id {thingId}) at tick {tick}\n{stack}");
    }

    /// The world from the server replaces what these spawns did (joining, rejoining after a desync)
    public void Clear()
    {
        seenDefs.Clear();
        entries.Clear();
        FirstDef = null;
    }

    public string Summary() => Count == 0 ? "none" : $"{Count} (first: {FirstDef})";

    public string Report()
    {
        if (Count == 0) return "No things spawned outside the synced simulation this session.";

        var text = new StringBuilder()
            .AppendLine("Things spawned outside the synced simulation, first occurrence per def. Each usually means")
            .AppendLine("an action (often from a mod) ran on this client only. The stack names the code that did it.");
        if (Count > entries.Count)
            text.AppendLine($"Showing the first {entries.Count} of {Count} defs.");

        foreach (var entry in entries)
            text.AppendLine().AppendLine(entry);
        return text.ToString();
    }
}
