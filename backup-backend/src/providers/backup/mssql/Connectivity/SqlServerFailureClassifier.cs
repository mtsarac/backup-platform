using System.Net.Sockets;
using BackupPlatform.BackupProviders.Abstractions.Database;
using Microsoft.Data.SqlClient;

namespace BackupPlatform.BackupProviders.MSSQL.Connectivity;

internal enum SqlServerOperationPhase
{
    Connect,
    Command
}

/// <summary>
/// Turns SqlClient failures into a stable kind plus a fixed, user-safe sentence. The original exception is never part of the
/// message; callers log it separately as an internal diagnostic.
/// </summary>
internal static class SqlServerFailureClassifier
{
    // Login failed, account disabled/locked/expired/must change password, requested database cannot be opened.
    private static readonly HashSet<int> AuthenticationErrors = [18451, 18452, 18456, 18470, 18486, 18487, 18488, 4060, 4064];

    // Permission denied on object/column/database/server, or the database is not accessible under the current security context.
    private static readonly HashSet<int> PermissionErrors = [229, 230, 262, 297, 300, 916];

    // Broken or lost transport while a command was running.
    private static readonly HashSet<int> ConnectionLostErrors = [-1, 0, 64, 121, 233, 1231, 10053, 10054, 10060, 10061];

    public static (DatabaseFailureKind Kind, string UserMessage, int? SqlErrorNumber) Classify(Exception exception, SqlServerOperationPhase phase)
    {
        ArgumentNullException.ThrowIfNull(exception);

        switch (exception)
        {
            case SqlException sql:
                return ClassifyNumber(sql.Number, phase);
            case TimeoutException:
                return (DatabaseFailureKind.Timeout, Messages.Timeout, null);
            case IOException or SocketException:
                return (DatabaseFailureKind.Unreachable, Messages.Unreachable, null);
            case ArgumentException or NotSupportedException or FormatException:
                return (DatabaseFailureKind.InvalidConfiguration, Messages.InvalidConfiguration, null);
            default:
                return (DatabaseFailureKind.Unexpected, Messages.Unexpected, null);
        }
    }

    public static string MessageFor(DatabaseFailureKind kind) => kind switch
    {
        DatabaseFailureKind.Unreachable => Messages.Unreachable,
        DatabaseFailureKind.Timeout => Messages.Timeout,
        DatabaseFailureKind.AuthenticationFailed => Messages.Authentication,
        DatabaseFailureKind.PermissionDenied => Messages.Permission,
        DatabaseFailureKind.InvalidConfiguration => Messages.InvalidConfiguration,
        _ => Messages.Unexpected
    };

    /// <summary>Classifies a SQL Server error number. Pure, so every mapping is unit-testable without a live server.</summary>
    internal static (DatabaseFailureKind Kind, string UserMessage, int? SqlErrorNumber) ClassifyNumber(int number, SqlServerOperationPhase phase)
    {
        if (number == -2)
        {
            return (DatabaseFailureKind.Timeout, Messages.Timeout, number);
        }

        if (AuthenticationErrors.Contains(number))
        {
            return (DatabaseFailureKind.AuthenticationFailed, Messages.Authentication, number);
        }

        if (PermissionErrors.Contains(number))
        {
            return (DatabaseFailureKind.PermissionDenied, Messages.Permission, number);
        }

        // Any other failure while establishing the connection is a connectivity problem (name resolution, refused port,
        // missing instance, TLS negotiation). During a command only a known set of transport errors qualifies.
        if (phase == SqlServerOperationPhase.Connect || ConnectionLostErrors.Contains(number))
        {
            return (DatabaseFailureKind.Unreachable, Messages.Unreachable, number);
        }

        return (DatabaseFailureKind.Unexpected, Messages.Unexpected, number);
    }

    private static class Messages
    {
        public const string Unreachable =
            "The SQL Server instance could not be reached. Check the address, port or instance name, network access and the encryption settings.";

        public const string Timeout = "The SQL Server operation timed out.";
        public const string Authentication = "SQL Server rejected the supplied credentials or the login cannot use the requested database.";
        public const string Permission = "The SQL Server principal lacks a permission required for this operation.";
        public const string InvalidConfiguration = "The SQL Server connection settings are invalid.";
        public const string Unexpected = "An unexpected SQL Server error occurred. Details are in the agent log.";
    }
}
