using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.SecretProviders.Abstractions;

namespace BackupPlatform.SecretProviders.DataProtection;

/// <summary>Vault KV v2 storage for reference-bound Data Protection ciphertext, not plaintext credentials.</summary>
public sealed class VaultCiphertextRepository : ISecretCiphertextRepository
{
    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly string prefix;
    private readonly Func<string?> token;

    public VaultCiphertextRepository(HttpClient http, Uri endpoint, string mount, string prefix, Func<string?> token)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query))
            throw new ArgumentException("Vault requires an HTTPS endpoint.", nameof(endpoint));
        this.http = http;
        this.endpoint = endpoint;
        this.prefix = "v1/" + Segment(mount) + "/data/" + Segment(prefix) + "/";
        this.token = token;
    }

    private static string Segment(string value) => Uri.EscapeDataString(new SecretReference(value).Name);

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, name, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Ensure(response);
        try
        {
            using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return json!.RootElement.GetProperty("data").GetProperty("data").GetProperty("ciphertext").GetString() ?? throw new SecretUnavailableException();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { throw new SecretUnavailableException(); }
    }

    public async Task SetAsync(string name, string ciphertext, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, name, new { data = new { ciphertext } }, cancellationToken).ConfigureAwait(false);
        Ensure(response);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        if (await GetAsync(name, cancellationToken).ConfigureAwait(false) is null) return false;
        using var response = await SendAsync(HttpMethod.Delete, name, null, cancellationToken).ConfigureAwait(false);
        Ensure(response);
        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string name, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint, prefix + Segment(name)));
        var credential = token();
        if (string.IsNullOrWhiteSpace(credential)) throw new SecretUnavailableException();
        request.Headers.Add("X-Vault-Token", credential);
        if (body is not null) request.Content = JsonContent.Create(body);
        try { return await http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new SecretUnavailableException(); }
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new SecretUnavailableException();
    }
}
