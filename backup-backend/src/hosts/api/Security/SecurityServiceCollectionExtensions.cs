using BackupPlatform.Agents.Platform;
using BackupPlatform.Application.Platform;
using BackupPlatform.Infrastructure.Platform;
using BackupPlatform.Api.Backups;
using BackupPlatform.Api.Quota;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.Api.Restores;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Restores;
using BackupPlatform.Api.Downloads;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Infrastructure.Downloads;
using BackupPlatform.Agents;
using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Agents.Credentials;
using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Agents.Management;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Identity;
using BackupPlatform.Api.Push;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Agents;
using BackupPlatform.Infrastructure.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Scheduling;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackupPlatform.Api.Security;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformSecurity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DbContextOptions<PlatformDbContext>>(CreateDatabaseOptions);
        services.AddScoped<ITenantContext>(ResolveTenantContext);
        services.AddScoped<PlatformDbContext>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<IIdentityStore, PostgreSqlIdentityStore>();
        services.AddScoped<IUserAccessStore, PostgreSqlUserAccessStore>();
        services.AddSingleton<PasswordHashService>();
        services.AddScoped<AuthenticationService>();
        services.AddScoped<TenantResolutionService>();
        services.AddScoped<IPlatformAdministrationStore, PostgreSqlPlatformAdministrationStore>();
        services.AddScoped<PlatformAdministrationService>();
        services.AddScoped<IPlatformAgentStore, PostgreSqlPlatformAgentStore>();
        services.AddScoped<PlatformAgentQueryService>();
        services.AddScoped<IManagedServiceStore, PostgreSqlManagedServiceStore>();
        services.AddScoped<ITenantEntitlementReader, PostgreSqlTenantEntitlementReader>();
        services.AddScoped<ManagedServiceAdministrationService>();
        AddQuota(services);
        services.AddScoped<BrowserCookieEvents>();
        AddBrowserAuthentication(services);
        AddAgentSecurity(services);
        AddAgentConnectivity(services);
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IConfiguration>().GetSection("Backups").Get<BackupExecutionOptions>() ?? new BackupExecutionOptions();
            options.ThrowIfInvalid();
            return options;
        });
        services.AddScoped<IBackupRunStore, PostgreSqlBackupRunStore>();
        services.AddScoped<IBackupUiStore, PostgreSqlBackupUiStore>();
        services.AddScoped<BackupOrchestrator>();
        services.AddHostedService<StaleBackupDetector>();
        services.AddSingleton<BackupRetryWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<BackupRetryWorker>());
        AddBackupRetention(services);
        AddRestores(services);
        services.AddScoped<IDownloadStore, PostgreSqlDownloadStore>();
        services.AddScoped<DownloadService>();
        services.AddScoped<DownloadStreamer>();
        services.AddHostedService<DownloadExpiryWorker>();
        services.AddAgentDownloads();
        services.AddSingleton<IBackupWorkSignal, AgentPushWorkSignal>();
        services.AddBackupScheduling();
        AddPolicies(services);
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-BackupPlatform.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.Cookie.Domain = null;
        });
        return services;
    }

    /// <summary>
    /// Quota: the admission gate backup orchestration asks (it sees only <see cref="IBackupAdmissionPolicy"/>), usage reads and recomputation,
    /// policy administration, and the maintenance loop. Operational settings come from <c>ManagedService:Quota</c>; policy values are rows.
    /// </summary>
    private static void AddQuota(IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IConfiguration>().GetSection("ManagedService:Quota").Get<QuotaOptions>() ?? new QuotaOptions();
            options.ThrowIfInvalid();
            return options;
        });
        services.AddScoped<IStorageUsageService, PostgreSqlStorageUsageService>();
        services.AddScoped<IBackupAdmissionPolicy, PostgreSqlBackupAdmissionPolicy>();
        services.AddScoped<IQuotaPolicyStore, PostgreSqlQuotaPolicyStore>();
        services.AddScoped<QuotaPolicyAdministrationService>();
        services.AddSingleton<StorageUsageMaintenanceWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<StorageUsageMaintenanceWorker>());
    }

    private static DbContextOptions<PlatformDbContext> CreateDatabaseOptions(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var connectionString = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = configuration.GetConnectionString("Platform");
        }
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Configure BACKUP_PLATFORM_CONNECTION_STRING or ConnectionStrings:Platform.");
        }
        return new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options;
    }

    /// <summary>
    /// Backend-owned retention: options, the storages the backend may reach, the delete/sweep/prune services and the worker that drives them.
    /// The worker is registered even when disabled; it then does nothing, so the API and its views work either way.
    /// </summary>
    private static void AddBackupRetention(IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IConfiguration>().GetSection("Backups:Retention").Get<BackupRetentionOptions>() ?? new BackupRetentionOptions();
            options.ThrowIfInvalid();
            return options;
        });
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IConfiguration>().GetSection("Backups:Storage").Get<BackupStorageOptions>() ?? new BackupStorageOptions();
            options.ThrowIfInvalid();
            return options;
        });
        // One identity per process: every call to ResolveNodeId without a configured id returns a different value.
        services.TryAddSingleton(provider => new BackupNodeIdentity(provider.GetRequiredService<BackupRetentionOptions>().ResolveNodeId()));
        services.TryAddSingleton<IBackupStorageResolver, ConfiguredBackupStorageResolver>();
        services.AddScoped<IBackupRetentionStore, PostgreSqlBackupRetentionStore>();
        services.AddScoped<IAgentReplicaDeletionStore, PostgreSqlAgentReplicaDeletionStore>();
        services.AddScoped<AgentReplicaDeletionService>();
        services.AddScoped<BackupRetentionService>();
        services.AddScoped<BackupStorageSweeper>();
        services.AddScoped<BackupArtifactService>();
        services.AddSingleton<BackupRetentionWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<BackupRetentionWorker>());
    }

    /// <summary>Restores: policy (configuration section <c>Restores</c>), the store, the service and the sweep that ends restores nobody is working on.</summary>
    private static void AddRestores(IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IConfiguration>().GetSection("Restores").Get<RestoreExecutionOptions>() ?? new RestoreExecutionOptions();
            options.ThrowIfInvalid();
            return options;
        });
        services.AddScoped<IRestoreStore, PostgreSqlRestoreStore>();
        services.AddScoped<RestoreService>();
        services.AddSingleton<StaleRestoreDetector>();
        services.AddHostedService(provider => provider.GetRequiredService<StaleRestoreDetector>());
    }

    private static ITenantContext ResolveTenantContext(IServiceProvider services)
    {
        var context = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (context?.Features.Get<ResolvedTenantContext>() is { } tenantContext)
        {
            return tenantContext;
        }
        if (context?.GetEndpoint()?.Metadata.GetMetadata<TenantScopedEndpoint>() is not null)
        {
            throw new InvalidOperationException("Tenant-scoped services cannot be resolved before membership validation.");
        }
        return UnresolvedTenantContext.Instance;
    }

    private static void AddBrowserAuthentication(IServiceCollection services)
    {
        services.AddAuthentication(BrowserAuthentication.Scheme).AddCookie(BrowserAuthentication.Scheme, options =>
        {
            options.Cookie.Name = BrowserAuthentication.CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.Cookie.Domain = null;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.EventsType = typeof(BrowserCookieEvents);
        }).AddScheme<AgentAuthenticationOptions, AgentAuthenticationHandler>(AgentAuthentication.Scheme, _ => { });
    }

    /// <summary>Agent identity services; deliberately shares no types or storage with user password authentication.</summary>
    private static void AddAgentSecurity(IServiceCollection services)
    {
        services.TryAddSingleton(new AgentSecurityOptions());
        services.AddScoped<IAgentEnrollmentStore, PostgreSqlAgentEnrollmentStore>();
        services.AddScoped<IAgentRegistryStore, PostgreSqlAgentRegistryStore>();
        services.AddScoped<EnrollmentAdministrationService>();
        services.AddScoped<AgentEnrollmentService>();
        services.AddScoped<AgentCredentialService>();
        services.AddScoped<AgentManagementService>();
        services.AddScoped<ICurrentAgent, HttpCurrentAgent>();
    }

    /// <summary>
    /// Heartbeat, polling, push channel, rate limits and liveness queries. The connection registry and push dispatcher
    /// are ephemeral per-process delivery aids; liveness lives in the database.
    /// </summary>
    private static void AddAgentConnectivity(IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IConfiguration>().GetSection("Agents:Connectivity").Get<AgentConnectivityOptions>()
                ?? new AgentConnectivityOptions();
            options.ThrowIfInvalid();
            return options;
        });
        services.AddSingleton<AgentProtocolGate>();
        services.AddScoped<IAgentConnectivityStore, PostgreSqlAgentConnectivityStore>();
        services.AddScoped<IAgentCapabilityStore, PostgreSqlAgentCapabilityStore>();
        services.AddScoped<AgentCapabilityService>();
        services.AddScoped<AgentConnectivityService>();
        services.AddScoped<AgentConnectivityQueryService>();
        services.AddAgentRateLimiting();
        services.AddSingleton<AgentConnectionRegistry>();
        services.AddSingleton<IAgentPushDispatcher, SignalRAgentPushDispatcher>();
        services.AddHostedService<AgentConnectionAuditor>();
        services.AddSignalR(options =>
        {
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(45);
            options.HandshakeTimeout = TimeSpan.FromSeconds(15);
            options.MaximumReceiveMessageSize = 4 * 1024;
            options.EnableDetailedErrors = false;
        });
    }

    private static void AddPolicies(IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            var browserPolicy = new AuthorizationPolicyBuilder(BrowserAuthentication.Scheme).RequireAuthenticatedUser().Build();
            options.DefaultPolicy = browserPolicy;
            options.FallbackPolicy = browserPolicy;
            options.AddPolicy(AuthorizationPolicies.TenantAdministrator, policy =>
                policy.AddAuthenticationSchemes(BrowserAuthentication.Scheme).RequireAuthenticatedUser().AddRequirements(new TenantRoleRequirement(TenantRole.Admin)));
            options.AddPolicy(AuthorizationPolicies.TenantOperator, policy =>
                policy.AddAuthenticationSchemes(BrowserAuthentication.Scheme).RequireAuthenticatedUser().AddRequirements(new TenantRoleRequirement(TenantRole.Operator)));
            options.AddPolicy(AuthorizationPolicies.TenantViewer, policy =>
                policy.AddAuthenticationSchemes(BrowserAuthentication.Scheme).RequireAuthenticatedUser().AddRequirements(new TenantRoleRequirement(TenantRole.Viewer)));
            options.AddPolicy(AuthorizationPolicies.PlatformAdmin, policy =>
                policy.AddAuthenticationSchemes(BrowserAuthentication.Scheme).RequireAuthenticatedUser().AddRequirements(new PlatformAdminRequirement()));
            options.AddPolicy(AuthorizationPolicies.Agent, policy =>
                policy.AddAuthenticationSchemes(AgentAuthentication.Scheme).RequireAuthenticatedUser());
        });
    }
}
