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
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Profile name cannot be empty.", nameof(name));
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
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Profile name cannot be empty.", nameof(name));
        ValidateZone(timeZoneId);
        Name = name.Trim();
        TimeZoneId = timeZoneId;
        UpdatedAt = now;
    }

    public void SetWindows(IEnumerable<TransferScheduleWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var list = windows.ToList();
        // Validate overlaps per day
        foreach (var group in list.GroupBy(w => w.DayOfWeek))
        {
            var ordered = group.OrderBy(w => w.StartTime).ToList();
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                if (ordered[i].EndTime > ordered[i + 1].StartTime)
                    throw new ArgumentException($"Windows overlap on {group.Key}: {ordered[i].StartTime}-{ordered[i].EndTime} and {ordered[i + 1].StartTime}-{ordered[i + 1].EndTime}");
            }
        }
        _windows.Clear();
        _windows.AddRange(list);
    }

    /// <summary>
    /// Resolves the effective limit at the given UTC instant, and how long until the current window ends or next window starts.
    /// Returns:
    /// - limit: bytes/sec if in a window, or null if outside any window (unlimited by profile).
    /// - nextTransition: the UTC instant when the next change (window start or end) occurs.
    /// </summary>
    public (long? Limit, DateTimeOffset? NextTransition) Evaluate(DateTimeOffset utcInstant)
    {
        var tz = ResolveZone(TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(utcInstant, tz);
        var currentDay = local.DayOfWeek;
        var currentTime = TimeOnly.FromTimeSpan(local.TimeOfDay);

        // Find active window today
        var active = _windows.FirstOrDefault(w => w.DayOfWeek == currentDay && currentTime >= w.StartTime && currentTime < w.EndTime);
        long? currentLimit = active?.BytesPerSecond;

        // Find the next transition instant
        DateTimeOffset? nextTransition = null;

        if (active != null)
        {
            var activeEndDateTime = new DateTime(local.Year, local.Month, local.Day, active.EndTime.Hour, active.EndTime.Minute, active.EndTime.Second);
            // Handle DST ambiguities using TimeZoneInfo
            nextTransition = ConvertLocalToUtc(activeEndDateTime, tz);
        }
        else
        {
            for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
            {
                var checkDate = local.DateTime.Date.AddDays(dayOffset);
                var checkDay = checkDate.DayOfWeek;
                var windowsOnDay = _windows.Where(w => w.DayOfWeek == checkDay).OrderBy(w => w.StartTime).ToList();

                foreach (var w in windowsOnDay)
                {
                    if (dayOffset == 0 && w.StartTime <= currentTime) continue;
                    var startDateTime = new DateTime(checkDate.Year, checkDate.Month, checkDate.Day, w.StartTime.Hour, w.StartTime.Minute, w.StartTime.Second);
                    nextTransition = ConvertLocalToUtc(startDateTime, tz);
                    break;
                }
                if (nextTransition != null) break;
            }
        }

        return (currentLimit, nextTransition);
    }

    private static DateTimeOffset ConvertLocalToUtc(DateTime localDateTime, TimeZoneInfo tz)
    {
        // When ambiguous (DST fall-back), choose standard or daylight properly
        var offset = tz.GetUtcOffset(localDateTime);
        var localWithOffset = new DateTimeOffset(localDateTime, offset);
        return localWithOffset.ToUniversalTime();
    }

    private static void ValidateZone(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || !ZoneShape.IsMatch(zone))
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
        if (startTime >= endTime) throw new ArgumentException("Window startTime must be strictly earlier than endTime.");
        UploadBandwidth.Validate(bytesPerSecond);

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
