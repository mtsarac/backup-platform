using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string EncodeBase32(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                result.Append(Base32Alphabet[(buffer >> bitsLeft) & 31]);
            }
        }
        if (bitsLeft > 0)
        {
            result.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        }
        return result.ToString();
    }

    public static byte[] DecodeBase32(string input)
    {
        var cleaned = input.Trim().TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>((cleaned.Length * 5) / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in cleaned)
        {
            var val = Base32Alphabet.IndexOf(c);
            if (val < 0) throw new FormatException($"Invalid Base32 character: {c}");
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }
        return output.ToArray();
    }

    public static long CurrentStep(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string ComputeCode(byte[] secret, long step)
    {
        Span<byte> stepBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(stepBytes, step);
        using var hmac = new HMACSHA1(secret);
        Span<byte> hash = stackalloc byte[20];
        hmac.TryComputeHash(stepBytes, hash, out _);
        var offset = hash[^1] & 0x0F;
        var binaryCode = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);
        var code = binaryCode % 1_000_000;
        return code.ToString("D6", CultureInfo.InvariantCulture);
    }

    public static bool VerifyCode(byte[] secret, string? code, DateTimeOffset time, long? lastUsedStep, out long matchedStep)
    {
        matchedStep = 0;
        if (string.IsNullOrWhiteSpace(code)) return false;
        var trimmed = code.Trim();
        if (trimmed.Length != Digits || !int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return false;

        var currentStep = CurrentStep(time);
        for (var step = currentStep - 1; step <= currentStep + 1; step++)
        {
            if (lastUsedStep.HasValue && step <= lastUsedStep.Value)
                continue;

            var expected = ComputeCode(secret, step);
            if (CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(trimmed),
                Encoding.ASCII.GetBytes(expected)))
            {
                matchedStep = step;
                return true;
            }
        }
        return false;
    }

    public static string[] GenerateRecoveryCodes(int count = 10)
    {
        const string chars = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"; // 32 unambiguous chars
        var codes = new string[count];
        var bytes = new byte[12];
        for (var i = 0; i < count; i++)
        {
            RandomNumberGenerator.Fill(bytes);
            var sb = new StringBuilder(14);
            for (var j = 0; j < 12; j++)
            {
                if (j is 4 or 8) sb.Append('-');
                sb.Append(chars[bytes[j] % chars.Length]);
            }
            codes[i] = sb.ToString();
        }
        return codes;
    }

    public static string NormalizeRecoveryCode(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var c in code)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    public static string HashRecoveryCode(UserId userId, string code)
    {
        var normalized = NormalizeRecoveryCode(code);
        var input = Encoding.UTF8.GetBytes($"{userId.Value:D}:{normalized}");
        var hash = SHA256.HashData(input);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
