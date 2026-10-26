namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>The engine keys the platform backs up. The agent-side copy of the domain's DatabaseEngines (the domain depends on nothing, so the agent cannot share it); a unit test keeps the two in step.</summary>
public static class DatabaseEngineKeys
{
    public const string MsSql = "mssql";
    public const string PostgreSql = "postgres";
    public const string FileSystem = "file-system";

    public static bool IsValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? engine) => engine is MsSql or PostgreSql or FileSystem;
}
