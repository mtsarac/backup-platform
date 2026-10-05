using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

/// <summary>
/// Backend-wide execution policy: how many backups may run at once and how often scheduled retries are looked for. These limits are
/// BACKEND policy. What an agent reports about itself (capabilities, heartbeat) never raises them. Enforcement counts Leased/Running
/// attempts in the database, so it holds across process restarts and across backend instances.
/// </summary>
public sealed class BackupExecutionOptions
{
    public const int DefaultGlobalMaxConcurrentBackups = 20;
    public const int DefaultTenantMaxConcurrentBackups = 5;
    public const int DefaultAgentMaxConcurrentBackups = 1;

    /// <summary>Backups executing at once across every tenant and agent.</summary>
    public int GlobalMaxConcurrentBackups { get; init; } = DefaultGlobalMaxConcurrentBackups;

    /// <summary>Default per-tenant limit, used unless <see cref="TenantOverrides"/> names the tenant.</summary>
    public int TenantMaxConcurrentBackups { get; init; } = DefaultTenantMaxConcurrentBackups;

    /// <summary>Per-tenant limits by tenant id (Guid text). An override replaces the default for that tenant; the global limit still caps the sum.</summary>
    public Dictionary<string, int> TenantOverrides { get; init; } = [];

    /// <summary>Backups one agent may execute at once. 1 keeps the agent strictly serial.</summary>
    public int AgentMaxConcurrentBackups { get; init; } = DefaultAgentMaxConcurrentBackups;

    /// <summary>How often the retry worker looks for RetryPending runs whose backoff has elapsed. Retries are durable; this only bounds how late one starts.</summary>
    public TimeSpan RetryScanInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound of retries started in one scan.</summary>
    public int RetryScanBatchSize { get; init; } = 50;

    /// <summary>The override for the tenant if one is configured under any valid spelling of its id (case, braces, hyphens), else the default.</summary>
    public int TenantLimit(TenantId tenantId)
    {
        foreach (var (key, limit) in TenantOverrides)
            if (Guid.TryParse(key, out var parsed) && parsed == tenantId.Value) return limit;
        return TenantMaxConcurrentBackups;
    }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (GlobalMaxConcurrentBackups < 1) problems.Add("GlobalMaxConcurrentBackups must be at least 1.");
        if (TenantMaxConcurrentBackups < 1) problems.Add("TenantMaxConcurrentBackups must be at least 1.");
        if (AgentMaxConcurrentBackups < 1) problems.Add("AgentMaxConcurrentBackups must be at least 1.");
        if (RetryScanInterval < TimeSpan.FromSeconds(1)) problems.Add("RetryScanInterval must be at least one second.");
        if (RetryScanBatchSize < 1) problems.Add("RetryScanBatchSize must be at least 1.");
        foreach (var (key, limit) in TenantOverrides)
        {
            if (!Guid.TryParse(key, out _)) problems.Add($"TenantOverrides key '{key}' is not a tenant id.");
            if (limit < 1) problems.Add($"TenantOverrides limit for '{key}' must be at least 1.");
        }
        return problems;
    }

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count != 0) throw new InvalidOperationException("Invalid backup execution options: " + string.Join(" ", problems));
    }
}
