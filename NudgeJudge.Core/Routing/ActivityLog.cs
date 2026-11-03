using System.Text.Json.Serialization;
using NudgeJudge.Core.Config;

namespace NudgeJudge.Core.Routing;

[JsonConverter(typeof(JsonStringEnumConverter<ActivityOutcome>))]
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
    string? Detail = null,
    Guid? BrowserId = null);

/// <summary>
/// Thread-safe log of what happened to each link (newest last), capped at <see cref="Capacity"/> entries.
/// With a <see cref="IActivityStore"/> it's persistent: every change goes to the store first and the
/// in-memory list becomes whatever the store holds, so entries written by other processes show up too.
/// </summary>
public sealed class ActivityLog
{
    public const int DefaultCapacity = 100;
    public const int MinCapacity = 1;
    public const int MaxCapacity = 10_000;

    private readonly IActivityStore? _store;
    private readonly Lock _gate = new();
    private List<ActivityEntry> _entries = [];

    public ActivityLog(IActivityStore? store = null, int capacity = DefaultCapacity, bool enabled = true)
    {
        _store = store;
        Capacity = ClampCapacity(capacity);
        IsEnabled = enabled;
        if (_store is not null) _entries = Trimmed(TryStore(s => s.Read()) ?? []);
    }

    /// <summary>A log that follows the Activity settings in <paramref name="config"/>, now and whenever they change.</summary>
    public static ActivityLog FollowingSettings(IConfigStore config, IActivityStore? store)
    {
        var s = config.Current.Settings;
        var log = new ActivityLog(store, s.ActivityLogLimit, s.ActivityLogEnabled);
        config.Changed += (_, _) => log.Configure(config.Current.Settings.ActivityLogEnabled, config.Current.Settings.ActivityLogLimit);
        return log;
    }

    public int Capacity { get; private set; }

    /// <summary>When false, new links aren't recorded. What's already logged stays until cleared.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Set when the log file couldn't be read or written; the log then keeps working in memory.</summary>
    public string? StoreError { get; private set; }

    /// <summary>One entry was appended (and the oldest dropped if the log was full).</summary>
    public event EventHandler<ActivityEntry>? Added;

    /// <summary>The contents changed in some other way; read <see cref="Snapshot"/> again.</summary>
    public event EventHandler? Reset;

    public IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (_gate) return [.. _entries];
    }

    public void Add(ActivityEntry entry)
    {
        bool appendedOnly;
        lock (_gate)
        {
            if (!IsEnabled) return;
            var expected = Trimmed([.. _entries, entry]);
            var stored = TryStore(s => s.Update(list =>
            {
                list.Add(entry);
                Trim(list);
            }));
            _entries = stored is null ? expected : [.. stored];
            // Another process may have written in the meantime; then the view needs a full reload.
            appendedOnly = _entries.SequenceEqual(expected);
        }
        if (appendedOnly) Added?.Invoke(this, entry);
        else Reset?.Invoke(this, EventArgs.Empty);
    }

    public void Clear() => Replace([]);

    /// <summary>Replaces everything (undoing a clear). Entries are oldest first; only the newest <see cref="Capacity"/> are kept.</summary>
    public void Replace(IEnumerable<ActivityEntry> entries)
    {
        lock (_gate)
        {
            var next = Trimmed([.. entries]);
            TryStore(s => s.Update(list =>
            {
                list.Clear();
                list.AddRange(next);
            }));
            _entries = next;
        }
        Reset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the user's settings. A lower capacity drops the oldest entries straight away.</summary>
    public void Configure(bool enabled, int capacity)
    {
        capacity = ClampCapacity(capacity);
        bool trimmed;
        lock (_gate)
        {
            IsEnabled = enabled;
            if (capacity == Capacity) return;
            Capacity = capacity;
            trimmed = _entries.Count > capacity;
            if (trimmed)
            {
                _entries = Trimmed(_entries);
                TryStore(s => s.Update(Trim));
            }
        }
        if (trimmed) Reset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Picks up entries other processes wrote to the store. Raises <see cref="Reset"/> if anything changed.</summary>
    public void Reload()
    {
        if (_store is null) return;
        lock (_gate)
        {
            if (TryStore(s => s.Read()) is not { } stored) return;
            var next = Trimmed([.. stored]);
            if (next.SequenceEqual(_entries)) return;
            _entries = next;
        }
        Reset?.Invoke(this, EventArgs.Empty);
    }

    public static int ClampCapacity(int capacity) => Math.Clamp(capacity, MinCapacity, MaxCapacity);

    private List<ActivityEntry> Trimmed(IReadOnlyList<ActivityEntry> entries) =>
        entries.Count <= Capacity ? [.. entries] : [.. entries.Skip(entries.Count - Capacity)];

    private void Trim(List<ActivityEntry> list)
    {
        if (list.Count > Capacity) list.RemoveRange(0, list.Count - Capacity);
    }

    private IReadOnlyList<ActivityEntry>? TryStore(Func<IActivityStore, IReadOnlyList<ActivityEntry>> action)
    {
        if (_store is null) return null;
        try
        {
            var result = action(_store);
            StoreError = null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never let a disk problem stop a link from opening.
            StoreError = "Couldn't save the activity log: " + ex.Message;
            return null;
        }
    }
}
