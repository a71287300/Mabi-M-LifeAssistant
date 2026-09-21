namespace MabiLifeAssistant.Tests;

public sealed class WorkStateRoundTrackerTests
{
    [Fact]
    public void Requires_three_working_votes_before_reporting_working()
    {
        var tracker = new MabiLifeAssistant.WorkStateRoundTracker();

        tracker.Observe(MabiLifeAssistant.WorkState.Working);
        tracker.Observe(MabiLifeAssistant.WorkState.Unknown);
        Assert.False(tracker.WorkIndicatorActive);
        Assert.True(tracker.WorkStateUnknown);

        tracker.Observe(MabiLifeAssistant.WorkState.Working);
        tracker.Observe(MabiLifeAssistant.WorkState.Working);

        Assert.True(tracker.WorkIndicatorActive);
        Assert.False(tracker.WorkStateUnknown);
    }

    [Fact]
    public void Round_does_not_restart_until_work_is_seen_and_two_idle_frames_follow()
    {
        var tracker = new MabiLifeAssistant.WorkStateRoundTracker();
        tracker.BeginRound();

        tracker.Observe(MabiLifeAssistant.WorkState.Idle);
        tracker.Observe(MabiLifeAssistant.WorkState.Idle);
        Assert.True(tracker.WaitingForRoundActivity);
        Assert.False(tracker.RoundWorkSeen);

        tracker.Observe(MabiLifeAssistant.WorkState.Working);
        Assert.True(tracker.RoundWorkSeen);
        Assert.True(tracker.WaitingForRoundActivity);

        tracker.Observe(MabiLifeAssistant.WorkState.Idle);
        Assert.True(tracker.WaitingForRoundActivity);
        var released = tracker.Observe(MabiLifeAssistant.WorkState.Idle);

        Assert.True(released);
        Assert.False(tracker.WaitingForRoundActivity);
    }

    [Fact]
    public void Unknown_frames_keep_the_assistant_from_starting()
    {
        var tracker = new MabiLifeAssistant.WorkStateRoundTracker();

        tracker.Observe(MabiLifeAssistant.WorkState.Unknown);
        tracker.Observe(MabiLifeAssistant.WorkState.Unknown);
        tracker.Observe(MabiLifeAssistant.WorkState.Unknown);

        Assert.True(tracker.WorkStateUnknown);
        Assert.False(tracker.WorkIndicatorActive);
    }
}
