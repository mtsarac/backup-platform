using BackupPlatform.Application.Identity;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Api.Security;

public sealed class HttpCurrentUser : ICurrentUser
{
    public HttpCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        IsAuthenticated = BrowserAuthentication.TryGetSession(httpContextAccessor.HttpContext?.User, out var userId, out _);
        UserId = userId;
    }

    public bool IsAuthenticated { get; }
    public UserId? UserId { get; }
}
