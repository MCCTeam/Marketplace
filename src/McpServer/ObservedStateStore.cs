namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>One chat/system line seen by the client.</summary>
/// <param name="Id">Monotonic id within this session, for <c>afterId</c> polling.</param>
/// <param name="TimestampUtc">When the line was observed.</param>
/// <param name="Kind">chat, private, or system.</param>
/// <param name="Text">The full rendered line.</param>
/// <param name="Sender">The sender name when one could be extracted, else null.</param>
/// <param name="Message">The body with sender/prefix stripped when possible, else null.</param>
public sealed record McpChatEntry(
    long Id, DateTimeOffset TimestampUtc, string Kind, string Text, string? Sender, string? Message);

/// <summary>One high-signal runtime event (death, respawn, container open/close, title, ...).</summary>
/// <param name="Id">Monotonic id within this session, for <c>afterId</c> polling.</param>
/// <param name="TimestampUtc">When the event was observed.</param>
/// <param name="Type">The event type, e.g. <c>death</c>, <c>inventory_open</c>.</param>
/// <param name="Data">Structured event detail, or null.</param>
public sealed record McpEventEntry(
    long Id, DateTimeOffset TimestampUtc, string Type, object? Data);

/// <summary>
/// The plugin-owned observed-state rings that back <c>mcc_chat_history</c> and <c>mcc_recent_events</c>: the MCP memory that polling agents read instead of a push subscription (the server exposes no resources or notifications).
/// <para>
/// Append-only under a lock with immutable snapshots on read: session-loop ingest never blocks tool threads, and <see cref="Clear"/> swaps the buffers so a reader mid-enumeration keeps a consistent view.
/// Both rings cap at 500 entries; ids restart at 1 per session and the store is cleared on session end so cursors never bleed across servers.
/// </para>
/// </summary>
public sealed class ObservedStateStore
{
    /// <summary>Maximum entries per ring.</summary>
    public const int MaxEntries = 500;

    private readonly object _gate = new();
    private List<McpChatEntry> _chat = [];
    private List<McpEventEntry> _events = [];
    private long _nextId = 1;
    private long _worldAge;
    private long _timeOfDay;
    private bool _hasTime;

    /// <summary>Appends one chat line.</summary>
    public void AddChat(string kind, string text, string? sender, string? message)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            _chat.Add(new McpChatEntry(_nextId++, DateTimeOffset.UtcNow, kind, text, sender, message));
            if (_chat.Count > MaxEntries)
                _chat.RemoveRange(0, _chat.Count - MaxEntries);
        }
    }

    /// <summary>Appends one runtime event.</summary>
    public void AddEvent(string type, object? data = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (_gate)
        {
            _events.Add(new McpEventEntry(_nextId++, DateTimeOffset.UtcNow, type, data));
            if (_events.Count > MaxEntries)
                _events.RemoveRange(0, _events.Count - MaxEntries);
        }
    }

    /// <summary>Records the latest world time. Stored separately, never in the event ring (a per-tick append would evict real events).</summary>
    public void SetTime(long worldAge, long timeOfDay)
    {
        lock (_gate)
        {
            _worldAge = worldAge;
            _timeOfDay = timeOfDay;
            _hasTime = true;
        }
    }

    /// <summary>Reads recent chat lines, newest last, up to <paramref name="maxCount"/>.</summary>
    public (IReadOnlyList<McpChatEntry> Entries, long LatestId) GetChat(int maxCount)
    {
        int take = Math.Clamp(maxCount, 1, MaxEntries);
        lock (_gate)
        {
            int start = Math.Max(0, _chat.Count - take);
            var slice = _chat.GetRange(start, _chat.Count - start);
            long latest = _chat.Count > 0 ? _chat[^1].Id : 0;
            return (slice, latest);
        }
    }

    /// <summary>Reads events after <paramref name="afterId"/>, newest last, with optional substring type filter.</summary>
    public (IReadOnlyList<McpEventEntry> Entries, long LatestId) GetEvents(long afterId, int maxCount, string? typeFilter)
    {
        int take = Math.Clamp(maxCount, 1, MaxEntries);
        lock (_gate)
        {
            var matches = new List<McpEventEntry>();
            foreach (McpEventEntry entry in _events)
            {
                if (entry.Id <= afterId)
                    continue;

                if (!string.IsNullOrEmpty(typeFilter)
                    && !entry.Type.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                matches.Add(entry);
                if (matches.Count >= take)
                    break;
            }

            long latest = _events.Count > 0 ? _events[^1].Id : 0;
            return (matches, latest);
        }
    }

    /// <summary>Reads the last observed world time, or null when no time update arrived yet.</summary>
    public (long WorldAge, long TimeOfDay)? GetTime()
    {
        lock (_gate)
            return _hasTime ? (_worldAge, _timeOfDay) : null;
    }

    /// <summary>Clears both rings and restarts ids. Called on session end.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _chat = [];
            _events = [];
            _nextId = 1;
            _hasTime = false;
        }
    }
}
