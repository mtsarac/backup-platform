using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackupPlatform.SecretProviders.Abstractions;

namespace BackupPlatform.SecretProviders.DataProtection;

/// <summary>Short-lived AWS identity read afresh per request; session tokens are signed as well.</summary>
public sealed record AwsSecretCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken = null);

/// <summary>AWS Secrets Manager JSON API with SigV4; the stored SecretString is already protected ciphertext.</summary>
public sealed class AwsSecretsCiphertextRepository : ISecretCiphertextRepository
{
    private readonly HttpClient http;
    private readonly string region;
    private readonly string prefix;
    private readonly Func<AwsSecretCredentials?> credentials;
    private readonly TimeProvider time;

    public AwsSecretsCiphertextRepository(HttpClient http, string region, string prefix, Func<AwsSecretCredentials?> credentials, TimeProvider? time = null)
    {
        if (!Regex.IsMatch(region, "^[a-z]{2}(-[a-z]+)+-[0-9]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) throw new ArgumentException("Invalid AWS region.", nameof(region));
        if (prefix.Length > 256 || prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '/' and not '_' and not '-')) throw new ArgumentException("Invalid AWS secret prefix.", nameof(prefix));
        this.http = http;
        this.region = region;
        this.prefix = prefix;
        this.credentials = credentials;
        this.time = time ?? TimeProvider.System;
    }

    private string Name(string name) => prefix + new SecretReference(name).Name;

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await SendAsync("GetSecretValue", new { SecretId = Name(name) }, cancellationToken).ConfigureAwait(false);
        if (await IsErrorAsync(response, "ResourceNotFoundException", cancellationToken).ConfigureAwait(false)) return null;
        if (await IsErrorAsync(response, "InvalidRequestException", cancellationToken).ConfigureAwait(false) &&
            await IsScheduledForDeletionAsync(name, cancellationToken).ConfigureAwait(false)) return null;
        Ensure(response);
        try
        {
            using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return json!.RootElement.GetProperty("SecretString").GetString() ?? throw new SecretUnavailableException();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { throw new SecretUnavailableException(); }
    }

    public async Task SetAsync(string name, string ciphertext, CancellationToken cancellationToken)
    {
        using var created = await SendAsync("CreateSecret", new { Name = Name(name), SecretString = ciphertext, ClientRequestToken = Guid.NewGuid().ToString("D") }, cancellationToken).ConfigureAwait(false);
        var exists = await IsErrorAsync(created, "ResourceExistsException", cancellationToken).ConfigureAwait(false);
        if (!exists && await IsErrorAsync(created, "InvalidRequestException", cancellationToken).ConfigureAwait(false) &&
            await IsScheduledForDeletionAsync(name, cancellationToken).ConfigureAwait(false))
        {
            using var restored = await SendAsync("RestoreSecret", new { SecretId = Name(name) }, cancellationToken).ConfigureAwait(false);
            Ensure(restored);
            exists = true;
        }
        if (exists)
        {
            using var updated = await SendAsync("PutSecretValue", new { SecretId = Name(name), SecretString = ciphertext, ClientRequestToken = Guid.NewGuid().ToString("D") }, cancellationToken).ConfigureAwait(false);
            Ensure(updated);
        }
        else Ensure(created);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        if (await GetAsync(name, cancellationToken).ConfigureAwait(false) is null) return false;
        using var response = await SendAsync("DeleteSecret", new { SecretId = Name(name), RecoveryWindowInDays = 7 }, cancellationToken).ConfigureAwait(false);
        if (await IsErrorAsync(response, "ResourceNotFoundException", cancellationToken).ConfigureAwait(false)) return false;
        Ensure(response);
        return true;
    }

    private async Task<bool> IsScheduledForDeletionAsync(string name, CancellationToken token)
    {
        using var response = await SendAsync("DescribeSecret", new { SecretId = Name(name) }, token).ConfigureAwait(false);
        Ensure(response);
        try
        {
            using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: token).ConfigureAwait(false);
            return json!.RootElement.TryGetProperty("DeletedDate", out var date) && date.ValueKind == JsonValueKind.Number;
        }
        catch (JsonException) { throw new SecretUnavailableException(); }
    }

    private async Task<HttpResponseMessage> SendAsync(string action, object payload, CancellationToken cancellationToken)
    {
        var identity = credentials();
        if (identity is null || string.IsNullOrWhiteSpace(identity.AccessKeyId) || string.IsNullOrWhiteSpace(identity.SecretAccessKey)) throw new SecretUnavailableException();
        var body = JsonSerializer.Serialize(payload);
        var now = time.GetUtcNow();
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var timestamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var host = "secretsmanager." + region + (region.StartsWith("cn-", StringComparison.Ordinal) ? ".amazonaws.com.cn" : ".amazonaws.com");
        var target = "secretsmanager." + action;
        var headers = "content-type:application/x-amz-json-1.1\nhost:" + host + "\nx-amz-date:" + timestamp + "\n";
        var signedHeaders = "content-type;host;x-amz-date";
        if (!string.IsNullOrEmpty(identity.SessionToken))
        {
            headers += "x-amz-security-token:" + identity.SessionToken.Trim() + "\n";
            signedHeaders += ";x-amz-security-token";
        }
        headers += "x-amz-target:" + target + "\n";
        signedHeaders += ";x-amz-target";
        var canonical = "POST\n/\n\n" + headers + "\n" + signedHeaders + "\n" + Hash(body);
        var scope = date + "/" + region + "/secretsmanager/aws4_request";
        var toSign = "AWS4-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + Hash(canonical);
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + identity.SecretAccessKey), date);
        key = Hmac(key, region);
        key = Hmac(key, "secretsmanager");
        key = Hmac(key, "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(key, toSign));
        CryptographicOperations.ZeroMemory(key);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://" + host + "/") { Content = new StringContent(body, Encoding.UTF8) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.1");
        request.Headers.Host = host;
        request.Headers.Add("X-Amz-Date", timestamp);
        request.Headers.Add("X-Amz-Target", target);
        if (!string.IsNullOrEmpty(identity.SessionToken)) request.Headers.Add("X-Amz-Security-Token", identity.SessionToken);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("AWS4-HMAC-SHA256", "Credential=" + identity.AccessKeyId + "/" + scope + ", SignedHeaders=" + signedHeaders + ", Signature=" + signature);
        try { return await http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new SecretUnavailableException(); }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
    private static void Ensure(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new SecretUnavailableException();
    }
    private static async Task<bool> IsErrorAsync(HttpResponseMessage response, string kind, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return false;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            return json.RootElement.TryGetProperty("__type", out var type) && type.GetString()?.Split('#').Last() == kind;
        }
        catch (JsonException) { return false; }
    }
}
