using System.Collections.Generic;

namespace Multiplayer.Common.Util;

/// <summary>
/// Allows at most <c>maxEvents</c> events within any span of <c>windowSeconds</c>.
/// The caller supplies the clock, which keeps this free of engine dependencies and easy to test.
/// </summary>
public class SlidingWindowLimiter(int maxEvents, double windowSeconds)
{
    private readonly Queue<double> events = new();

    /// <summary>Records an event at <paramref name="now"/> and returns true if it is within the limit.</summary>
    public bool TryAcquire(double now)
    {
        while (events.Count > 0 && now - events.Peek() >= windowSeconds)
            events.Dequeue();

        if (events.Count >= maxEvents)
            return false;

        events.Enqueue(now);
        return true;
    }
}
