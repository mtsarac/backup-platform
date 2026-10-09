using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using BackupPlatform.BackupProviders.MSSQL.Restore;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.MsSql;

/// <summary>
/// The read-only half of a restore against a real SQL Server: HEADERONLY, FILELISTONLY and VERIFYONLY of real backup files, and how a file
/// the server cannot use is reported. Every file a test creates is removed again; the fixture sweeps anything a failed test leaves.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class MsSqlRestoreInspectionIntegrationTests(MsSqlFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly MsSqlRestoreHarness _harness = new(fixture);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private Task<RestoreInspectionResult> InspectAsync(
        StagedRestoreSource source,
        bool verify = true,
        DatabaseCredential? credential = null,
        CancellationToken cancellationToken = default) =>
        _harness.Provider.InspectBackupAsync(
            fixture.Settings, credential ?? fixture.Admin, new RestoreInspectionRequest(source.ServerPath, verify), cancellationToken);

    private static string Describe(RestoreInspectionResult result) =>
        $"{result.Failure?.Kind}/{result.Failure?.Stage}/{result.Failure?.ReasonCode}/{result.Failure?.Cause}: {result.Failure?.Message}";

    [Fact]
    public async Task HeaderAndFileListOfARealBackupAreReadByNameAndMatchTheCatalog()
    {
        var source = await _harness.FullBackupAsync();

        var result = await InspectAsync(source);

        Assert.True(result.Failure is null, Describe(result));
        var inspection = result.Inspection!;
        var header = inspection.Header;
        Assert.Equal(fixture.Source, header.DatabaseName);
        Assert.Equal(RestoreBackupKind.Database, header.Kind);
        var serverVersion = Version.Parse(Convert.ToString((await fixture.QueryAsync("SELECT CONVERT(nvarchar(50), SERVERPROPERTY('ProductVersion'))"))[0][0], System.Globalization.CultureInfo.InvariantCulture)!);
        Assert.Equal(serverVersion.Major, header.SourceServerVersion!.Major);
        Assert.Equal(1, header.Position);
        Assert.True(header.BackupSizeBytes > 0);
        Assert.False(header.IsCopyOnly);
        Assert.True(header.HasChecksums);
        Assert.False(header.IsCompressed);
        Assert.False(header.IsEncrypted);
        Assert.Equal(130, header.CompatibilityLevel);
        Assert.False(string.IsNullOrWhiteSpace(header.Collation));
        Assert.False(string.IsNullOrWhiteSpace(header.SourceServerName));
        Assert.Equal(await _harness.FamilyGuidAsync(fixture.Source), header.FamilyGuid);
        Assert.NotNull(header.BackupStartedAt);
        Assert.True(header.BackupFinishedAt >= header.BackupStartedAt);
        var sourceNow = Assert.IsType<DateTime>((await fixture.QueryAsync("SELECT SYSDATETIME()"))[0][0]);
        Assert.True(sourceNow - header.BackupFinishedAt < TimeSpan.FromHours(6), $"finished {header.BackupFinishedAt}");
        Assert.True(header.BackupFinishedAt < sourceNow.AddMinutes(1), $"finished {header.BackupFinishedAt}");

        var catalog = await _harness.FilesAsync(fixture.Source);
        Assert.Equal(catalog.Count, inspection.Files.Count);
        foreach (var (logical, path, type) in catalog)
        {
            var file = Assert.Single(inspection.Files, candidate => candidate.LogicalName == logical);
            Assert.Equal(path, file.SourcePhysicalName);
            Assert.Equal(type == "LOG" ? RestoreFileKind.Log : RestoreFileKind.Data, file.Kind);
            Assert.True(file.SizeBytes > 0);
        }

        Assert.Equal("PRIMARY", inspection.Files.Single(file => file.Kind == RestoreFileKind.Data).FileGroupName);
        Assert.Null(inspection.Files.Single(file => file.Kind == RestoreFileKind.Log).FileGroupName);
        Assert.True(inspection.VerifyOnlyPassed);
        Assert.NotNull(inspection.VerifyStartedAt);
        Assert.True(inspection.VerifyFinishedAt >= inspection.VerifyStartedAt);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task VerifyOnlyRunsOnlyWhenAskedFor()
    {
        var source = await _harness.FullBackupAsync();

        var result = await InspectAsync(source, verify: false);

        Assert.True(result.Failure is null, Describe(result));
        Assert.False(result.Inspection!.VerifyOnlyPassed);
        Assert.Null(result.Inspection.VerifyStartedAt);
        Assert.Null(result.Inspection.VerifyFinishedAt);
    }

    [Fact]
    public async Task ACompressedBackupIsReportedAsCompressed()
    {
        var source = await _harness.CompressedBackupAsync();

        var result = await InspectAsync(source);

        Assert.True(result.Failure is null, Describe(result));
        Assert.True(result.Inspection!.Header.IsCompressed);
        Assert.True(result.Inspection.VerifyOnlyPassed);
    }

    [Fact]
    public async Task ABackupWithoutChecksumsIsVerifiedWithoutAskingForThemAndWarns()
    {
        var source = await _harness.PlainBackupAsync();

        var result = await InspectAsync(source);

        Assert.True(result.Failure is null, Describe(result));
        Assert.False(result.Inspection!.Header.HasChecksums);
        Assert.True(result.Inspection.VerifyOnlyPassed);
        Assert.Equal(SqlServerRestoreWarningCodes.BackupWithoutChecksums, Assert.Single(result.Warnings).Code);
    }

    [Fact]
    public async Task AFileThatIsNotABackupIsInvalidAndNothingIsThrown()
    {
        var file = _harness.WriteFile("garbage", System.Text.Encoding.UTF8.GetBytes("this is not a SQL Server backup, it is text"));

        var result = await InspectAsync(file);

        Assert.Null(result.Inspection);
        Assert.Equal(RestoreFailureKind.BackupInvalid, result.Failure!.Kind);
        Assert.Equal(RestoreExecutionStage.InspectingBackup, result.Failure.Stage);
        Assert.Equal(SqlServerRestoreReasons.BackupMediaInvalid, result.Failure.ReasonCode);
        Assert.DoesNotContain(fixture.Staging.ServerDirectory.Value, result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyFileIsInvalidToo()
    {
        var file = _harness.WriteFile("empty", []);

        var result = await InspectAsync(file);

        Assert.Equal(RestoreFailureKind.BackupInvalid, result.Failure!.Kind);
        Assert.Equal(SqlServerRestoreReasons.BackupMediaInvalid, result.Failure.ReasonCode);
    }

    [Fact]
    public async Task AFileTheServerCannotFindIsAStagingProblemOfTheServersAccess()
    {
        var missing = new StagedRestoreSource(string.Empty, "missing.bak", fixture.Staging.ServerDirectory.Combine($"bp_it_rs_missing_{fixture.Suffix}.bak"), "unused", 0);

        var result = await InspectAsync(missing);

        Assert.Equal(RestoreFailureKind.BackupInvalid, result.Failure!.Kind);
        Assert.Equal(SqlServerRestoreReasons.BackupFileUnreadable, result.Failure.ReasonCode);
        Assert.Equal(BackupFailureCause.StagingInvalid, result.Failure.Cause);
        Assert.Contains("service account", result.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Staging.ServerDirectory.Value, result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATruncatedBackupFailsVerificationButItsHeaderStaysReadable()
    {
        var copy = _harness.CopyOf(await _harness.FullBackupAsync(), "truncated");
        using (var stream = new FileStream(copy.AgentPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(20_000);
        }

        var withoutVerify = await InspectAsync(copy, verify: false);
        var withVerify = await InspectAsync(copy, verify: true);

        output.WriteLine($"truncated, no verify: {Describe(withoutVerify)}");
        output.WriteLine($"truncated, verify: {Describe(withVerify)}");
        Assert.Null(withVerify.Inspection);
        Assert.Equal(RestoreFailureKind.BackupInvalid, withVerify.Failure!.Kind);
        Assert.Contains(withVerify.Failure.ReasonCode, new[] { SqlServerRestoreReasons.BackupFileUnreadable, SqlServerRestoreReasons.BackupMediaInvalid });
        Assert.DoesNotContain(fixture.Staging.ServerDirectory.Value, withVerify.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlippedByteInsideTheBackupIsFoundByVerificationWithChecksums()
    {
        var copy = _harness.CopyOf(await _harness.FullBackupAsync(), "flipped");
        using (var stream = new FileStream(copy.AgentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            stream.Position = stream.Length / 2;
            var original = stream.ReadByte();
            stream.Position = stream.Length / 2;
            stream.WriteByte((byte)(original ^ 0xFF));
        }

        var verified = await InspectAsync(copy, verify: true);
        var unverified = await InspectAsync(copy, verify: false);

        output.WriteLine($"flipped, verify: {Describe(verified)}");
        Assert.True(unverified.Failure is null, "The header of a backup with one damaged page is still readable: " + Describe(unverified));
        Assert.Null(verified.Inspection);
        Assert.Equal(RestoreFailureKind.BackupInvalid, verified.Failure!.Kind);
        Assert.Equal(SqlServerRestoreReasons.BackupChecksumMismatch, verified.Failure.ReasonCode);
    }

    [Fact]
    public async Task ADifferentialBackupIsNotSupported()
    {
        await _harness.FullBackupAsync();
        var (server, agent) = _harness.NewServerFile("diff");
        await fixture.ExecuteAsync($"BACKUP DATABASE {MsSqlRestoreHarness.Quote(fixture.Source)} TO DISK = {MsSqlRestoreHarness.Literal(server.Value)} WITH DIFFERENTIAL, INIT");

        var result = await InspectAsync(new StagedRestoreSource(fixture.Source, Path.GetFileName(agent), server, agent, 0));

        Assert.Null(result.Inspection);
        Assert.Equal(RestoreFailureKind.BackupInvalid, result.Failure!.Kind);
        Assert.Equal(RestoreReasonCodes.BackupTypeUnsupported, result.Failure.ReasonCode);
        Assert.Equal(BackupFailureCause.Unsupported, result.Failure.Cause);
        Assert.Contains("differential", result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATransactionLogBackupIsNotSupported()
    {
        await _harness.FullBackupAsync();
        var (server, agent) = _harness.NewServerFile("log");
        await fixture.ExecuteAsync($"BACKUP LOG {MsSqlRestoreHarness.Quote(fixture.Source)} TO DISK = {MsSqlRestoreHarness.Literal(server.Value)} WITH INIT");

        var result = await InspectAsync(new StagedRestoreSource(fixture.Source, Path.GetFileName(agent), server, agent, 0));

        Assert.Equal(RestoreReasonCodes.BackupTypeUnsupported, result.Failure!.ReasonCode);
        Assert.Contains("transaction log", result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWithTwoBackupSetsIsNotSupported()
    {
        var (server, agent) = _harness.NewServerFile("twosets");
        var source = $"{MsSqlRestoreHarness.Quote(fixture.Source)} TO DISK = {MsSqlRestoreHarness.Literal(server.Value)}";
        await fixture.ExecuteAsync($"BACKUP DATABASE {source} WITH INIT; BACKUP DATABASE {source} WITH NOINIT;");

        var result = await InspectAsync(new StagedRestoreSource(fixture.Source, Path.GetFileName(agent), server, agent, 0));

        Assert.Null(result.Inspection);
        Assert.Equal(RestoreReasonCodes.BackupTypeUnsupported, result.Failure!.ReasonCode);
        Assert.Contains("more than one backup set", result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEncryptedBackupFailsAsNotSupported()
    {
        var certificate = $"bp_it_rs_cert_{fixture.Suffix}";
        var (server, agent) = _harness.NewServerFile("encrypted");
        var createdMasterKey = Convert.ToInt32((await fixture.QueryAsync("SELECT COUNT(*) FROM master.sys.symmetric_keys WHERE name = N'##MS_DatabaseMasterKey##'"))[0][0], System.Globalization.CultureInfo.InvariantCulture) == 0;
        try
        {
            if (createdMasterKey)
            {
                await fixture.ExecuteAsync($"USE master; CREATE MASTER KEY ENCRYPTION BY PASSWORD = N'Aa1!{Guid.NewGuid():N}'");
            }

            await fixture.ExecuteAsync($"USE master; CREATE CERTIFICATE [{certificate}] WITH SUBJECT = N'bp restore integration test'");
            await fixture.ExecuteAsync(
                $"BACKUP DATABASE {MsSqlRestoreHarness.Quote(fixture.Source)} TO DISK = {MsSqlRestoreHarness.Literal(server.Value)} "
                + $"WITH INIT, ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = [{certificate}])");

            var result = await InspectAsync(new StagedRestoreSource(fixture.Source, Path.GetFileName(agent), server, agent, 0));

            Assert.Null(result.Inspection);
            Assert.Equal(SqlServerRestoreReasons.BackupEncryptedUnsupported, result.Failure!.ReasonCode);
            Assert.Equal(BackupFailureCause.Unsupported, result.Failure.Cause);
        }
        finally
        {
            await fixture.ExecuteAsync($"USE master; IF EXISTS (SELECT 1 FROM sys.certificates WHERE name = N'{certificate}') DROP CERTIFICATE [{certificate}]");
            if (createdMasterKey)
            {
                await fixture.ExecuteAsync("USE master; IF EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = N'##MS_DatabaseMasterKey##') DROP MASTER KEY");
            }
        }
    }

    [Fact]
    public async Task ALoginThatMayNotRestoreCannotReadBackupsAndIsToldSo()
    {
        var source = await _harness.FullBackupAsync();

        var result = await InspectAsync(source, credential: fixture.Credential(fixture.Operator));

        output.WriteLine(Describe(result));
        Assert.Null(result.Inspection);
        Assert.Equal(RestoreFailureKind.PreCheckFailed, result.Failure!.Kind);
        Assert.Equal(SqlServerRestoreReasons.PermissionDenied, result.Failure.ReasonCode);
        Assert.Equal(BackupFailureCause.PermissionDenied, result.Failure.Cause);
    }

    [Fact]
    public async Task ALoginThatMayRestoreReadsTheSameBackup()
    {
        var source = await _harness.FullBackupAsync();

        var result = await InspectAsync(source, credential: fixture.Credential(fixture.OperatorWithVerify));

        Assert.True(result.Failure is null, Describe(result));
        Assert.True(result.Inspection!.VerifyOnlyPassed);
    }

    [Fact]
    public async Task AWrongPasswordIsAnAuthenticationFailureNotAnException()
    {
        var source = await _harness.FullBackupAsync();
        var wrong = new PasswordDatabaseCredential(fixture.Admin.UserName, new DatabaseSecret("definitely-not-the-password"));

        var result = await InspectAsync(source, credential: wrong);

        Assert.Equal(RestoreFailureKind.PreCheckFailed, result.Failure!.Kind);
        Assert.Equal(SqlServerRestoreReasons.AuthenticationFailed, result.Failure.ReasonCode);
        Assert.Equal(BackupFailureCause.AuthenticationFailed, result.Failure.Cause);
    }

    [Fact]
    public async Task AnUnreachableServerIsADatabaseUnavailableFailureNotAnException()
    {
        var source = await _harness.FullBackupAsync();
        var settings = new DatabaseConnectionSettings(
            new DatabaseEndpoint("127.0.0.1", 1), new DatabaseConnectionOptions(connectTimeout: TimeSpan.FromSeconds(2), requireEncryption: false));

        var result = await _harness.Provider.InspectBackupAsync(settings, fixture.Admin, new RestoreInspectionRequest(source.ServerPath, false), CancellationToken.None);

        Assert.Equal(RestoreFailureKind.PreCheckFailed, result.Failure!.Kind);
        Assert.Equal(BackupFailureCause.DatabaseUnavailable, result.Failure.Cause);
        Assert.Equal(SqlServerRestoreReasons.ServerUnavailable, result.Failure.ReasonCode);
    }

    [Fact]
    public async Task ACancelledTokenSurfacesAsCancellationNotAsAFailure()
    {
        var source = await _harness.FullBackupAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InspectAsync(source, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task APathWithControlCharactersIsAnArgumentErrorBeforeAnythingIsSent()
    {
        var hostile = new StagedRestoreSource(string.Empty, "x", new DatabaseServerPath("/var/opt/mssql/staging/a\nb.bak"), "unused", 0);

        await Assert.ThrowsAsync<ArgumentException>(() => InspectAsync(hostile));
    }

    [Fact]
    public async Task AStagedFileNameWithAQuoteAndASpaceWorksAgainstTheLiveParser()
    {
        var original = await _harness.FullBackupAsync();
        var name = $"bp_it_rs_it's a file_{fixture.Suffix}.bak";
        var target = Path.Combine(fixture.Staging.AgentDirectory.Value, name);
        File.Copy(original.AgentPath, target);
        try
        {
            var result = await InspectAsync(new StagedRestoreSource(fixture.Source, name, fixture.Staging.ServerDirectory.Combine(name), target, 0));

            Assert.True(result.Failure is null, Describe(result));
        }
        finally
        {
            File.Delete(target);
        }
    }
}
