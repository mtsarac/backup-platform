using System.Text.Json;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Agents.Platform;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Tenancy;

namespace BackupPlatform.Api.Endpoints;

public sealed class PlatformTenantRequest
{
    public string? Name { get; init; }
}

public sealed class PlatformUserRequest
{
    public string? LoginIdentifier { get; init; }
    public string? DisplayName { get; init; }
    public string? Password { get; init; }
}

public sealed class PlatformMembershipRequest
{
    public Guid UserId { get; init; }
    public string? Role { get; init; }
}

public sealed class PlatformMembershipRoleRequest
{
    public string? Role { get; init; }
}

/// <summary>
/// Platform administration. Every route requires the PlatformAdmin policy and none is tenant-scoped: the tenant
/// resolution middleware never runs for them, so a tenant Owner/Admin membership grants nothing here and
/// PlatformAdmin grants no tenant role. Reads and writes use explicit platform operations; writes are audited.
/// </summary>
public static class PlatformEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;

    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var platform = endpoints.MapGroup("/api/platform").RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        platform.MapGet("/summary", GetSummaryAsync);
        platform.MapGet("/tenants", ListTenantsAsync);
        platform.MapPost("/tenants", CreateTenantAsync);
        platform.MapGet("/tenants/{tenantId:guid}", GetTenantAsync);
        platform.MapPut("/tenants/{tenantId:guid}", RenameTenantAsync);
        platform.MapGet("/tenants/{tenantId:guid}/memberships", ListMembershipsAsync);
        platform.MapPost("/tenants/{tenantId:guid}/memberships", AddMembershipAsync);
        platform.MapPut("/tenants/{tenantId:guid}/memberships/{userId:guid}", ChangeMembershipRoleAsync);
        platform.MapDelete("/tenants/{tenantId:guid}/memberships/{userId:guid}", RemoveMembershipAsync);
        platform.MapGet("/tenants/{tenantId:guid}/agents/{agentId:guid}", GetAgentAsync);
        platform.MapGet("/users", ListUsersAsync);
        platform.MapPost("/users", CreateUserAsync);
        platform.MapGet("/agents", ListAgentsAsync);
        platform.MapGet("/audit", ListAuditAsync);
        return endpoints;
    }

    private static async Task<IResult> GetSummaryAsync(
        IPlatformAdministrationStore store, PlatformAgentQueryService agents, TimeProvider time, CancellationToken cancellationToken)
    {
        var counts = await store.GetCountsAsync(time.GetUtcNow(), cancellationToken);
        var summary = await agents.SummarizeAsync(null, cancellationToken);
        return Results.Ok(new
        {
            tenants = counts.Tenants,
            users = counts.Users,
            agents = ToResponse(summary),
            enabledBackupJobs = counts.EnabledBackupJobs,
            failedRunsLast24Hours = counts.FailedRunsLast24Hours
        });
    }

    private static async Task<IResult> ListTenantsAsync(
        string? search, int? page, int? pageSize, IPlatformAdministrationStore store, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size))
        {
            return InvalidPaging();
        }
        var result = await store.ListTenantsAsync(search, p, size, cancellationToken);
        return Results.Ok(Page(result, ToResponse));
    }

    private static async Task<IResult> CreateTenantAsync(
        PlatformTenantRequest? request, ICurrentUser currentUser, PlatformAdministrationService service, CancellationToken cancellationToken)
    {
        try
        {
            var tenant = await service.CreateTenantAsync(request?.Name, currentUser.UserId!, cancellationToken);
            return Results.Json(ToResponse(tenant), statusCode: StatusCodes.Status201Created);
        }
        catch (InvalidPlatformRequestException exception)
        {
            return Invalid(exception);
        }
    }

    private static async Task<IResult> GetTenantAsync(
        Guid tenantId, IPlatformAdministrationStore store, PlatformAgentQueryService agents, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            return TenantNotFound();
        }
        var id = new TenantId(tenantId);
        var tenant = await store.GetTenantAsync(id, cancellationToken);
        if (tenant is null)
        {
            return TenantNotFound();
        }
        var summary = await agents.SummarizeAsync(id, cancellationToken);
        return Results.Ok(new
        {
            tenantId = tenant.TenantId.Value,
            name = tenant.Name,
            memberCount = tenant.MemberCount,
            enabledBackupJobs = tenant.EnabledBackupJobs,
            agents = ToResponse(summary),
            enrollment = new { enabled = tenant.EnrollmentEnabled, changedAt = tenant.EnrollmentChangedAt }
        });
    }

    private static async Task<IResult> RenameTenantAsync(
        Guid tenantId, PlatformTenantRequest? request, ICurrentUser currentUser, PlatformAdministrationService service, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            return TenantNotFound();
        }
        try
        {
            var change = await service.RenameTenantAsync(new TenantId(tenantId), request?.Name, currentUser.UserId!, cancellationToken);
            return change.Outcome == PlatformChangeOutcome.TenantNotFound ? TenantNotFound() : Results.Ok(ToResponse(change.Value!));
        }
        catch (InvalidPlatformRequestException exception)
        {
            return Invalid(exception);
        }
    }

    private static async Task<IResult> ListMembershipsAsync(
        Guid tenantId, int? page, int? pageSize, IPlatformAdministrationStore store, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size))
        {
            return InvalidPaging();
        }
        if (tenantId == Guid.Empty)
        {
            return TenantNotFound();
        }
        var result = await store.ListMembershipsAsync(new TenantId(tenantId), p, size, cancellationToken);
        return result is null ? TenantNotFound() : Results.Ok(Page(result, ToResponse));
    }

    private static async Task<IResult> AddMembershipAsync(
        Guid tenantId, PlatformMembershipRequest? request, ICurrentUser currentUser, PlatformAdministrationService service, CancellationToken cancellationToken)
    {
        if (request is null || request.UserId == Guid.Empty)
        {
            return Invalid(new InvalidPlatformRequestException("userId", "required"));
        }
        if (!TryParseRole(request.Role, out var role))
        {
            return Invalid(new InvalidPlatformRequestException("role", "invalid_value"));
        }
        if (tenantId == Guid.Empty)
        {
            return TenantNotFound();
        }
        var change = await service.AddMembershipAsync(new TenantId(tenantId), new UserId(request.UserId), role, currentUser.UserId!, cancellationToken);
        return change.Outcome switch
        {
            PlatformChangeOutcome.TenantNotFound => TenantNotFound(),
            PlatformChangeOutcome.UserNotFound => Results.NotFound(new { error = "user_not_found" }),
            PlatformChangeOutcome.AlreadyMember => Results.Conflict(new { error = "already_member" }),
            _ => Results.Json(ToResponse(change.Value!), statusCode: StatusCodes.Status201Created)
        };
    }

    private static async Task<IResult> ChangeMembershipRoleAsync(
        Guid tenantId, Guid userId, PlatformMembershipRoleRequest? request, ICurrentUser currentUser,
        PlatformAdministrationService service, CancellationToken cancellationToken)
    {
        if (!TryParseRole(request?.Role, out var role))
        {
            return Invalid(new InvalidPlatformRequestException("role", "invalid_value"));
        }
        if (tenantId == Guid.Empty || userId == Guid.Empty)
        {
            return MembershipNotFound();
        }
        var change = await service.ChangeMembershipRoleAsync(new TenantId(tenantId), new UserId(userId), role, currentUser.UserId!, cancellationToken);
        return change.Outcome == PlatformChangeOutcome.MembershipNotFound ? MembershipNotFound() : Results.Ok(ToResponse(change.Value!));
    }

    private static async Task<IResult> RemoveMembershipAsync(
        Guid tenantId, Guid userId, ICurrentUser currentUser, PlatformAdministrationService service, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
        {
            return MembershipNotFound();
        }
        var change = await service.RemoveMembershipAsync(new TenantId(tenantId), new UserId(userId), currentUser.UserId!, cancellationToken);
        return change.Outcome == PlatformChangeOutcome.MembershipNotFound ? MembershipNotFound() : Results.NoContent();
    }

    private static async Task<IResult> ListUsersAsync(
        string? search, int? page, int? pageSize, IPlatformAdministrationStore store, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size))
        {
            return InvalidPaging();
        }
        var result = await store.ListUsersAsync(search, p, size, cancellationToken);
        return Results.Ok(Page(result, ToResponse));
    }

    private static async Task<IResult> CreateUserAsync(
        PlatformUserRequest? request, ICurrentUser currentUser, PlatformAdministrationService service, CancellationToken cancellationToken)
    {
        try
        {
            var change = await service.CreateUserAsync(request?.LoginIdentifier, request?.DisplayName, request?.Password, currentUser.UserId!, cancellationToken);
            return change.Outcome == PlatformChangeOutcome.LoginTaken
                ? Results.Conflict(new { error = "login_taken" })
                : Results.Json(ToResponse(change.Value!), statusCode: StatusCodes.Status201Created);
        }
        catch (InvalidPlatformRequestException exception)
        {
            return Invalid(exception);
        }
    }

    private static async Task<IResult> ListAgentsAsync(
        Guid? tenantId, string? connectivity, string? status, string? search, int? page, int? pageSize,
        PlatformAgentQueryService agents, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size))
        {
            return InvalidPaging();
        }
        var connectivityFilter = AgentConnectivityFilter.All;
        if (!string.IsNullOrEmpty(connectivity)
            && !(Enum.TryParse(connectivity, ignoreCase: true, out connectivityFilter) && connectivityFilter != AgentConnectivityFilter.All && Enum.IsDefined(connectivityFilter)))
        {
            return Invalid(new InvalidPlatformRequestException("connectivity", "invalid_value"));
        }
        AgentStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<AgentStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || status.All(char.IsDigit))
            {
                return Invalid(new InvalidPlatformRequestException("status", "invalid_value"));
            }
            statusFilter = parsed;
        }
        if (tenantId == Guid.Empty)
        {
            return Invalid(new InvalidPlatformRequestException("tenantId", "invalid_value"));
        }
        var filter = new PlatformAgentFilter(tenantId is null ? null : new TenantId(tenantId.Value), connectivityFilter, statusFilter, search);
        var result = await agents.ListAsync(filter, p, size, cancellationToken);
        return Results.Ok(Page(result, view => ToResponse(view.Agent.Agent.TenantId.Value, view.TenantName, view.Agent, view.CapabilityCount, view.CapabilityRevision, view.CapabilitiesReportedAt)));
    }

    private static async Task<IResult> GetAgentAsync(
        Guid tenantId, Guid agentId, IPlatformAdministrationStore store, AgentConnectivityQueryService connectivity, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || agentId == Guid.Empty)
        {
            return Results.NotFound(new { error = "agent_not_found" });
        }
        var id = new TenantId(tenantId);
        var tenant = await store.GetTenantAsync(id, cancellationToken);
        var view = tenant is null ? null : await connectivity.GetAsync(id, new AgentId(agentId), cancellationToken);
        if (tenant is null || view is null)
        {
            return Results.NotFound(new { error = "agent_not_found" });
        }
        var capabilities = view.Capabilities;
        return Results.Ok(new
        {
            tenantId = tenantId,
            tenantName = tenant.Name,
            agentId = view.Agent.Id.Value,
            hostName = view.Agent.HostName,
            status = view.Agent.Status.ToString(),
            connectivity = view.Connectivity.ToString().ToLowerInvariant(),
            operationalStatus = view.Status.ToString(),
            platform = view.Agent.Software.Platform.ToString(),
            osDescription = view.OsDescription,
            agentVersion = view.ReportedAgentVersion,
            protocolVersion = view.ReportedProtocolVersion,
            capabilityCount = capabilities?.Capabilities.Count ?? 0,
            capabilityRevision = capabilities?.Revision,
            capabilitiesReportedAt = capabilities?.ReportedAt,
            lastSeenAt = view.LastSeenAt,
            enrolledAt = view.EnrolledAt,
            lastHeartbeatAt = view.LastHeartbeatAt,
            lastConnectedAt = view.LastConnectedAt,
            runtimeVersion = view.RuntimeVersion,
            capabilities = (capabilities?.Capabilities ?? []).Select(capability => new
            {
                identifier = capability.Identifier,
                support = capability.Support.ToString(),
                metadata = capability.Metadata
            })
        });
    }

    private static async Task<IResult> ListAuditAsync(
        Guid? tenantId, int? page, int? pageSize, IPlatformAdministrationStore store, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size))
        {
            return InvalidPaging();
        }
        if (tenantId == Guid.Empty)
        {
            return Invalid(new InvalidPlatformRequestException("tenantId", "invalid_value"));
        }
        var result = await store.ListAuditAsync(tenantId is null ? null : new TenantId(tenantId.Value), p, size, cancellationToken);
        return Results.Ok(Page(result, item => new
        {
            id = item.Id,
            at = item.At,
            actorUserId = item.ActorUserId.Value,
            actorDisplayName = item.ActorDisplayName,
            actorKind = item.ActorKind.ToString(),
            action = item.Action,
            tenantId = item.TenantId?.Value,
            tenantName = item.TenantName,
            targetType = item.TargetType,
            targetId = item.TargetId,
            details = JsonSerializer.Deserialize<JsonElement>(item.DetailsJson)
        }));
    }

    private static bool TryPaging(int? page, int? pageSize, out int p, out int size)
    {
        p = page ?? 1;
        size = pageSize ?? DefaultPageSize;
        return p is >= 1 and <= 1_000_000 && size is >= 1 and <= MaximumPageSize;
    }

    private static bool TryParseRole(string? value, out TenantRole role)
    {
        role = default;
        return !string.IsNullOrEmpty(value) && value.All(char.IsLetter)
            && Enum.TryParse(value, ignoreCase: true, out role) && Enum.IsDefined(role);
    }

    private static object Page<TSource, TResult>(PagedResult<TSource> result, Func<TSource, TResult> map) => new
    {
        items = result.Items.Select(map).ToList(),
        totalCount = result.TotalCount,
        page = result.Page,
        pageSize = result.PageSize
    };

    private static object ToResponse(PlatformTenantItem tenant) => new
    {
        tenantId = tenant.TenantId.Value,
        name = tenant.Name,
        memberCount = tenant.MemberCount,
        agentCount = tenant.AgentCount,
        enrollmentEnabled = tenant.EnrollmentEnabled
    };

    private static object ToResponse(PlatformUserItem user) => new
    {
        userId = user.UserId.Value,
        displayName = user.DisplayName,
        loginIdentifier = user.LoginIdentifier,
        isPlatformAdmin = user.IsPlatformAdmin,
        membershipCount = user.MembershipCount
    };

    private static object ToResponse(PlatformMembershipItem membership) => new
    {
        userId = membership.UserId.Value,
        displayName = membership.DisplayName,
        loginIdentifier = membership.LoginIdentifier,
        role = membership.Role.ToString()
    };

    private static object ToResponse(AgentConnectivitySummary summary) => new
    {
        total = summary.Total,
        online = summary.Online,
        offline = summary.Offline,
        unknown = summary.Unknown,
        disabled = summary.Disabled,
        revoked = summary.Revoked
    };

    private static object ToResponse(
        Guid tenantId, string tenantName, AgentStatusView view, int capabilityCount, string? capabilityRevision, DateTimeOffset? capabilitiesReportedAt) => new
    {
        tenantId,
        tenantName,
        agentId = view.Agent.Id.Value,
        hostName = view.Agent.HostName,
        status = view.Agent.Status.ToString(),
        connectivity = view.Connectivity.ToString().ToLowerInvariant(),
        operationalStatus = view.Status.ToString(),
        platform = view.Agent.Software.Platform.ToString(),
        osDescription = view.OsDescription,
        agentVersion = view.ReportedAgentVersion,
        protocolVersion = view.ReportedProtocolVersion,
        capabilityCount,
        capabilityRevision,
        capabilitiesReportedAt,
        lastSeenAt = view.LastSeenAt,
        enrolledAt = view.EnrolledAt
    };

    private static IResult TenantNotFound() => Results.NotFound(new { error = "tenant_not_found" });

    private static IResult MembershipNotFound() => Results.NotFound(new { error = "membership_not_found" });

    private static IResult InvalidPaging() => Results.BadRequest(new { error = "invalid_request", field = "page", code = "invalid_value" });

    private static IResult Invalid(InvalidPlatformRequestException exception) =>
        Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code });
}
