using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace BackupPlatform.Domain.Backups;

/// <summary>A schedule setting was rejected. <see cref="Field"/> and <see cref="Code"/> are stable and machine-readable; the message is safe to show.</summary>
public sealed class InvalidBackupScheduleException(string field, string code, string message) : ArgumentException(message)
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}

/// <summary>
/// When a <see cref="BackupJob"/> should run on its own. This is the ONLY place that parses cron text and resolves time zones; every other
/// type works with validated occurrences, which are UTC instants.
/// </summary>
/// <remarks>
/// <para>Cron is the standard five-field form (minute hour day-of-month month day-of-week). A six-field form with leading seconds exists
/// for development and tests and is accepted only when the caller allows it; rehydrating a stored schedule never depends on that switch.</para>
/// <para>The time zone is an IANA identifier (<c>Europe/Istanbul</c>, <c>UTC</c>) and is always explicit: the server's local zone is never
/// consulted. Daylight-saving transitions follow Cronos: a fixed time inside a spring-forward gap fires once, right after the gap; a fixed
/// time that occurs twice on a fall-back night fires once, at its first occurrence. Occurrences are therefore distinct UTC instants and a
/// logical occurrence can never be produced twice.</para>
/// <para><see cref="EffectiveFrom"/> is the instant before which occurrences do not count. It moves to "now" whenever the cron, the time
/// zone or the enabled state changes, so enabling or editing a schedule never replays the past.</para>
/// </remarks>
public sealed class BackupSchedule
{
    public static readonly TimeSpan MinimumGracePeriod = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumGracePeriod = TimeSpan.FromDays(7);
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromMinutes(30);

    private static readonly Regex ZoneShape = new(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Zones = new(StringComparer.Ordinal);

    private BackupSchedule() { CronExpression = null!; TimeZoneId = null!; }
    private BackupSchedule(bool enabled, string cron, string timeZoneId, TimeSpan grace, DateTimeOffset effectiveFrom)
    {
        Enabled = enabled;
        CronExpression = cron;
        TimeZoneId = timeZoneId;
        MissedRunGracePeriod = grace;
        EffectiveFrom = BackupGuard.Timestamp(effectiveFrom);
    }

    public bool Enabled { get; private set; }
    public string CronExpression { get; private set; }
    public string TimeZoneId { get; private set; }
    /// <summary>How late after its scheduled instant an occurrence may still start. Later than that it is Missed and never runs.</summary>
    public TimeSpan MissedRunGracePeriod { get; private set; }
    public DateTimeOffset EffectiveFrom { get; private set; }

    /// <summary>A short stable digest of what determines the occurrences (cron and zone); lets a derived trigger be recognised as stale.</summary>
    public string Fingerprint
    {
        get
        {
            var hash = 14695981039346656037UL;
            foreach (var value in Encoding.UTF8.GetBytes($"{CronExpression}|{TimeZoneId}"))
            {
                hash = (hash ^ value) * 1099511628211UL;
            }
            return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public static BackupSchedule Create(bool enabled, string? cronExpression, string? timeZoneId, TimeSpan? gracePeriod, DateTimeOffset now, bool allowSeconds = false)
    {
        var cron = ValidateCron(cronExpression, allowSeconds);
        var zone = ValidateZone(timeZoneId);
        var grace = ValidateGrace(gracePeriod);
        return new BackupSchedule(enabled, cron, zone, grace, now);
    }

    /// <summary>The schedule after an edit. The anchor only moves when the edit changes which instants are occurrences or turns the schedule on.</summary>
    public BackupSchedule Change(bool enabled, string? cronExpression, string? timeZoneId, TimeSpan? gracePeriod, DateTimeOffset now, bool allowSeconds = false)
    {
        var cron = ValidateCron(cronExpression, allowSeconds);
        var zone = ValidateZone(timeZoneId);
        var grace = ValidateGrace(gracePeriod);
        var reanchor = cron != CronExpression || zone != TimeZoneId || (enabled && !Enabled);
        return new BackupSchedule(enabled, cron, zone, grace, reanchor ? now : EffectiveFrom);
    }

    /// <summary>Same schedule, counting occurrences only from <paramref name="now"/> on. Used when the owning job is re-enabled.</summary>
    public BackupSchedule Reanchor(DateTimeOffset now) => new(Enabled, CronExpression, TimeZoneId, MissedRunGracePeriod, now);

    /// <summary>The first occurrence strictly after <paramref name="instant"/> (never before <see cref="EffectiveFrom"/>), as a UTC instant.</summary>
    public DateTimeOffset? NextOccurrenceAfter(DateTimeOffset instant)
    {
        var from = instant < EffectiveFrom ? EffectiveFrom.AddTicks(-1) : instant;
        var next = Parse(CronExpression).GetNextOccurrence(from.ToUniversalTime(), ResolveZone(TimeZoneId), inclusive: false);
        return next?.ToUniversalTime();
    }

    /// <summary>Occurrences in (<paramref name="afterExclusive"/>, <paramref name="upToInclusive"/>], ascending, never before <see cref="EffectiveFrom"/>.</summary>
    public IEnumerable<DateTimeOffset> OccurrencesBetween(DateTimeOffset afterExclusive, DateTimeOffset upToInclusive)
    {
        var after = afterExclusive < EffectiveFrom ? EffectiveFrom : afterExclusive;
        if (after >= upToInclusive) return [];
        return Parse(CronExpression).GetOccurrences(after.ToUniversalTime(), upToInclusive.ToUniversalTime(), ResolveZone(TimeZoneId), fromInclusive: false, toInclusive: true)
            .Select(occurrence => occurrence.ToUniversalTime());
    }

    private static string ValidateCron(string? text, bool allowSeconds)
    {
        var cron = text?.Trim();
        if (string.IsNullOrEmpty(cron) || cron.Length > 128 || cron.Any(char.IsControl))
            throw new InvalidBackupScheduleException("cronExpression", "invalid_cron", "A cron expression is required.");
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (fields == 6 && !allowSeconds)
            throw new InvalidBackupScheduleException("cronExpression", "invalid_cron", "Use the five-field cron form: minute hour day-of-month month day-of-week.");
        if (fields is not (5 or 6))
            throw new InvalidBackupScheduleException("cronExpression", "invalid_cron", "A cron expression has five fields: minute hour day-of-month month day-of-week.");
        cron = string.Join(' ', cron.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        try { _ = Parse(cron); }
        catch (Cronos.CronFormatException) { throw new InvalidBackupScheduleException("cronExpression", "invalid_cron", "The cron expression is not valid."); }
        return cron;
    }

    private static string ValidateZone(string? id)
    {
        var zone = id?.Trim();
        if (string.IsNullOrEmpty(zone) || zone.Length > 64 || !ZoneShape.IsMatch(zone))
            throw new InvalidBackupScheduleException("timeZone", "invalid_time_zone", "An IANA time zone identifier such as Europe/Istanbul or UTC is required.");
        try { _ = ResolveZone(zone); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidBackupScheduleException("timeZone", "invalid_time_zone", "The time zone is unknown.");
        }
        return zone;
    }

    private static TimeSpan ValidateGrace(TimeSpan? grace)
    {
        var value = grace ?? DefaultGracePeriod;
        if (value < MinimumGracePeriod || value > MaximumGracePeriod)
            throw new InvalidBackupScheduleException("missedRunGraceSeconds", "invalid_grace", "The missed-run grace period must be between 1 minute and 7 days.");
        return value;
    }

    private static Cronos.CronExpression Parse(string cron) => Cronos.CronExpression.Parse(cron, cron.Split(' ').Length == 6 ? Cronos.CronFormat.IncludeSeconds : Cronos.CronFormat.Standard);

    private static TimeZoneInfo ResolveZone(string id) => Zones.GetOrAdd(id, static key => TimeZoneInfo.FindSystemTimeZoneById(key));
}
