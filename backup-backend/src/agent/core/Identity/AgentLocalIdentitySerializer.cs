using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupPlatform.Agent.Core.Identity;

/// <summary>
/// Versioned byte representation of <see cref="AgentLocalIdentity"/>, shared by every platform store so that
/// protection (DPAPI, file permissions) is the only per-platform concern. The output contains the plaintext secret
/// and must only be written to protected storage.
/// </summary>
public static class AgentLocalIdentitySerializer
{
    private const int CurrentVersion = 1;

    public static byte[] Serialize(AgentLocalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var document = new IdentityDocument
        {
            Version = CurrentVersion,
            AgentId = identity.AgentId,
            ServerUri = identity.ServerUri.AbsoluteUri,
            InstallationId = identity.InstallationId,
            Current = CredentialDocument.From(identity.Current),
            Pending = identity.Pending is null ? null : CredentialDocument.From(identity.Pending)
        };
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }

    /// <summary>Throws <see cref="AgentCredentialStoreException"/> without echoing content for any malformed or unsupported input.</summary>
    public static AgentLocalIdentity Deserialize(ReadOnlySpan<byte> data)
    {
        try
        {
            var document = JsonSerializer.Deserialize<IdentityDocument>(data)
                ?? throw new AgentCredentialStoreException("The stored agent identity is empty.");
            if (document.Version != CurrentVersion)
            {
                throw new AgentCredentialStoreException("The stored agent identity has an unsupported version.");
            }
            return new AgentLocalIdentity(
                document.AgentId, new Uri(document.ServerUri, UriKind.Absolute), document.InstallationId,
                document.Current.ToCredential(), document.Pending?.ToCredential());
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or UriFormatException or FormatException or InvalidOperationException)
        {
            // The inner exception is deliberately omitted: parser messages can quote input fragments.
            throw new AgentCredentialStoreException("The stored agent identity is malformed.");
        }
    }

    private sealed class IdentityDocument
    {
        [JsonRequired] public int Version { get; init; }
        [JsonRequired] public Guid AgentId { get; init; }
        [JsonRequired] public string ServerUri { get; init; } = string.Empty;
        [JsonRequired] public Guid InstallationId { get; init; }
        [JsonRequired] public CredentialDocument Current { get; init; } = null!;
        public CredentialDocument? Pending { get; init; }
    }

    private sealed class CredentialDocument
    {
        [JsonRequired] public Guid CredentialId { get; init; }
        [JsonRequired] public string Secret { get; init; } = string.Empty;
        [JsonRequired] public int Generation { get; init; }

        public static CredentialDocument From(AgentStoredCredential credential) =>
            new() { CredentialId = credential.CredentialId, Secret = credential.Secret, Generation = credential.Generation };

        public AgentStoredCredential ToCredential() => new(CredentialId, Secret, Generation);
    }
}
