namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>The engine keys the platform backs up. One definition shared by the backend and the agent.</summary>
public static class DatabaseEngineKeys
{
    public const string MsSql = "mssql";
    public const string PostgreSql = "postgres";
    public const string FileSystem = "file-system";

    public static bool IsValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? engine) => engine is MsSql or PostgreSql or FileSystem;
}
