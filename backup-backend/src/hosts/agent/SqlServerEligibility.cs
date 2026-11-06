using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// Whether this agent can be given SQL Server work. A Windows agent is the supported SQL Server host and is always eligible. A Linux agent is eligible only
/// when its staging is a valid Mapped area: the agent directory exists and is writable, and a different server directory is declared (SQL Server running
/// in a container or elsewhere and seeing the same files under that path). This is capability, not reachability: whether the server answers and really
/// sees the directory is proven by the pre-check at the start of every run.
/// </summary>
internal static class SqlServerEligibility
{
    public static bool IsEligible(AgentBackupStagingOptions? staging, bool windows, Func<string, bool> isWritableDirectory)
    {
        if (windows) return true;
        return staging is { Mapping: StagingMappingKind.Mapped, AgentDirectory: { } agentDirectory }
            && AgentBackupOptionsValidation.TryCreateStaging(staging, out _)
            && isWritableDirectory(agentDirectory);
    }

    public static bool IsWritableDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            var probe = Path.Combine(path, ".bp-capability-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }
}
