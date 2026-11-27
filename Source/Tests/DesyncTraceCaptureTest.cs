using System.Runtime.CompilerServices;
using Multiplayer.Client;
using Multiplayer.Client.Desyncs;

namespace Tests;

public class DesyncTraceCaptureTest
{
    [TestCase(DesyncTracingMode.None)]
    [TestCase((DesyncTracingMode)99)]
    public void DisabledOrUnknownModeDoesNotCollect(DesyncTracingMode mode)
    {
        Assert.That(DesyncTraceCapture.TryCapture(mode, out var trace), Is.False);
        Assert.That(trace, Is.EqualTo(default(DesyncTrace)));
    }

    [Test]
    public void FastReturnsContextMarkerWithoutAllocatingManagedStacks()
    {
        for (int i = 0; i < 100; i++)
            DesyncTraceCapture.TryCapture(DesyncTracingMode.Fast, out _);

        // The runtime itself can allocate on this thread while it recompiles hot methods (tiered compilation),
        // and background work left by other tests can delay that into this loop. Fast is allocation-free if any
        // attempt allocates nothing.
        long allocated = long.MaxValue;
        for (int attempt = 0; attempt < 5 && allocated != 0; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
                DesyncTraceCapture.TryCapture(DesyncTracingMode.Fast, out _);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.That(allocated, Is.Zero, "Fast must not allocate stack traces or format event strings");
        Assert.That(DesyncTraceCapture.TryCapture(DesyncTracingMode.Fast, out var trace), Is.True);
        Assert.That(trace.Text, Is.SameAs(DesyncTraceCapture.FastDescription));
        Assert.That(trace.Depth, Is.Zero);
        Assert.That(trace.Hash, Is.Zero);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DesyncTrace CaptureFromDiagnosticCaller()
    {
        DesyncTraceCapture.TryCapture(DesyncTracingMode.Slow, out var trace);
        return trace;
    }

    [Test]
    public void SlowCapturesDetailedManagedCallerFrames()
    {
        var trace = CaptureFromDiagnosticCaller();
        Assert.That(trace.Text, Does.Contain(nameof(CaptureFromDiagnosticCaller)));
        Assert.That(trace.Depth, Is.InRange(1, DesyncTraceCapture.MaxDepth));
        Assert.That(trace.Text, Is.Not.EqualTo(DesyncTraceCapture.FastDescription));
    }

    [Test]
    public void SwitchingModesDoesNotReuseSlowStackData()
    {
        DesyncTraceCapture.TryCapture(DesyncTracingMode.Slow, out var trace);
        Assert.That(trace.Depth, Is.Positive);
        DesyncTraceCapture.TryCapture(DesyncTracingMode.Fast, out trace);
        Assert.That(trace.Text, Is.EqualTo(DesyncTraceCapture.FastDescription));
        DesyncTraceCapture.TryCapture(DesyncTracingMode.None, out trace);
        Assert.That(trace, Is.EqualTo(default(DesyncTrace)));
    }
}
