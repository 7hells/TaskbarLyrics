using Xunit;

namespace TaskbarLyrics.App.Tests;

public sealed class SessionPresenceTrackerTests
{
    [Fact]
    public void FirstObservationOfPresenceIsAnEdge()
    {
        var tracker = new SessionPresenceTracker();

        Assert.True(tracker.Update(true));
        Assert.True(tracker.HasActiveSession);
    }

    [Fact]
    public void FirstObservationOfAbsenceIsAnEdge()
    {
        var tracker = new SessionPresenceTracker();

        Assert.True(tracker.Update(false));
        Assert.False(tracker.HasActiveSession);
    }

    [Fact]
    public void RepeatedPresenceDoesNotRaiseAnEdge()
    {
        var tracker = new SessionPresenceTracker();
        tracker.Update(true);

        Assert.False(tracker.Update(true));
    }

    [Fact]
    public void RepeatedAbsenceDoesNotRaiseAnEdge()
    {
        var tracker = new SessionPresenceTracker();
        tracker.Update(false);

        Assert.False(tracker.Update(false));
    }

    [Fact]
    public void AppearanceAndDisappearanceEachRaiseAnEdge()
    {
        var tracker = new SessionPresenceTracker();

        Assert.True(tracker.Update(true));
        Assert.False(tracker.Update(true));
        Assert.True(tracker.Update(false));
        Assert.False(tracker.Update(false));
        Assert.True(tracker.Update(true));
        Assert.True(tracker.HasActiveSession);
    }
}
