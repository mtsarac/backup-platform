using BackupPlatform.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BackupPlatform.Api.Push;

[Authorize(AuthenticationSchemes = BrowserAuthentication.Scheme)]
public sealed class InAppNotificationHub(InAppNotificationFanout fanout) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (!BrowserAuthentication.TryGetSession(Context.User, out var userId, out var sessionId)
            || !await fanout.RegisterAsync(Context, userId!, sessionId, Context.ConnectionAborted))
        {
            Context.Abort();
            return;
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        fanout.Unregister(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
