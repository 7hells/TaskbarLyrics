namespace TaskbarLyrics.App;

// Tracks whether an SMTC session is currently present and reports only the
// appearance/disappearance edges. The first observation is also treated as an
// edge so callers can drive an initial state without a separate query.
// Kept pure so the edge semantics are unit-testable without a live SMTC manager.
// Updates arrive from both the snapshot-refresh path (lyrics thread) and the
// SMTC SessionsChanged event (thread pool), so state access is synchronized.
internal sealed class SessionPresenceTracker
{
    private readonly object _sync = new();
    private bool? _lastPresence;

    public bool HasActiveSession
    {
        get
        {
            lock (_sync)
            {
                return _lastPresence == true;
            }
        }
    }

    public bool Update(bool hasSession)
    {
        lock (_sync)
        {
            if (_lastPresence == hasSession)
            {
                return false;
            }

            _lastPresence = hasSession;
            return true;
        }
    }
}
