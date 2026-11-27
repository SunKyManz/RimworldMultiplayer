using Multiplayer.Common.Util;
using UnityEngine;

namespace Multiplayer.Client.Desyncs;

/// <summary>
/// Decides whether a desync may be resynced without the player clicking. Limited so that a desync that
/// recurs right after every resync (e.g. from a non-deterministic mod) falls back to the manual window.
/// </summary>
public class AutoResyncPolicy
{
    public const int MaxResyncs = 3;
    public const double WindowSeconds = 10 * 60;

    private readonly SlidingWindowLimiter limiter = new(MaxResyncs, WindowSeconds);

    public bool TryClaim() =>
        Multiplayer.settings.autoResyncOnDesync && limiter.TryAcquire(Time.realtimeSinceStartup);
}
