namespace BackupPlatform.Domain.Backups;

/// <summary>The engine keys the platform backs up. One definition shared by the backend; the agent keeps its own copy in BackupProviders.Abstractions (the domain depends on nothing).</summary>
public static class DatabaseEngines
{
    public const string MsSql = "mssql";
    public const string PostgreSql = "postgres";
    public const string FileSystem = "file-system";

    public static bool IsValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? engine) => engine is MsSql or PostgreSql or FileSystem;
}
