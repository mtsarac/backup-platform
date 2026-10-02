using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Protocol;
using BackupPlatform.Domain.Agents;

namespace BackupPlatform.Agents.Connectivity;

public enum HeartbeatStatus
{
    Recorded = 1,
    InvalidRequest = 2
}

public sealed record HeartbeatResult(HeartbeatStatus Status, string? InvalidField, DateTimeOffset ServerTime);

public sealed record PollResult(string Cursor, TimeSpan NextPollAfter);

/// <summary>
/// Agent-facing liveness. The caller must already have authenticated the agent and passed the protocol gate; this
/// service trusts only that identity and the server clock, never an agent-supplied identity, time, or "online" claim.
/// </summary>
public sealed class AgentConnectivityService(IAgentConnectivityStore store, TimeProvider timeProvider, AgentConnectivityOptions options)
{
    private const int MaximumVersionLength = 64;
    private const int MaximumCapabilityRevisionLength = 64;
    private const int MaximumRuntimeTextLength = 256;
    private const int MaximumCursorLength = 64;
    /// <summary>The cursor of the only position that exists today: a queue with nothing in it.</summary>
    public const string EmptyCursor = "0";

    public async Task<HeartbeatResult> RecordHeartbeatAsync(
        AgentIdentity caller, ProtocolVersion protocol, AgentHeartbeatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow();
        if (!TryValidate(request, out var platform, out var invalidField))
        {
            return new HeartbeatResult(HeartbeatStatus.InvalidRequest, invalidField, now);
        }
        await store.RecordHeartbeatAsync(new HeartbeatWrite(
            caller.AgentId, caller.TenantId, now, request.AgentVersion.Trim(), protocol.Major, protocol.Minor, platform,
            Normalize(request.Runtime?.OsDescription), Normalize(request.Runtime?.RuntimeVersion), Normalize(request.CapabilityRevision)),
            cancellationToken).ConfigureAwait(false);
        return new HeartbeatResult(HeartbeatStatus.Recorded, null, now);
    }

    /// <summary>Records a push connection as a sign of life (and as the last connection), regardless of write coalescing.</summary>
    public Task NoteConnectedAsync(AgentIdentity caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return store.TouchAsync(caller.AgentId, caller.TenantId, timeProvider.GetUtcNow(), connected: true, TimeSpan.Zero, cancellationToken);
    }

    /// <summary>
    /// Fallback poll. No dispatchable work exists in this iteration, so the answer is always an empty position; the
    /// future dispatcher plugs in here. The poll still counts as a (coalesced) sign of life.
    /// </summary>
    public async Task<PollResult?> PollAsync(AgentIdentity caller, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (cursor is { Length: > MaximumCursorLength } || cursor?.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')) == true)
        {
            return null;
        }
        await store.TouchAsync(caller.AgentId, caller.TenantId, timeProvider.GetUtcNow(), connected: false,
            options.SeenWriteCoalescing, cancellationToken).ConfigureAwait(false);
        return new PollResult(EmptyCursor, options.PollInterval);
    }

    private static bool TryValidate(AgentHeartbeatRequest request, out AgentPlatform platform, out string? invalidField)
    {
        platform = default;
        invalidField = null;
        if (request.AgentVersion is not { Length: > 0 and <= MaximumVersionLength } || !Version.TryParse(request.AgentVersion.Trim(), out _))
        {
            invalidField = nameof(request.AgentVersion);
            return false;
        }
        if (string.Equals(request.Platform, AgentPlatformNames.Windows, StringComparison.OrdinalIgnoreCase))
        {
            platform = AgentPlatform.Windows;
        }
        else if (string.Equals(request.Platform, AgentPlatformNames.Linux, StringComparison.OrdinalIgnoreCase))
        {
            platform = AgentPlatform.Linux;
        }
        else
        {
            invalidField = nameof(request.Platform);
            return false;
        }
        if (request.CapabilityRevision is { } revision
            && (revision.Length is 0 or > MaximumCapabilityRevisionLength || revision.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':'))))
        {
            invalidField = nameof(request.CapabilityRevision);
            return false;
        }
        if (!IsWellFormedText(request.Runtime?.OsDescription) || !IsWellFormedText(request.Runtime?.RuntimeVersion))
        {
            invalidField = nameof(request.Runtime);
            return false;
        }
        return true;
    }

    private static bool IsWellFormedText(string? value) =>
        value is null || (value.Length <= MaximumRuntimeTextLength && !value.Any(char.IsControl));

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
