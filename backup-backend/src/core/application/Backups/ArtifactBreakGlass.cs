using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

public enum BreakGlassOpenOutcome { Opened, ArtifactNotFound, NotEncrypted, KeyUnavailable }

public sealed record BreakGlassOpenCommand(Guid RequestId, TenantId TenantId, Guid ArtifactId, UserId Requester, string Reason, TimeSpan Ttl, DateTimeOffset Now);

public sealed record BreakGlassOpenResult(BreakGlassOpenOutcome Outcome, ArtifactBreakGlassRequest? Request = null);

public enum BreakGlassDecisionOutcome { Decided, NotFound, Refused }

/// <summary><see cref="Denial"/> is set when <see cref="Outcome"/> is <see cref="BreakGlassDecisionOutcome.Refused"/>.</summary>
public sealed record BreakGlassDecisionResult(BreakGlassDecisionOutcome Outcome, BreakGlassDenial? Denial = null, ArtifactBreakGlassRequest? Request = null);

public sealed record BreakGlassFilter(TenantId? TenantId, BreakGlassStatus? Status, UserId? Requester, int Page, int PageSize, DateTimeOffset Now);

public sealed record BreakGlassRow(ArtifactBreakGlassRequest Request, string? TenantName, string? RequesterName, string? DeciderName);

public sealed record BreakGlassPage<T>(IReadOnlyList<T> Items, long TotalCount, int Page, int PageSize);

/// <summary>A break-glass step was refused by the database's view of the request (it was decided, spent or expired in a race). Carries no key material.</summary>
public sealed class BreakGlassDeniedException(BreakGlassDenial denial) : Exception("The break-glass request cannot be used.")
{
    public BreakGlassDenial Denial { get; } = denial;
}

/// <summary>A request field is invalid. Carries the field and a stable code, never the offending value.</summary>
public sealed class InvalidBreakGlassRequestException(string field, string code) : Exception($"Invalid break-glass request field '{field}' ({code}).")
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}

/// <summary>
/// Break-glass requests and their decisions. Every write is atomic with its audit event. Using an approved request (the single use) happens inside the download
/// store's create transaction, together with the download hold, so spending the grant and holding the artifact cannot disagree.
/// </summary>
public interface IArtifactBreakGlassStore
{
    /// <summary>Checks the artifact exists for the tenant, is encrypted and its key can still decrypt, then inserts the Pending request and its audit.</summary>
    Task<BreakGlassOpenResult> OpenAsync(BreakGlassOpenCommand command, CancellationToken cancellationToken);

    /// <summary>Locks the request, lets the domain judge it (different admin, still Pending, not expired) and writes the decision and its audit in one transaction.</summary>
    Task<BreakGlassDecisionResult> ApproveAsync(Guid requestId, UserId by, DateTimeOffset now, CancellationToken cancellationToken);

    Task<BreakGlassDecisionResult> DenyAsync(Guid requestId, UserId by, string reason, DateTimeOffset now, CancellationToken cancellationToken);

    Task<BreakGlassRow?> GetAsync(Guid requestId, CancellationToken cancellationToken);

    Task<BreakGlassPage<BreakGlassRow>> ListAsync(BreakGlassFilter filter, CancellationToken cancellationToken);

    /// <summary>Records the expiry (and its audit) of Pending/Approved requests past their window. Bookkeeping only: every use is judged against the clock. Returns how many.</summary>
    Task<int> ExpireDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

public sealed record BreakGlassRequestView(Guid Id, Guid TenantId, string? TenantName, Guid ArtifactId, string Status, string Reason, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset ServerNow, Guid RequestedBy, string? RequestedByName, Guid? DecidedBy, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionReason,
    DateTimeOffset? ConsumedAt, Guid? DownloadId, bool IsRequester, bool CanApprove, bool CanDownload);

public sealed record BreakGlassTenantRequestView(Guid Id, Guid ArtifactId, string Status, string Reason, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? RequestedByName,
    string? DecidedByName, DateTimeOffset? DecidedAt, DateTimeOffset? ConsumedAt);

public sealed class ArtifactBreakGlassService(IArtifactBreakGlassStore store, TimeProvider time)
{
    public const int MaximumTtlMinutes = 240;
    private const int MaximumPageSize = 100;

    public DateTimeOffset Now => time.GetUtcNow();

    /// <exception cref="InvalidBreakGlassRequestException">A request field is invalid.</exception>
    public async Task<BreakGlassOpenResult> OpenAsync(Guid? tenantId, Guid? artifactId, UserId requester, string? reason, int? ttlMinutes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requester);
        if (tenantId is not { } tenant || tenant == Guid.Empty) throw new InvalidBreakGlassRequestException("tenantId", "required");
        if (artifactId is not { } artifact || artifact == Guid.Empty) throw new InvalidBreakGlassRequestException("artifactId", "required");
        string trimmed;
        try { trimmed = ArtifactBreakGlassRequest.ValidateReason(reason); }
        catch (ArgumentException)
        {
            throw new InvalidBreakGlassRequestException("reason", string.IsNullOrWhiteSpace(reason) ? "required" : reason.Trim().Length > ArtifactBreakGlassRequest.MaximumReasonLength ? "too_long" : "invalid_value");
        }
        var minutes = ttlMinutes ?? (int)ArtifactBreakGlassRequest.DefaultTtl.TotalMinutes;
        if (minutes < 1) throw new InvalidBreakGlassRequestException("ttlMinutes", "invalid_value");
        if (minutes > MaximumTtlMinutes) throw new InvalidBreakGlassRequestException("ttlMinutes", "too_long");
        return await store.OpenAsync(new BreakGlassOpenCommand(Guid.NewGuid(), new TenantId(tenant), artifact, requester, trimmed, TimeSpan.FromMinutes(minutes), time.GetUtcNow()), cancellationToken);
    }

    public Task<BreakGlassDecisionResult> ApproveAsync(Guid requestId, UserId by, CancellationToken cancellationToken) =>
        store.ApproveAsync(requestId, by, time.GetUtcNow(), cancellationToken);

    /// <exception cref="InvalidBreakGlassRequestException">The reason is blank or too long.</exception>
    public Task<BreakGlassDecisionResult> DenyAsync(Guid requestId, UserId by, string? reason, CancellationToken cancellationToken)
    {
        string trimmed;
        try { trimmed = ArtifactBreakGlassRequest.ValidateReason(reason); }
        catch (ArgumentException)
        {
            throw new InvalidBreakGlassRequestException("reason", string.IsNullOrWhiteSpace(reason) ? "required" : "too_long");
        }
        return store.DenyAsync(requestId, by, trimmed, time.GetUtcNow(), cancellationToken);
    }

    public Task<BreakGlassRow?> GetAsync(Guid requestId, CancellationToken cancellationToken) => store.GetAsync(requestId, cancellationToken);

    public Task<BreakGlassPage<BreakGlassRow>> ListAsync(TenantId? tenantId, BreakGlassStatus? status, UserId? requester, int page, int pageSize, CancellationToken cancellationToken) =>
        store.ListAsync(new BreakGlassFilter(tenantId, status, requester, Math.Max(1, page), Math.Clamp(pageSize, 1, MaximumPageSize), time.GetUtcNow()), cancellationToken);

    public BreakGlassRequestView ToView(BreakGlassRow row, UserId viewer)
    {
        var request = row.Request;
        var now = time.GetUtcNow();
        var status = request.EffectiveStatus(now);
        var isRequester = request.RequestedBy == viewer;
        return new BreakGlassRequestView(request.Id, request.TenantId.Value, row.TenantName, request.ArtifactId, status.ToString(), request.Reason, request.CreatedAt, request.ExpiresAt, now,
            request.RequestedBy.Value, row.RequesterName, request.DecidedBy?.Value, row.DeciderName, request.DecidedAt, request.DecisionReason, request.ConsumedAt, request.DownloadSessionId,
            isRequester, status == BreakGlassStatus.Pending && !isRequester, status == BreakGlassStatus.Approved && isRequester);
    }

    public BreakGlassTenantRequestView ToTenantView(BreakGlassRow row)
    {
        var request = row.Request;
        return new BreakGlassTenantRequestView(request.Id, request.ArtifactId, request.EffectiveStatus(time.GetUtcNow()).ToString(), request.Reason, request.CreatedAt, request.ExpiresAt,
            row.RequesterName, row.DeciderName, request.DecidedAt, request.ConsumedAt);
    }
}
