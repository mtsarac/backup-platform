using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

/// <summary>
/// A named schedule defining upload rate limits (bytes/sec) across recurring time-of-day and day-of-week windows
/// in an explicit IANA time zone.
/// </summary>
public sealed class BandwidthProfile : ITenantOwned
{
    private static readonly Regex ZoneShape = new(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Zones = new(StringComparer.Ordinal);
    private readonly List<TransferScheduleWindow> _windows = [];

    private BandwidthProfile()
    {
        TenantId = null!;
        Name = null!;
        TimeZoneId = null!;
    }

    public BandwidthProfile(Guid id, TenantId tenantId, string name, string timeZoneId, DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(id));
        ArgumentNullException.ThrowIfNull(tenantId);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) throw new ArgumentException("Profile name must contain 1–200 characters.", nameof(name));
        ValidateZone(timeZoneId);

        Id = id;
        TenantId = tenantId;
        Name = name.Trim();
        TimeZoneId = timeZoneId;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public string Name { get; private set; }
    public string TimeZoneId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<TransferScheduleWindow> Windows => _windows.AsReadOnly();

    public void Update(string name, string timeZoneId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) throw new ArgumentException("Profile name must contain 1–200 characters.", nameof(name));
        ValidateZone(timeZoneId);
        Name = name.Trim();
        TimeZoneId = timeZoneId;
        UpdatedAt = now;
    }

    public void SetWindows(IEnumerable<TransferScheduleWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var list = windows.ToList();
        var week = TimeSpan.TicksPerDay * 7;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].ProfileId != Id) throw new ArgumentException("A window must belong to this profile.", nameof(windows));
            var start = (int)list[i].DayOfWeek * TimeSpan.TicksPerDay + list[i].StartTime.Ticks;
            var end = start + Duration(list[i]);
            for (var j = 0; j < i; j++)
            {
                var otherStart = (int)list[j].DayOfWeek * TimeSpan.TicksPerDay + list[j].StartTime.Ticks;
                for (var shift = -1; shift <= 1; shift++)
                    if (start < otherStart + shift * week + Duration(list[j]) && otherStart + shift * week < end)
                        throw new ArgumentException("Schedule windows overlap.", nameof(windows));
            }
        }
        _windows.Clear();
        _windows.AddRange(list);
    }

    private static long Duration(TransferScheduleWindow window) =>
        window.EndTime > window.StartTime ? window.EndTime.Ticks - window.StartTime.Ticks
            : TimeSpan.TicksPerDay - window.StartTime.Ticks + window.EndTime.Ticks;

    /// <summary>
    /// Resolves the effective limit at the given UTC instant, and how long until the current window ends or next window starts.
    /// Returns:
    /// - limit: bytes/sec if in a window, or null if outside any window (unlimited by profile).
    /// - nextTransition: the UTC instant when the next change (window start or end) occurs.
    /// </summary>
    public (long? Limit, DateTimeOffset? NextTransition) Evaluate(DateTimeOffset utcInstant)
    {
        if (_windows.Count == 0) return (null, null);
        var zone = ResolveZone(TimeZoneId);
        var localDate = TimeZoneInfo.ConvertTime(utcInstant, zone).Date;
        DateTimeOffset? next = null;
        for (var day = -1; day <= 7; day++)
        {
            var date = localDate.AddDays(day);
            foreach (var window in _windows)
            {
                if (window.DayOfWeek != date.DayOfWeek) continue;
                Boundary(date.Add(window.StartTime.ToTimeSpan()));
                Boundary(date.Add(window.StartTime.ToTimeSpan()).AddTicks(Duration(window)));
            }
        }

        // A clock jump can enter or leave a window without crossing either wall-clock boundary.
        if (zone.SupportsDaylightSavingTime)
        {
            var previous = utcInstant;
            var offset = zone.GetUtcOffset(previous);
            var horizon = next ?? utcInstant.AddDays(8);
            while (previous < horizon)
            {
                var probe = previous.AddHours(1);
                if (probe > horizon) probe = horizon;
                var probeOffset = zone.GetUtcOffset(probe);
                if (offset != probeOffset)
                {
                    var low = previous.UtcTicks;
                    var high = probe.UtcTicks;
                    while (high - low > 1)
                    {
                        var middle = low + (high - low) / 2;
                        if (zone.GetUtcOffset(new DateTimeOffset(middle, TimeSpan.Zero)) == offset) low = middle;
                        else high = middle;
                    }
                    Consider(new DateTimeOffset(high, TimeSpan.Zero));
                    horizon = next ?? horizon;
                }
                previous = probe;
                offset = probeOffset;
            }
        }
        return (RateAt(utcInstant), next);

        long? RateAt(DateTimeOffset instant)
        {
            var local = TimeZoneInfo.ConvertTime(instant, zone);
            var time = TimeOnly.FromTimeSpan(local.TimeOfDay);
            foreach (var window in _windows)
            {
                var active = window.EndTime > window.StartTime
                    ? window.DayOfWeek == local.DayOfWeek && time >= window.StartTime && time < window.EndTime
                    : window.DayOfWeek == local.DayOfWeek && time >= window.StartTime
                        || (DayOfWeek)(((int)window.DayOfWeek + 1) % 7) == local.DayOfWeek && time < window.EndTime;
                if (active) return window.BytesPerSecond;
            }
            return null;
        }

        void Consider(DateTimeOffset candidate)
        {
            if (candidate > utcInstant && (next is null || candidate < next)
                && RateAt(candidate) != RateAt(candidate.AddTicks(-1)))
                next = candidate;
        }

        void Boundary(DateTime local)
        {
            if (zone.IsInvalidTime(local)) return;
            if (zone.IsAmbiguousTime(local))
            {
                foreach (var offset in zone.GetAmbiguousTimeOffsets(local))
                    Consider(new DateTimeOffset(local, offset).ToUniversalTime());
            }
            else Consider(new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime());
        }
    }

    private static void ValidateZone(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || zone.Length > 64 || !ZoneShape.IsMatch(zone))
            throw new ArgumentException("An IANA time zone identifier such as Europe/Istanbul or UTC is required.", nameof(zone));
        try { _ = ResolveZone(zone); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("The time zone is unknown.", nameof(zone), exception);
        }
    }

    public static TimeZoneInfo ResolveZone(string id) => Zones.GetOrAdd(id, static key => TimeZoneInfo.FindSystemTimeZoneById(key));
}

/// <summary>
/// An active time window within a BandwidthProfile governing the maximum permitted upload bandwidth during specific hours.
/// </summary>
public sealed class TransferScheduleWindow
{
    private TransferScheduleWindow() { }

    public TransferScheduleWindow(Guid id, Guid profileId, DayOfWeek dayOfWeek, TimeOnly startTime, TimeOnly endTime, long bytesPerSecond)
    {
        if (id == Guid.Empty) throw new ArgumentException("Window ID cannot be empty.", nameof(id));
        if (profileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        if (!Enum.IsDefined(dayOfWeek)) throw new ArgumentException("Unknown day of week.", nameof(dayOfWeek));
        if (startTime == endTime) throw new ArgumentException("Window startTime and endTime must differ.");
        if (bytesPerSecond != 0) UploadBandwidth.Validate(bytesPerSecond);

        Id = id;
        ProfileId = profileId;
        DayOfWeek = dayOfWeek;
        StartTime = startTime;
        EndTime = endTime;
        BytesPerSecond = bytesPerSecond;
    }

    public Guid Id { get; private set; }
    public Guid ProfileId { get; private set; }
    public DayOfWeek DayOfWeek { get; private set; }
    public TimeOnly StartTime { get; private set; }
    public TimeOnly EndTime { get; private set; }
    public long BytesPerSecond { get; private set; }
}
