using Microsoft.Extensions.Options;

namespace BankSync.Core;

/// <summary>Converts the configured daily sync slots and time zone into concrete instants and local days.</summary>
internal sealed class SlotCalculator
{
    private readonly TimeZoneInfo _tz;
    private readonly TimeOnly[] _slots;
    private readonly TimeSpan _maxJitter;

    public SlotCalculator(IOptions<BankSyncOptions> options)
    {
        var o = options.Value;
        _tz = TimeZoneInfo.FindSystemTimeZoneById(o.TimeZone);
        _slots = o.SyncSlots.Select(s => TimeOnly.ParseExact(s, "HH:mm")).OrderBy(t => t).ToArray();
        _maxJitter = o.MaxSlotJitter;
    }

    public TimeZoneInfo TimeZone => _tz;
    public IReadOnlyList<TimeOnly> Slots => _slots;

    public DateTime ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _tz).DateTime;
    public DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(ToLocal(utc));
    public string LocalDay(DateTimeOffset utc) => LocalDate(utc).ToString("yyyy-MM-dd");

    /// <summary>First slot strictly after <paramref name="nowUtc"/>. Null when no slots are configured.</summary>
    public DateTimeOffset? NextSlot(DateTimeOffset nowUtc)
    {
        if (_slots.Length == 0) return null;
        var local = ToLocal(nowUtc);
        var today = DateOnly.FromDateTime(local);
        foreach (var slot in _slots)
        {
            var candidate = today.ToDateTime(slot);
            if (candidate > local) return ToUtc(candidate);
        }
        return ToUtc(today.AddDays(1).ToDateTime(_slots[0]));
    }

    /// <summary>Most recent slot at or before <paramref name="nowUtc"/>, possibly yesterday. Null when no slots are configured.</summary>
    public DateTimeOffset? LastSlotAtOrBefore(DateTimeOffset nowUtc)
    {
        if (_slots.Length == 0) return null;
        var local = ToLocal(nowUtc);
        var today = DateOnly.FromDateTime(local);
        for (var i = _slots.Length - 1; i >= 0; i--)
        {
            var candidate = today.ToDateTime(_slots[i]);
            if (candidate <= local) return ToUtc(candidate);
        }
        return ToUtc(today.AddDays(-1).ToDateTime(_slots[^1]));
    }

    /// <summary>Deterministic per-account delay so accounts are not fetched at the same second.</summary>
    public TimeSpan Jitter(int accountId)
    {
        if (_maxJitter <= TimeSpan.Zero) return TimeSpan.Zero;
        var seconds = (accountId * 7919L) % (long)_maxJitter.TotalSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    private DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (_tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);   // DST gap
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, _tz), TimeSpan.Zero);
    }
}
