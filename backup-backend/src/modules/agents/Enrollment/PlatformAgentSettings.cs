using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Enrollment;

/// <summary>
/// Platform-wide agent installation settings, managed once by a platform administrator: the backend HTTPS address the installer
/// connects to and how long an unused enrollment token stays valid. A singleton row (<see cref="SingletonId"/>); no row means defaults.
/// </summary>
public sealed class PlatformAgentSettings
{
    public const int SingletonId = 1;
    public const int MaximumServerUrlLength = 2048;

    private PlatformAgentSettings()
    {
        UpdatedBy = null!;
    }

    public PlatformAgentSettings(string? serverUrl, int enrollmentTokenLifetimeMinutes, UserId updatedBy, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(updatedBy);
        ServerUrl = serverUrl;
        EnrollmentTokenLifetimeMinutes = enrollmentTokenLifetimeMinutes;
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    public int Id { get; private set; } = SingletonId;
    public string? ServerUrl { get; private set; }
    public int EnrollmentTokenLifetimeMinutes { get; private set; }
    public UserId UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Returns false, leaving the audit stamp untouched, when both values are already current.</summary>
    public bool Set(string? serverUrl, int enrollmentTokenLifetimeMinutes, UserId updatedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(updatedBy);
        if (ServerUrl == serverUrl && EnrollmentTokenLifetimeMinutes == enrollmentTokenLifetimeMinutes)
        {
            return false;
        }
        ServerUrl = serverUrl;
        EnrollmentTokenLifetimeMinutes = enrollmentTokenLifetimeMinutes;
        UpdatedBy = updatedBy;
        UpdatedAt = now.ToUniversalTime();
        return true;
    }
}

/// <summary>What is stored: both values are the platform administrator's, unvalidated against current limits.</summary>
public sealed record PlatformAgentSettingsState(string? ServerUrl, int EnrollmentTokenLifetimeMinutes, DateTimeOffset UpdatedAt);

/// <summary>Persistence for the platform agent settings singleton.</summary>
public interface IPlatformAgentSettingsStore
{
    /// <summary>Null while a platform administrator has never saved the settings.</summary>
    Task<PlatformAgentSettingsState?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Upserts the singleton. An actual change is audited in the same transaction; saving identical values writes nothing.</summary>
    Task SaveAsync(string? serverUrl, int enrollmentTokenLifetimeMinutes, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);
}
