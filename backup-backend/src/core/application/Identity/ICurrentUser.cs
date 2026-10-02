using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Identity;

/// <summary>Identity established by a trusted authentication scheme, independent of HTTP.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    UserId? UserId { get; }
}
