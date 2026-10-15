using NixManager.Dashboard;

namespace NixManager.Tests;

/// <summary>
/// <c>ui --browser</c> ends with its last tab, as the Bun version's <c>ui</c> did (<c>main</c> in <c>src/main.ts</c>): a
/// second after the last tab closed, so a reload keeps it, after three minutes without any tab, and never while an
/// operation runs. <c>checks.daemon-runic</c> runs the same with real processes and connections.
/// </summary>
public sealed class BrowserLifetimeTests
{
    private static readonly DateTimeOffset Opened = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => Opened.AddSeconds(seconds);

    [Test]
    public async Task It_ends_a_second_after_the_last_tab_closed()
    {
        var lifetime = new BrowserLifetime(Opened);
        lifetime.Connected(At(2));
        await Assert.That(lifetime.Ended(At(30), busy: false)).IsFalse();
        lifetime.Disconnected(At(40));
        await Assert.That(lifetime.Ended(At(40.5), busy: false)).IsFalse();
        await Assert.That(lifetime.Ended(At(41), busy: false)).IsTrue();
    }

    [Test]
    public async Task A_reload_within_the_grace_period_keeps_it()
    {
        var lifetime = new BrowserLifetime(Opened);
        lifetime.Connected(At(2));
        lifetime.Disconnected(At(10));
        lifetime.Connected(At(10.4));
        await Assert.That(lifetime.Ended(At(60), busy: false)).IsFalse();
        // The reloaded tab is closed later.
        lifetime.Disconnected(At(70));
        await Assert.That(lifetime.Ended(At(71), busy: false)).IsTrue();
    }

    [Test]
    public async Task It_waits_for_the_last_of_several_tabs()
    {
        var lifetime = new BrowserLifetime(Opened);
        lifetime.Connected(At(1));
        lifetime.Connected(At(2));
        lifetime.Disconnected(At(3));
        await Assert.That(lifetime.Ended(At(10), busy: false)).IsFalse();
        lifetime.Disconnected(At(11));
        await Assert.That(lifetime.Ended(At(12), busy: false)).IsTrue();
    }

    [Test]
    public async Task A_tab_whose_disconnection_arrives_first_still_counts_as_gone()
    {
        var lifetime = new BrowserLifetime(Opened);
        lifetime.Disconnected(At(5));
        lifetime.Connected(At(5));
        await Assert.That(lifetime.Ended(At(6), busy: false)).IsTrue();
    }

    [Test]
    public async Task A_running_operation_finishes_first()
    {
        var lifetime = new BrowserLifetime(Opened);
        lifetime.Connected(At(1));
        lifetime.Disconnected(At(2));
        await Assert.That(lifetime.Ended(At(100), busy: true)).IsFalse();
        await Assert.That(lifetime.Ended(At(100), busy: false)).IsTrue();
    }

    [Test]
    public async Task Without_any_tab_it_ends_after_three_minutes()
    {
        var lifetime = new BrowserLifetime(Opened);
        await Assert.That(lifetime.Ended(At(179), busy: false)).IsFalse();
        await Assert.That(lifetime.Ended(At(181), busy: true)).IsFalse();
        await Assert.That(lifetime.Ended(At(181), busy: false)).IsTrue();
        // A tab that connected keeps it, however late.
        var late = new BrowserLifetime(Opened);
        late.Connected(At(170));
        await Assert.That(late.Ended(At(400), busy: false)).IsFalse();
    }
}
