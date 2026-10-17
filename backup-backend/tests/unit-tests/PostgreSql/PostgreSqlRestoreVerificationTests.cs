using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql;
using BackupPlatform.BackupProviders.PostgreSql.Restore;
using BackupPlatform.BackupProviders.PostgreSql.Tools;

namespace BackupPlatform.UnitTests.PostgreSql;

public sealed class PostgreSqlRestoreVerificationTests
{
    private const string Verification = "bp_verify_0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("CREATE DATABASE src WITH TEMPLATE = template0 ENCODING = 'UTF8' LOCALE_PROVIDER = libc LOCALE = 'en_US.utf8';",
        "CREATE DATABASE \"bp_verify_0123456789abcdef0123456789abcdef\" WITH TEMPLATE = template0 ENCODING = 'UTF8' LOCALE_PROVIDER = libc LOCALE = 'en_US.utf8';")]
    [InlineData("CREATE DATABASE src WITH TEMPLATE = template0 OWNER = app_owner ENCODING = 'UTF8' LC_COLLATE = 'C';",
        "CREATE DATABASE \"bp_verify_0123456789abcdef0123456789abcdef\" WITH TEMPLATE = template0  ENCODING = 'UTF8' LC_COLLATE = 'C';")]
    [InlineData("CREATE DATABASE src WITH TEMPLATE = template0 ENCODING = 'UTF8' OWNER = \"odd owner\" TABLESPACE = fast;",
        "CREATE DATABASE \"bp_verify_0123456789abcdef0123456789abcdef\" WITH TEMPLATE = template0 ENCODING = 'UTF8'  ;")]
    public void TheVerificationCreateStatementKeepsEncodingAndLocaleButNeitherOwnerNorTablespace(string archived, string expected) =>
        Assert.Equal(expected, PostgreSqlArchive.RewriteVerificationCreateStatement(archived, "src", Verification));

    [Fact]
    public void AnOwnerLookingLocaleValueIsNotMistakenForTheOwnerClause()
    {
        var statement = "CREATE DATABASE src WITH TEMPLATE = template0 LOCALE = 'OWNER = x';";

        Assert.Contains("LOCALE = 'OWNER = x'", PostgreSqlArchive.RewriteVerificationCreateStatement(statement, "src", Verification), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DatabaseRestoreMode.IsolatedVerification, "bp_old_0123456789abcdef0123456789abcdef")]
    [InlineData(DatabaseRestoreMode.IsolatedVerification, "production")]
    [InlineData(DatabaseRestoreMode.NewDatabase, Verification)]
    [InlineData(DatabaseRestoreMode.ReplaceExistingDatabase, Verification)]
    [InlineData(DatabaseRestoreMode.NewDatabase, "BP_VERIFY_whatever")]
    [InlineData(DatabaseRestoreMode.NewDatabase, "bp_old_0123456789abcdef0123456789abcdef")]
    [InlineData(DatabaseRestoreMode.ReplaceExistingDatabase, "bp_restore_0123456789abcdef")]
    [InlineData(DatabaseRestoreMode.NewDatabase, "postgres")]
    public void EveryOtherModeRejectsReservedAndProtectedNames(DatabaseRestoreMode mode, string name)
    {
        var failure = Assert.Throws<PostgreSqlRestoreException>(() => PostgreSqlRestoreProvider.ValidateName(name, mode));

        Assert.Equal(RestoreReasonCodes.TargetProtected, failure.ReasonCode);
    }

    [Theory]
    [InlineData(DatabaseRestoreMode.IsolatedVerification, Verification)]
    [InlineData(DatabaseRestoreMode.NewDatabase, "customer_db")]
    [InlineData(DatabaseRestoreMode.ReplaceExistingDatabase, "customer_db")]
    public void ValidNamesPassTheirOwnMode(DatabaseRestoreMode mode, string name) => PostgreSqlRestoreProvider.ValidateName(name, mode);

    [Theory]
    [InlineData("{\"provider\":\"backup-platform\",\"phase\":\"previous\",\"target\":\"x\",\"operation\":\"y\"}", true)]
    [InlineData("{\"provider\":\"backup-platform\",\"phase\":\"scratch\"}", false)]
    [InlineData("{\"provider\":\"other\",\"phase\":\"previous\"}", false)]
    [InlineData("{\"phase\":\"previous\"}", false)]
    [InlineData("{\"provider\":1,\"phase\":\"previous\"}", false)]
    [InlineData("[\"backup-platform\",\"previous\"]", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheMarkerOfARetiredDatabaseReleasesARollbackCopy(string? comment, bool expected) =>
        Assert.Equal(expected, PostgreSqlRestoreVerifier.IsPreviousMarker(comment));

    [Fact]
    public void AnUntrustedExtensionIsReportedWithItsOwnFixedReasonAndNoToolText()
    {
        var failure = PostgreSqlToolFailureClassifier.Classify(PostgreSqlToolKind.Restore,
        [
            "pg_restore: error: could not execute query: ERROR:  permission denied to create extension \"amcheck\"",
            "HINT:  Must be superuser to create this extension.",
            "Command was: CREATE EXTENSION IF NOT EXISTS amcheck WITH SCHEMA public;"
        ]);

        Assert.Equal(PostgreSqlReasons.ExtensionRequiresSuperuser, failure.ReasonCode);
        Assert.DoesNotContain("amcheck", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PostgreSqlReasons.PermissionDenied, PostgreSqlToolFailureClassifier.Classify(PostgreSqlToolKind.Restore, ["ERROR:  permission denied for table t"]).ReasonCode);
    }
}
