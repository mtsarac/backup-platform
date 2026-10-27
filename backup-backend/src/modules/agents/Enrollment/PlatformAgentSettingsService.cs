using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Enrollment;

public enum SavePlatformAgentSettingsStatus
{
    Saved = 1,
    InvalidServerUrl = 2,
    InvalidLifetime = 3
}

public sealed record PlatformAgentSettingsView(
    string? ServerUrl, int EnrollmentTokenLifetimeMinutes, int MinimumMinutes, int MaximumMinutes, DateTimeOffset? UpdatedAt);

public sealed record SavePlatformAgentSettingsResult(SavePlatformAgentSettingsStatus Status, PlatformAgentSettingsView? Settings);

/// <summary>Reads and changes the platform agent settings. Authorization (platform administrator) is the caller's responsibility.</summary>
public sealed class PlatformAgentSettingsService(IPlatformAgentSettingsStore store, TimeProvider timeProvider, AgentSecurityOptions options)
{
    private int MinimumMinutes => (int)options.MinimumEnrollmentTokenLifetime.TotalMinutes;
    private int MaximumMinutes => (int)options.MaximumEnrollmentTokenLifetime.TotalMinutes;

    public async Task<PlatformAgentSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var state = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        return new PlatformAgentSettingsView(state?.ServerUrl, EffectiveMinutes(state), MinimumMinutes, MaximumMinutes, state?.UpdatedAt);
    }

    /// <summary>The lifetime every new tenant token gets. A stored value that configured limits have since excluded is clamped, never offered to a tenant as an error.</summary>
    public async Task<TimeSpan> GetTokenLifetimeAsync(CancellationToken cancellationToken) =>
        TimeSpan.FromMinutes(EffectiveMinutes(await store.GetAsync(cancellationToken).ConfigureAwait(false)));

    /// <summary>The stored server address, or null when none is set.</summary>
    public async Task<string?> GetServerUrlAsync(CancellationToken cancellationToken) =>
        (await store.GetAsync(cancellationToken).ConfigureAwait(false))?.ServerUrl;

    public async Task<SavePlatformAgentSettingsResult> SaveAsync(
        string? serverUrl, int enrollmentTokenLifetimeMinutes, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!AgentServerUrl.TryNormalize(serverUrl, out var normalized))
        {
            return new SavePlatformAgentSettingsResult(SavePlatformAgentSettingsStatus.InvalidServerUrl, null);
        }
        if (enrollmentTokenLifetimeMinutes < MinimumMinutes || enrollmentTokenLifetimeMinutes > MaximumMinutes)
        {
            return new SavePlatformAgentSettingsResult(SavePlatformAgentSettingsStatus.InvalidLifetime, null);
        }
        var now = timeProvider.GetUtcNow();
        await store.SaveAsync(normalized, enrollmentTokenLifetimeMinutes, actor, now, cancellationToken).ConfigureAwait(false);
        return new SavePlatformAgentSettingsResult(SavePlatformAgentSettingsStatus.Saved, await GetAsync(cancellationToken).ConfigureAwait(false));
    }

    private int EffectiveMinutes(PlatformAgentSettingsState? state) =>
        Math.Clamp(state?.EnrollmentTokenLifetimeMinutes ?? (int)options.DefaultEnrollmentTokenLifetime.TotalMinutes, MinimumMinutes, MaximumMinutes);
}

/// <summary>The one definition of a usable agent server address, shared by the stored setting and the configured fallback.</summary>
public static class AgentServerUrl
{
    /// <summary>
    /// True with the address normalized (no trailing slash), or true with null for an empty value. False for anything that is not an absolute
    /// https URL with a host and without user info, query or fragment.
    /// </summary>
    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }
        var url = uri.AbsoluteUri.TrimEnd('/');
        if (url.Length > PlatformAgentSettings.MaximumServerUrlLength)
        {
            return false;
        }
        normalized = url;
        return true;
    }
}
