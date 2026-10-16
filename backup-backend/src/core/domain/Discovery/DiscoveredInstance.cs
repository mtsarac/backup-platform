using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Discovery;

public enum DiscoveryPresence
{
    Present = 1,
    Missing = 2
}

public enum DiscoveryChangeKind
{
    Discovered = 1,
    Changed,
    Recovered,
    Unreachable,
    Missing,
    Reappeared
}

/// <summary>What one scan saw of an instance. Strings are the wire vocabulary, validated at the application boundary.</summary>
public sealed record InstanceObservation(
    string Engine, string Source, string DisplayName, string? InstanceName, string Host, int? Port, string? Version, string? Edition,
    string? HostPlatform, string? ContainerName, string? ContainerImage, string Reachability, string Access, string? AccessReasonCode,
    string FullBackup, string? FullBackupReasonCode);

/// <summary>
/// A database service an agent found. Absence is never deletion: an instance that is not seen becomes <see cref="DiscoveryPresence.Missing"/>
/// after <see cref="MissesBeforeMissing"/> consecutive complete scans and comes back as <see cref="DiscoveryChangeKind.Reappeared"/>.
/// The credential binding is only the agent-local reference name and connection flags, never a secret.
/// </summary>
public sealed class DiscoveredInstance : ITenantOwned
{
    public const int MissesBeforeMissing = 2;
    public const int MaximumKeyLength = 200;

    private DiscoveredInstance()
    {
        TenantId = null!; AgentId = null!; InstanceKey = null!; Engine = null!; Source = null!; DisplayName = null!; Host = null!;
        Reachability = null!; Access = null!; FullBackup = null!;
    }

    public DiscoveredInstance(Guid id, TenantId tenantId, AgentId agentId, string instanceKey, InstanceObservation observation, DateTimeOffset now)
    {
        BackupGuard.Id(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(agentId);
        BackupGuard.Text(instanceKey, MaximumKeyLength);
        Id = id;
        TenantId = tenantId;
        AgentId = agentId;
        InstanceKey = instanceKey;
        Presence = DiscoveryPresence.Present;
        Encrypt = true;
        LastChangeKind = DiscoveryChangeKind.Discovered;
        LastChangeAt = now;
        Engine = observation.Engine;
        Access = observation.Access;
        FullBackup = observation.FullBackup;
        Assign(observation, now);
        // After Assign: its "already known" test is whether this has been set.
        FirstSeenAt = now;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public AgentId AgentId { get; private set; }
    public string InstanceKey { get; private set; }
    public string Engine { get; private set; }
    public string Source { get; private set; }
    public string DisplayName { get; private set; }
    public string? InstanceName { get; private set; }
    public string Host { get; private set; }
    public int? Port { get; private set; }
    public string? Version { get; private set; }
    public string? Edition { get; private set; }
    public string? HostPlatform { get; private set; }
    public string? ContainerName { get; private set; }
    public string? ContainerImage { get; private set; }
    public DiscoveryPresence Presence { get; private set; }
    public int MissCount { get; private set; }
    public string Reachability { get; private set; }
    public string Access { get; private set; }
    public string? AccessReasonCode { get; private set; }
    public string FullBackup { get; private set; }
    public string? FullBackupReasonCode { get; private set; }
    public string? CredentialReference { get; private set; }
    public bool IntegratedSecurity { get; private set; }
    public bool Encrypt { get; private set; }
    public bool TrustServerCertificate { get; private set; }
    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DiscoveryChangeKind? LastChangeKind { get; private set; }
    public DateTimeOffset? LastChangeAt { get; private set; }
    /// <summary>When the database list was last actually enumerated; null if never (credentials missing so far).</summary>
    public DateTimeOffset? DatabasesCheckedAt { get; private set; }

    public bool HasBinding => CredentialReference is not null || IntegratedSecurity;

    /// <summary>Records one sighting. Returns the change it caused, if any.</summary>
    public DiscoveryChangeKind? Observe(InstanceObservation observation, DateTimeOffset now)
    {
        DiscoveryChangeKind? change = null;
        if (Presence == DiscoveryPresence.Missing)
        {
            change = DiscoveryChangeKind.Reappeared;
        }
        else if (IsMaterialChange(observation))
        {
            change = DiscoveryChangeKind.Changed;
        }
        if (Reachability != "Unreachable" && observation.Reachability == "Unreachable")
        {
            change = DiscoveryChangeKind.Unreachable;
        }
        else if (Reachability == "Unreachable" && observation.Reachability == "Reachable")
        {
            change ??= DiscoveryChangeKind.Recovered;
        }
        Presence = DiscoveryPresence.Present;
        Assign(observation, now);
        if (change is { } kind)
        {
            LastChangeKind = kind;
            LastChangeAt = now;
        }
        return change;
    }

    /// <summary>Records that a complete scan of this instance's source did not see it.</summary>
    public bool MarkNotSeen(DateTimeOffset now)
    {
        MissCount++;
        if (Presence == DiscoveryPresence.Present && MissCount >= MissesBeforeMissing)
        {
            Presence = DiscoveryPresence.Missing;
            LastChangeKind = DiscoveryChangeKind.Missing;
            LastChangeAt = now;
            return true;
        }
        return false;
    }

    public void MarkDatabasesChecked(DateTimeOffset now) => DatabasesCheckedAt = now;

    public void Bind(string? credentialReference, bool integratedSecurity, bool encrypt, bool trustServerCertificate)
    {
        if (credentialReference is not null)
        {
            BackupGuard.Reference(credentialReference);
        }
        if (integratedSecurity && (Engine != "mssql" || credentialReference is not null))
        {
            throw new ArgumentException("Integrated security applies to SQL Server without a credential reference.");
        }
        CredentialReference = credentialReference;
        IntegratedSecurity = integratedSecurity;
        Encrypt = encrypt;
        TrustServerCertificate = trustServerCertificate;
    }

    private bool IsMaterialChange(InstanceObservation observation) =>
        Source != observation.Source || Host != observation.Host || Port != observation.Port || ContainerImage != observation.ContainerImage
        || Differs(Version, observation.Version) || Differs(Edition, observation.Edition);

    /// <summary>
    /// A scan that could not read a value (no credential) says nothing about it, and the cheap handshake reports a shorter dotted version
    /// (<c>17.0.5015</c>) of the same server the provider reports in full (<c>17.0.5015.4</c>); neither is a change.
    /// </summary>
    private static bool Differs(string? stored, string? observed) =>
        stored is not null && observed is not null && stored != observed
        && !stored.StartsWith(observed + ".", StringComparison.Ordinal) && !observed.StartsWith(stored + ".", StringComparison.Ordinal);

    private static string? Prefer(string? stored, string? observed) =>
        observed is null || (stored is not null && stored.StartsWith(observed + ".", StringComparison.Ordinal)) ? stored : observed;

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(Source), nameof(DisplayName), nameof(Host), nameof(Reachability))]
    private void Assign(InstanceObservation observation, DateTimeOffset now)
    {
        Source = observation.Source;
        DisplayName = observation.DisplayName;
        InstanceName = observation.InstanceName;
        Host = observation.Host;
        Port = observation.Port;
        Version = Prefer(Version, observation.Version);
        Edition = observation.Edition ?? Edition;
        HostPlatform = observation.HostPlatform ?? HostPlatform;
        ContainerName = observation.ContainerName;
        ContainerImage = observation.ContainerImage;
        Reachability = observation.Reachability;
        // "Unknown" access means this scan did not look (a targeted scan of another instance): what is already known stays.
        var keepKnownAccess = observation.Access == "Unknown" && FirstSeenAt != default;
        Access = keepKnownAccess ? Access : observation.Access;
        AccessReasonCode = keepKnownAccess ? AccessReasonCode : observation.AccessReasonCode;
        FullBackup = keepKnownAccess ? FullBackup : observation.FullBackup;
        FullBackupReasonCode = keepKnownAccess ? FullBackupReasonCode : observation.FullBackupReasonCode;
        MissCount = 0;
        LastSeenAt = now;
    }
}
