using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public enum AccountMailKind { Verification, PasswordReset }

public sealed record AccountMail(string To, AccountMailKind Kind, string Token);

/// <summary>Platform email delivery for account mail. Not available when no SMTP relay (or public base URL for the links) is configured.</summary>
public interface IAccountMailer
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
    void Enqueue(AccountMail mail);
}

public enum PasswordResetResult { Completed, InvalidToken, InvalidPassword }

/// <summary>Optional email verification and self-service password reset. Reset only ever reaches a verified address.</summary>
public sealed class AccountEmailService(IIdentityStore store, IAccountMailer mailer, PasswordHashService passwordHasher, TimeProvider time)
{
    public static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(30);

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => mailer.IsAvailableAsync(cancellationToken);

    /// <summary>Sends a reset message when the account exists and its address is verified; otherwise does nothing. The caller answers identically either way.</summary>
    public async Task RequestPasswordResetAsync(string? identifier, CancellationToken cancellationToken)
    {
        string? normalized;
        try
        {
            normalized = identifier?.Contains('@') == true
                ? LoginIdentifierNormalizer.NormalizeEmail(identifier)
                : LoginIdentifierNormalizer.TryNormalize(identifier, out var login) ? login : null;
        }
        catch (ArgumentException) { return; }
        if (normalized is null || await store.FindCredentialAsync(normalized, cancellationToken) is not { Email: not null, EmailVerifiedAt: not null } credential) return;
        await IssueAsync(credential, AccountEmailPurpose.PasswordReset, ResetLifetime, cancellationToken);
    }

    /// <summary>False when the account has no address or it is already verified.</summary>
    public async Task<bool> SendVerificationAsync(UserId userId, CancellationToken cancellationToken)
    {
        if (await store.FindCredentialByUserIdAsync(userId, cancellationToken) is not { Email: not null, EmailVerifiedAt: null } credential) return false;
        await IssueAsync(credential, AccountEmailPurpose.Verification, VerificationLifetime, cancellationToken);
        return true;
    }

    public Task<bool> ConfirmEmailAsync(string? token, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(token) ? Task.FromResult(false) : store.ConfirmEmailAsync(Hash(token), time.GetUtcNow(), cancellationToken);

    public async Task<(PasswordResetResult Result, string? FailureReason)> CompletePasswordResetAsync(string? token, string? newPassword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return (PasswordResetResult.InvalidToken, null);
        var hash = Hash(token);
        if (await store.FindUsableEmailTokenAsync(hash, AccountEmailPurpose.PasswordReset, time.GetUtcNow(), cancellationToken) is not { } userId) return (PasswordResetResult.InvalidToken, null);
        if (!PasswordPolicy.Validate(newPassword, out var failureReason)) return (PasswordResetResult.InvalidPassword, failureReason);
        var completed = await store.CompletePasswordResetAsync(hash, passwordHasher.Hash(userId, newPassword!), time.GetUtcNow(), cancellationToken);
        return (completed ? PasswordResetResult.Completed : PasswordResetResult.InvalidToken, null);
    }

    private async Task IssueAsync(PasswordCredential credential, AccountEmailPurpose purpose, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        await store.IssueEmailTokenAsync(new AccountEmailToken(Guid.NewGuid(), credential.UserId, purpose, Hash(secret), credential.NormalizedEmail!, now, now + lifetime), cancellationToken);
        mailer.Enqueue(new AccountMail(credential.Email!, purpose == AccountEmailPurpose.PasswordReset ? AccountMailKind.PasswordReset : AccountMailKind.Verification, secret));
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
