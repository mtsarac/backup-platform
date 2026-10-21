using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

public sealed class BandwidthProfileDomainTests
{
    private readonly TenantId _tenant = TenantId.New();

    [Fact]
    public void Profile_creation_validates_timezone_and_name()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new BandwidthProfile(Guid.NewGuid(), _tenant, "", "Europe/Istanbul", now));
        Assert.Throws<ArgumentException>(() => new BandwidthProfile(Guid.NewGuid(), _tenant, "Standard", "Invalid/Timezone", now));

        var profile = new BandwidthProfile(Guid.NewGuid(), _tenant, "Standard", "Europe/Istanbul", now);
        Assert.Equal("Standard", profile.Name);
        Assert.Equal("Europe/Istanbul", profile.TimeZoneId);
        Assert.Empty(profile.Windows);
    }

    [Fact]
    public void Window_creation_validates_day_time_and_rate()
    {
        var profileId = Guid.NewGuid();
        // Rate range check
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TransferScheduleWindow(Guid.NewGuid(), profileId, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), 10)); // < Min
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TransferScheduleWindow(Guid.NewGuid(), profileId, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), 200_000_000_000)); // > Max

        // Start >= End check (must not be zero length or inverted)
        Assert.Throws<ArgumentException>(() =>
            new TransferScheduleWindow(Guid.NewGuid(), profileId, DayOfWeek.Monday, new TimeOnly(17, 0), new TimeOnly(9, 0), 500_000));
        Assert.Throws<ArgumentException>(() =>
            new TransferScheduleWindow(Guid.NewGuid(), profileId, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(9, 0), 500_000));

        var window = new TransferScheduleWindow(Guid.NewGuid(), profileId, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), 500_000);
        Assert.Equal(DayOfWeek.Monday, window.DayOfWeek);
        Assert.Equal(new TimeOnly(9, 0), window.StartTime);
        Assert.Equal(new TimeOnly(17, 0), window.EndTime);
        Assert.Equal(500_000, window.BytesPerSecond);
    }

    [Fact]
    public void Overlapping_windows_on_same_day_rejected()
    {
        var profile = new BandwidthProfile(Guid.NewGuid(), _tenant, "Office", "Europe/Istanbul", DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => profile.SetWindows([
            new TransferScheduleWindow(Guid.NewGuid(), profile.Id, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(13, 0), 500_000),
            new TransferScheduleWindow(Guid.NewGuid(), profile.Id, DayOfWeek.Monday, new TimeOnly(12, 0), new TimeOnly(18, 0), 1_000_000)
        ]));
    }

    [Fact]
    public void Evaluate_inside_window_returns_rate_and_window_end_transition()
    {
        var profile = new BandwidthProfile(Guid.NewGuid(), _tenant, "Office", "Europe/London", DateTimeOffset.UtcNow);
        // Monday 09:00 to 17:00 London time, 1 MB/s
        profile.SetWindows([
            new TransferScheduleWindow(Guid.NewGuid(), profile.Id, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), 1_000_000)
        ]);

        // 2026-06-15 is a Monday. London is in BST (UTC+1).
        // 10:00 BST is 09:00 UTC.
        var nowUtc = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);
        var (limit, nextTransition) = profile.Evaluate(nowUtc);

        Assert.Equal(1_000_000, limit);
        Assert.NotNull(nextTransition);
        // 17:00 BST is 16:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 16, 0, 0, TimeSpan.Zero), nextTransition.Value);
    }

    [Fact]
    public void Evaluate_outside_window_returns_null_and_next_window_start_transition()
    {
        var profile = new BandwidthProfile(Guid.NewGuid(), _tenant, "Office", "Europe/London", DateTimeOffset.UtcNow);
        profile.SetWindows([
            new TransferScheduleWindow(Guid.NewGuid(), profile.Id, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), 1_000_000)
        ]);

        // 2026-06-15 Monday 08:00 BST is 07:00 UTC. Window starts at 09:00 BST (08:00 UTC).
        var nowUtc = new DateTimeOffset(2026, 6, 15, 7, 0, 0, TimeSpan.Zero);
        var (limit, nextTransition) = profile.Evaluate(nowUtc);

        Assert.Null(limit);
        Assert.NotNull(nextTransition);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 8, 0, 0, TimeSpan.Zero), nextTransition.Value);
    }

    [Fact]
    public void Evaluate_across_dst_transition_respects_timezone_offset()
    {
        // London clocks fall back on Sunday, October 25, 2026 (BST UTC+1 -> GMT UTC+0 at 02:00 BST)
        var profile = new BandwidthProfile(Guid.NewGuid(), _tenant, "Office", "Europe/London", DateTimeOffset.UtcNow);
        profile.SetWindows([
            new TransferScheduleWindow(Guid.NewGuid(), profile.Id, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), 2_000_000)
        ]);

        // Before fall-back: Monday Oct 19, 2026 (BST = UTC+1). 09:00 BST -> 08:00 UTC
        var oct19Utc = new DateTimeOffset(2026, 10, 19, 7, 0, 0, TimeSpan.Zero);
        var (_, next19) = profile.Evaluate(oct19Utc);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), next19!.Value);

        // After fall-back: Monday Oct 26, 2026 (GMT = UTC+0). 09:00 GMT -> 09:00 UTC
        var oct26Utc = new DateTimeOffset(2026, 10, 26, 7, 0, 0, TimeSpan.Zero);
        var (_, next26) = profile.Evaluate(oct26Utc);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero), next26!.Value);
    }
}
