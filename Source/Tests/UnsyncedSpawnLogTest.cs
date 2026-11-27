using Multiplayer.Client.Desyncs;

namespace Tests;

[TestFixture]
public class UnsyncedSpawnLogTest
{
    [Test]
    public void EmptyLogSaysSo()
    {
        var log = new UnsyncedSpawnLog();

        Assert.That(log.Count, Is.Zero);
        Assert.That(log.Summary(), Is.EqualTo("none"));
        Assert.That(log.Report(), Does.Contain("No things spawned"));
    }

    [Test]
    public void KeepsFirstOccurrencePerDef()
    {
        var log = new UnsyncedSpawnLog();
        log.Record("Steel", 10, 100, "stack A");
        log.Record("Steel", 11, 200, "stack B");
        log.Record("Wood", 12, 300, "stack C");

        Assert.That(log.Count, Is.EqualTo(2));
        Assert.That(log.Summary(), Is.EqualTo("2 (first: Steel)"));

        var report = log.Report();
        Assert.That(report, Does.Contain("Steel (id 10) at tick 100").And.Contain("stack A"));
        Assert.That(report, Does.Not.Contain("stack B"));
        Assert.That(report, Does.Contain("Wood (id 12) at tick 300").And.Contain("stack C"));
    }

    [Test]
    public void CapsEntriesButKeepsCounting()
    {
        var log = new UnsyncedSpawnLog();
        for (int i = 0; i < UnsyncedSpawnLog.MaxEntries + 5; i++)
            log.Record($"Def{i}", i, i, $"stack {i}");

        Assert.That(log.Count, Is.EqualTo(UnsyncedSpawnLog.MaxEntries + 5));
        var report = log.Report();
        Assert.That(report, Does.Contain($"Showing the first {UnsyncedSpawnLog.MaxEntries} of {UnsyncedSpawnLog.MaxEntries + 5} defs."));
        Assert.That(report, Does.Not.Contain($"Def{UnsyncedSpawnLog.MaxEntries} "));
    }

    [Test]
    public void ClearForgetsEverything()
    {
        var log = new UnsyncedSpawnLog();
        log.Record("Steel", 10, 100, "stack A");
        log.Clear();
        log.Record("Steel", 20, 500, "stack B");

        Assert.That(log.Summary(), Is.EqualTo("1 (first: Steel)"));
        Assert.That(log.Report(), Does.Contain("stack B").And.Not.Contain("stack A"));
    }
}
