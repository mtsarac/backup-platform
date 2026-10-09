using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Support;

namespace BackupPlatform.UnitTests;

public sealed class SupportAccessSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly UserId Admin = UserId.New();
    private static readonly UserId OtherAdmin = UserId.New();
    private const SupportPermissions View = SupportPermissions.ViewBackupMetadata;

    private static SupportAccessSession Open(SupportPermissions permissions = View, TimeSpan? duration = null, string reason = "Ticket 4711") =>
        SupportAccessSession.Open(Guid.NewGuid(), TenantId.New(), Admin, reason, permissions, Now, duration ?? SupportAccessSession.DefaultDuration, "key", "fingerprint");

    [Fact]
    public void WindowIsComputedFromTheGivenClockInstantAndTheReasonIsTrimmed()
    {
        var session = Open(duration: TimeSpan.FromMinutes(90), reason: "  customer lost a database \n");
        Assert.Equal(Now, session.StartedAt);
        Assert.Equal(Now.AddMinutes(90), session.ExpiresAt);
        Assert.Equal("customer lost a database", session.Reason);
        Assert.Equal(SupportSessionStatus.Active, session.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t ")]
    public void ABlankReasonIsRefused(string? reason) => Assert.Throws<ArgumentException>(() => Open(reason: reason!));

    [Fact]
    public void TheReasonIsBoundedAndTheDurationCannotExceedFourHours()
    {
        Assert.Throws<ArgumentException>(() => Open(reason: new string('x', 501)));
        Assert.NotNull(Open(reason: new string('x', 500)));
        Assert.Equal(Now.AddHours(4), Open(duration: TimeSpan.FromHours(4)).ExpiresAt);
        Assert.Throws<ArgumentOutOfRangeException>(() => Open(duration: TimeSpan.FromHours(4) + TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Open(duration: TimeSpan.Zero));
    }

    [Fact]
    public void DownloadAndRestoreNeedMetadataAndNothingOutsideTheKnownSetIsAccepted()
    {
        Assert.Throws<ArgumentException>(() => Open(SupportPermissions.None));
        Assert.Throws<ArgumentException>(() => Open(SupportPermissions.DownloadBackup));
        Assert.Throws<ArgumentException>(() => Open(SupportPermissions.RestoreBackup));
        Assert.Throws<ArgumentException>(() => Open(View | (SupportPermissions)8));
        Assert.NotNull(Open(View | SupportPermissions.DownloadBackup | SupportPermissions.RestoreBackup));
    }

    [Fact]
    public void ExpiryIsJudgedFromTheClockEvenBeforeAnythingRecordsIt()
    {
        var session = Open(duration: TimeSpan.FromMinutes(10));
        Assert.Null(session.Evaluate(Admin, View, Now.AddMinutes(10).AddTicks(-1)));
        Assert.Equal(SupportAccessDenial.SessionExpired, session.Evaluate(Admin, View, Now.AddMinutes(10)));
        Assert.Equal(SupportSessionStatus.Active, session.Status);
        Assert.Equal(SupportSessionStatus.Expired, session.EffectiveStatus(Now.AddMinutes(10)));
        Assert.False(session.IsUsable(Now.AddMinutes(11)));
    }

    [Fact]
    public void OnlyTheOwnerIsAuthorizedAndOnlyForGrantedPermissions()
    {
        var session = Open(View | SupportPermissions.DownloadBackup);
        Assert.Null(session.Evaluate(Admin, SupportPermissions.DownloadBackup, Now));
        Assert.Equal(SupportAccessDenial.NotSessionOwner, session.Evaluate(OtherAdmin, View, Now));
        Assert.Equal(SupportAccessDenial.PermissionMissing, session.Evaluate(Admin, SupportPermissions.RestoreBackup, Now));
        Assert.Equal(SupportAccessDenial.PermissionMissing, session.Evaluate(Admin, View | SupportPermissions.RestoreBackup, Now));
    }

    [Fact]
    public void ClosingAndRevokingEndTheSessionForGoodAndNeverReactivateOrExtendIt()
    {
        var closed = Open();
        closed.Close(Admin, Now.AddMinutes(5));
        Assert.Equal(SupportSessionStatus.Closed, closed.Status);
        Assert.Equal(Admin, closed.ClosedBy);
        Assert.Equal(SupportAccessDenial.SessionClosed, closed.Evaluate(Admin, View, Now.AddMinutes(6)));
        Assert.Throws<InvalidOperationException>(() => closed.Close(Admin, Now.AddMinutes(6)));
        Assert.Throws<InvalidOperationException>(() => closed.Revoke(OtherAdmin, "why", Now.AddMinutes(6)));
        Assert.False(closed.Expire(Now.AddHours(2)));
        Assert.Equal(SupportSessionStatus.Closed, closed.Status);

        var revoked = Open();
        revoked.Revoke(OtherAdmin, " risk review ", Now.AddMinutes(1));
        Assert.Equal(SupportSessionStatus.Revoked, revoked.Status);
        Assert.Equal("risk review", revoked.CloseReason);
        Assert.Equal(SupportAccessDenial.SessionRevoked, revoked.Evaluate(Admin, View, Now.AddMinutes(2)));
        Assert.Throws<ArgumentException>(() => Open().Revoke(OtherAdmin, "  ", Now));
    }

    [Fact]
    public void OnlyTheOwnerCanCloseAndAnEndedOrLapsedSessionCannotBeEnded()
    {
        var session = Open(duration: TimeSpan.FromMinutes(10));
        Assert.Throws<InvalidOperationException>(() => session.Close(OtherAdmin, Now));
        Assert.Throws<InvalidOperationException>(() => session.Close(Admin, Now.AddMinutes(10)));
        Assert.True(session.Expire(Now.AddMinutes(10)));
        Assert.Equal(SupportSessionStatus.Expired, session.Status);
        Assert.Equal(session.ExpiresAt, session.ClosedAt);
        Assert.Null(session.ClosedBy);
        Assert.False(session.Expire(Now.AddMinutes(20)));
        Assert.Throws<InvalidOperationException>(() => session.Revoke(Admin, "late", Now.AddMinutes(20)));
    }

    [Fact]
    public void ASupportRestoreCanOnlyCreateANewDatabase()
    {
        static RestoreOperation Restore(RestoreMode mode, Guid? support, Guid? requester) => new(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(),
            RestoreSourceAccess.CentralStorage, AgentId.New(), new RestoreTarget("sql", 1433, true, null, true, false), "Restored", mode,
            overwriteConfirmed: mode == RestoreMode.ReplaceExistingDatabase, options: null, idempotencyKey: "k", requestFingerprint: "f", requestedByUserId: requester, createdAt: Now,
            supportSessionId: support);

        var support = Guid.NewGuid();
        Assert.Equal(support, Restore(RestoreMode.NewDatabase, support, Guid.NewGuid()).SupportSessionId);
        Assert.Throws<ArgumentException>(() => Restore(RestoreMode.ReplaceExistingDatabase, support, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Restore(RestoreMode.NewDatabase, support, null));
        // The tenant's own Replace is untouched.
        Assert.Null(Restore(RestoreMode.ReplaceExistingDatabase, null, Guid.NewGuid()).SupportSessionId);
    }
}
