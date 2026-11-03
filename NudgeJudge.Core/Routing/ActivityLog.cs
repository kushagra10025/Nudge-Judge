namespace NudgeJudge.Core.Routing;

public enum ActivityOutcome
{
    /// <summary>A rule matched and the browser was launched.</summary>
    Routed,
    /// <summary>No rule matched; the chooser was shown.</summary>
    Asked,
    /// <summary>The user picked a browser in the chooser.</summary>
    Chosen,
    /// <summary>Routing was off; sent to the primary browser.</summary>
    PassedThrough,
    /// <summary>The URL failed validation.</summary>
    Rejected,
    /// <summary>The browser couldn't be started.</summary>
    Failed,
}

public sealed record ActivityEntry(
    DateTimeOffset Timestamp,
    string Url,
    ActivityOutcome Outcome,
    string? BrowserName = null,
    int? RuleNumber = null,
    string? RulePattern = null,
    string? Detail = null);

/// <summary>Bounded, thread-safe, in-memory log of what happened to each link (newest last).</summary>
public sealed class ActivityLog(int capacity = 500)
{
    private readonly LinkedList<ActivityEntry> _entries = new();
    private readonly Lock _gate = new();

    public event EventHandler<ActivityEntry>? Added;
    public event EventHandler? Cleared;

    public IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (_gate) return [.. _entries];
    }

    public void Add(ActivityEntry entry)
    {
        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > capacity) _entries.RemoveFirst();
        }
        Added?.Invoke(this, entry);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }
}
