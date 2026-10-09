using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Storage;

public sealed class GoogleDriveOAuthState : ITenantOwned
{
    private GoogleDriveOAuthState()
    {
        State = null!;
        TenantId = null!;
        CodeVerifier = null!;
        RedirectUri = null!;
    }

    public GoogleDriveOAuthState(
        string state,
        TenantId tenantId,
        Guid connectionId,
        Guid userId,
        string codeVerifier,
        string redirectUri,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentNullException.ThrowIfNull(tenantId);
        if (connectionId == Guid.Empty) throw new ArgumentException("Connection ID must not be empty.", nameof(connectionId));
        if (userId == Guid.Empty) throw new ArgumentException("User ID must not be empty.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        State = state;
        TenantId = tenantId;
        ConnectionId = connectionId;
        UserId = userId;
        CodeVerifier = codeVerifier;
        RedirectUri = redirectUri;
        CreatedAt = createdAt.ToUniversalTime();
        ExpiresAt = expiresAt.ToUniversalTime();
        Used = false;
    }

    public string State { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public Guid UserId { get; private set; }
    public string CodeVerifier { get; private set; }
    public string RedirectUri { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public bool Used { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now.ToUniversalTime() >= ExpiresAt;

    public void MarkUsed() => Used = true;
}
