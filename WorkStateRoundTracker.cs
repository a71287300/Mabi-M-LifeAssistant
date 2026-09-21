namespace MabiLifeAssistant;

/// <summary>
/// Debounces visual predictions and guards the transition between collection rounds.
/// This state machine is independent from WPF and can be verified without a live game window.
/// </summary>
internal sealed class WorkStateRoundTracker
{
    private const int VoteWindowSize = 5;
    private const int RequiredStateVotes = 3;
    private const int RequiredIdleAfterWorkVotes = 2;
    private readonly Queue<WorkState> _history = new();

    public bool WaitingForRoundActivity { get; private set; }
    public bool RoundWorkSeen { get; private set; }
    public int IdleAfterWorkVotes { get; private set; }
    public bool WorkIndicatorActive { get; private set; }
    public bool WorkStateUnknown { get; private set; } = true;

    public bool Observe(WorkState observedState)
    {
        _history.Enqueue(observedState);
        while (_history.Count > VoteWindowSize)
            _history.Dequeue();

        WorkIndicatorActive = _history.Count(state => state == WorkState.Working) >= RequiredStateVotes;
        var idleVotes = _history.Count(state => state == WorkState.Idle);
        WorkStateUnknown = !WorkIndicatorActive && idleVotes < RequiredStateVotes;

        if (WaitingForRoundActivity && observedState == WorkState.Working)
            RoundWorkSeen = true;

        if (WaitingForRoundActivity && RoundWorkSeen)
        {
            IdleAfterWorkVotes = observedState == WorkState.Idle
                ? Math.Min(RequiredIdleAfterWorkVotes, IdleAfterWorkVotes + 1)
                : 0;
        }
        else
        {
            IdleAfterWorkVotes = 0;
        }

        if (WaitingForRoundActivity && RoundWorkSeen
            && observedState == WorkState.Idle
            && IdleAfterWorkVotes >= RequiredIdleAfterWorkVotes
            && !WorkIndicatorActive)
        {
            WaitingForRoundActivity = false;
            IdleAfterWorkVotes = 0;
            return true;
        }

        return false;
    }

    public void BeginRound()
    {
        WaitingForRoundActivity = true;
        RoundWorkSeen = false;
        IdleAfterWorkVotes = 0;
        WorkIndicatorActive = false;
        WorkStateUnknown = true;
        _history.Clear();
    }
}
