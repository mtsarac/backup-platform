namespace BackupPlatform.Application.Backups;

/// <summary>
/// Names this backend process in delete leases, so an operator can tell which node holds a delete and so two nodes never share a name.
/// One per process (the host registers a singleton built from <see cref="BackupRetentionOptions.ResolveNodeId"/>): a node that restarts is a new
/// owner, and its predecessor's leases simply expire.
/// </summary>
public sealed record BackupNodeIdentity(string Value);
