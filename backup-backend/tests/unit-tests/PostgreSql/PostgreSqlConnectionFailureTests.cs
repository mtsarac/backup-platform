using System.Net.Sockets;
using System.Security.Authentication;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackupPlatform.UnitTests.PostgreSql;

public sealed class PostgreSqlConnectionFailureTests
{
    // The shapes observed against a PostgreSQL 18 server with Npgsql 10 (ticket #200).
    public static TheoryData<string, Exception, DatabaseFailureKind> Failures => new()
    {
        {
            "an untrusted server certificate (VerifyFull)",
            new NpgsqlException("Exception while performing SSL handshake",
                new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.")),
            DatabaseFailureKind.ServerCertificateUntrusted
        },
        {
            "a server without SSL",
            new NpgsqlException("SSL connection requested. No SSL enabled connection from this host is configured."),
            DatabaseFailureKind.Unreachable
        },
        {
            "a TLS problem the server reports in words",
            new NpgsqlException("The SSL/TLS negotiation failed."),
            DatabaseFailureKind.InvalidConfiguration
        },
        {
            "a wrong password",
            new PostgresException("password authentication failed", "FATAL", "FATAL", PostgresErrorCodes.InvalidPassword),
            DatabaseFailureKind.AuthenticationFailed
        },
        {
            "a missing database",
            new PostgresException("database does not exist", "FATAL", "FATAL", PostgresErrorCodes.InvalidCatalogName),
            DatabaseFailureKind.InvalidConfiguration
        },
        {
            "a closed port",
            new NpgsqlException("Failed to connect to 127.0.0.1:5432", new SocketException()),
            DatabaseFailureKind.Unreachable
        }
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void ConnectionFailuresAreClassifiedAndOnlyAnUntrustedCertificateIsTheCertificateKind(string scenario, Exception exception, DatabaseFailureKind expected)
    {
        var translated = PostgreSqlSession.Translate(NullLogger.Instance, "connect", exception, timedOut: false);

        Assert.True(expected == translated.Kind, scenario);
        Assert.Same(exception, translated.InnerException);
    }

    [Fact]
    public void AnUntrustedCertificateGetsItsOwnReasonAndCauseForBackupPreChecksAndRestores()
    {
        var error = new DatabaseProviderException(DatabaseFailureKind.ServerCertificateUntrusted, "The server's certificate could not be verified.");

        var backup = PostgreSqlFailures.FromConnection(error);
        var restore = PostgreSqlRestoreProvider.Failure(error, RestoreFailureKind.PreCheckFailed, RestoreExecutionStage.PreparingTarget);

        Assert.Equal((PostgreSqlReasons.ServerCertificateUntrusted, BackupFailureCause.ServerCertificateUntrusted), (backup.ReasonCode, backup.Cause));
        Assert.Equal((PostgreSqlReasons.ServerCertificateUntrusted, BackupFailureCause.ServerCertificateUntrusted), (restore.ReasonCode, restore.Cause));
    }

    [Fact]
    public void VerificationReportsAnUntrustedCertificateRatherThanAGenericFailure()
    {
        var error = new DatabaseProviderException(DatabaseFailureKind.ServerCertificateUntrusted, "certificate rejected");
        Assert.Equal("server-certificate-untrusted", BackupPlatform.BackupProviders.PostgreSql.Restore.PostgreSqlRestoreVerifier.ReasonFor(error, "unexpected"));
    }
}
