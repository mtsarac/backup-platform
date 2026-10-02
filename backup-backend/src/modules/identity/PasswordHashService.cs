using BackupPlatform.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Identity;

/// <summary>Uses ASP.NET's versioned Identity password hashing primitive, never application-defined cryptography.</summary>
/// <remarks>
/// IdentityV3 uses salted PBKDF2; successful verification of an older format can request rehashing.
/// Unknown identifiers perform dummy verification to reduce timing differences, not to promise constant-time HTTP responses.
/// Plaintext passwords are transient method inputs and must never be logged or stored.
/// </remarks>
public sealed class PasswordHashService
{
    private const int MaximumPasswordLength = 1024;
    private static readonly PasswordHasher<UserId> Hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = 210_000
    }));
    private static readonly UserId DummyUserId = UserId.New();
    private static readonly string DummyHash = Hasher.HashPassword(DummyUserId, Guid.NewGuid().ToString("N"));

    public string Hash(UserId userId, string password)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length > MaximumPasswordLength)
        {
            throw new ArgumentException("A password must not exceed 1024 characters.", nameof(password));
        }
        return Hasher.HashPassword(userId, password);
    }

    public PasswordVerificationResult Verify(UserId userId, string passwordHash, string password)
    {
        ArgumentNullException.ThrowIfNull(userId);
        if (!IsValidPassword(password) || string.IsNullOrWhiteSpace(passwordHash))
        {
            return PasswordVerificationResult.Failed;
        }
        try
        {
            return Hasher.VerifyHashedPassword(userId, passwordHash, password);
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Failed;
        }
    }

    public PasswordVerificationResult VerifyDummy(string password) =>
        Hasher.VerifyHashedPassword(DummyUserId, DummyHash, IsValidPassword(password) ? password : "invalid-password");

    internal static bool IsValidPassword(string? password) =>
        !string.IsNullOrWhiteSpace(password) && password.Length <= MaximumPasswordLength;
}
