using System.Globalization;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>
/// Reserve-and-commit token ledger over a UTC-day window.
/// Hints never bypass it: every call reserves first and fails fast when the day is spent.
/// Pure enough to unit test.
/// </summary>
public sealed class LlmBudgetLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DayUse> _use = new(StringComparer.OrdinalIgnoreCase);
    private long _globalPerDay;
    private Dictionary<string, long> _perPlugin = new(StringComparer.OrdinalIgnoreCase);

    public void Configure(long globalPerDay, IReadOnlyDictionary<string, long>? perPlugin)
    {
        lock (_gate)
        {
            _globalPerDay = Math.Max(0, globalPerDay);
            _perPlugin = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (perPlugin is not null)
            {
                foreach (var pair in perPlugin)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
                        _perPlugin[pair.Key.Trim()] = pair.Value;
                }
            }
        }
    }

    public bool TryReserve(string owner, long estimate, out long reservation)
    {
        reservation = 0;
        if (estimate < 0)
            estimate = 0;

        lock (_gate)
        {
            DayUse shared = ForLocked("*");
            DayUse own = ForLocked(owner);
            long pluginLimit = _perPlugin.TryGetValue(owner, out long limit) ? limit : 0;
            if ((_globalPerDay > 0 && shared.Spent + estimate > _globalPerDay)
                || (pluginLimit > 0 && own.Spent + estimate > pluginLimit))
                return false;

            shared.Reserved += estimate;
            own.Reserved += estimate;
            reservation = estimate;
            return true;
        }
    }

    public void Commit(string owner, long reservation, long actual)
    {
        lock (_gate)
        {
            DayUse shared = ForLocked("*");
            DayUse own = ForLocked(owner);
            shared.Reserved = Math.Max(0, shared.Reserved - reservation);
            own.Reserved = Math.Max(0, own.Reserved - reservation);
            shared.Spent += Math.Max(0, actual);
            own.Spent += Math.Max(0, actual);
        }
    }

    public long SpentToday(string owner)
    {
        lock (_gate)
            return ForLocked(owner).Spent;
    }

    private DayUse ForLocked(string owner)
    {
        string day = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!_use.TryGetValue(owner, out DayUse? use) || use.Day != day)
        {
            use = new DayUse(day);
            _use[owner] = use;
        }

        return use;
    }

    private sealed class DayUse(string day)
    {
        public string Day { get; } = day;
        public long Spent { get; set; }
        public long Reserved { get; set; }
    }
}
