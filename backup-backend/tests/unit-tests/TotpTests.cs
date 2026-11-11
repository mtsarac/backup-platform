using System.Security.Cryptography;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Identity;
using Xunit;

namespace BackupPlatform.UnitTests;

public sealed class TotpTests
{
    [Fact]
    public void Base32EncodeAndDecodeRoundtrips()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var encoded = Totp.EncodeBase32(bytes);
        var decoded = Totp.DecodeBase32(encoded);
        Assert.Equal(bytes, decoded);
    }

    [Fact]
    public void TotpGeneratesExpectedCodeAndVerifiesWithinWindow()
    {
        var secret = RandomNumberGenerator.GetBytes(20);
        var now = DateTimeOffset.UtcNow;
        var step = Totp.CurrentStep(now);

        var code = Totp.ComputeCode(secret, step);
        Assert.Equal(6, code.Length);

        // Current time matches
        Assert.True(Totp.VerifyCode(secret, code, now, null, out var matchedStep));
        Assert.Equal(step, matchedStep);

        // Skew -1 step matches
        var past = now.AddSeconds(-Totp.StepSeconds);
        Assert.True(Totp.VerifyCode(secret, code, past.AddSeconds(Totp.StepSeconds * 2), null, out var skewStep));
        Assert.Equal(step, skewStep);

        // Out of skew window (>1 step) fails
        var farFuture = now.AddSeconds(Totp.StepSeconds * 3);
        Assert.False(Totp.VerifyCode(secret, code, farFuture, null, out _));
    }

    [Fact]
    public void TotpRejectsCodeReuseWithinWindow()
    {
        var secret = RandomNumberGenerator.GetBytes(20);
        var now = DateTimeOffset.UtcNow;
        var step = Totp.CurrentStep(now);
        var code = Totp.ComputeCode(secret, step);

        // First verification with no lastUsedStep succeeds
        Assert.True(Totp.VerifyCode(secret, code, now, null, out var matchedStep));
        Assert.Equal(step, matchedStep);

        // Subsequent verification with lastUsedStep >= step is refused!
        Assert.False(Totp.VerifyCode(secret, code, now, matchedStep, out _));

        // Slightly later within the same step still rejected
        Assert.False(Totp.VerifyCode(secret, code, now.AddSeconds(5), matchedStep, out _));
    }

    [Fact]
    public void RecoveryCodeHashingAndNormalization()
    {
        var userId = UserId.New();
        var codes = Totp.GenerateRecoveryCodes(10);
        Assert.Equal(10, codes.Length);

        var first = codes[0];
        var hash1 = Totp.HashRecoveryCode(userId, first);
        var hash2 = Totp.HashRecoveryCode(userId, first.ToLowerInvariant().Replace("-", " "));
        Assert.Equal(hash1, hash2);

        // Different user yields different hash
        var otherUser = UserId.New();
        var hashOther = Totp.HashRecoveryCode(otherUser, first);
        Assert.NotEqual(hash1, hashOther);
    }
}
