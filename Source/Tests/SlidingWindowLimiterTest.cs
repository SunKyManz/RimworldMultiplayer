using Multiplayer.Common.Util;

namespace Tests;

[TestFixture]
public class SlidingWindowLimiterTest
{
    [Test]
    public void AllowsUpToMax_ThenRejectsInsideWindow()
    {
        var limiter = new SlidingWindowLimiter(3, 600);

        Assert.That(limiter.TryAcquire(0), Is.True);
        Assert.That(limiter.TryAcquire(10), Is.True);
        Assert.That(limiter.TryAcquire(20), Is.True);
        Assert.That(limiter.TryAcquire(30), Is.False);
    }

    [Test]
    public void AllowsAgainOnceOldestEventExpires()
    {
        var limiter = new SlidingWindowLimiter(2, 100);
        limiter.TryAcquire(0);
        limiter.TryAcquire(50);

        Assert.That(limiter.TryAcquire(99), Is.False);
        Assert.That(limiter.TryAcquire(100), Is.True, "the event at 0 is exactly one window old");
        Assert.That(limiter.TryAcquire(120), Is.False, "events at 50 and 100 still count");
        Assert.That(limiter.TryAcquire(150), Is.True);
    }

    [Test]
    public void RejectedAttemptsDoNotCountTowardTheLimit()
    {
        var limiter = new SlidingWindowLimiter(1, 100);
        limiter.TryAcquire(0);

        Assert.That(limiter.TryAcquire(60), Is.False);
        Assert.That(limiter.TryAcquire(100), Is.True, "the rejected attempt at 60 must not extend the block");
    }
}
