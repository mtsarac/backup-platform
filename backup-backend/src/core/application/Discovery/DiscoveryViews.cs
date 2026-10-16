namespace BackupPlatform.Application.Discovery;

/// <summary>Tenant-facing read models; their JSON (camelCase) is the contract of the discovery UI.</summary>
public sealed record DiscoverySourceView(string Source, string Status, string? ReasonCode);

public sealed record DiscoveryAgentStatus(
    Guid AgentId, bool Enabled, int ScanIntervalMinutes, bool IncludeDocker, string ScanState, string? ScanReason,
    DateTimeOffset? LastScanAt, DateTimeOffset? LastSuccessfulScanAt, string? LastOutcome, string? LastFailureCode,
    DateTimeOffset? NextScanDueAt, int InstanceCount, IReadOnlyList<DiscoverySourceView> Sources);

public sealed record DiscoveryCredentialView(bool Bound, string? CredentialReference, bool IntegratedSecurity, bool Encrypt, bool TrustServerCertificate);

public sealed record DiscoveryLastChangeView(string Kind, DateTimeOffset At);

public sealed record DiscoveryDatabaseCounts(int Total, int User, int BackupEligible);

public sealed record DiscoveryInstanceSummary(
    Guid Id, Guid AgentId, string InstanceKey, string Engine, string Source, string DisplayName, string? InstanceName, string Host, int? Port,
    string? Version, string? Edition, string? HostPlatform, string? ContainerName, string? ContainerImage, string Presence, string Reachability,
    string Access, string? AccessReasonCode, string FullBackup, string? FullBackupReasonCode, DiscoveryCredentialView Credential,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DiscoveryLastChangeView? LastChange, DateTimeOffset? DatabasesCheckedAt,
    DiscoveryDatabaseCounts DatabaseCounts);

public sealed record DiscoveryJobReference(Guid Id, string Name, bool Enabled);

public sealed record DiscoveryDatabaseView(
    string Name, bool IsSystem, string State, string FullBackup, string? FullBackupReasonCode, string Presence, DateTimeOffset LastSeenAt,
    IReadOnlyList<DiscoveryJobReference> Jobs);

public sealed record DiscoveryInstanceDetail(DiscoveryInstanceSummary Instance, IReadOnlyList<DiscoveryDatabaseView> Databases);
