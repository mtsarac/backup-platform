using BackupPlatform.Agents.Platform;
using BackupPlatform.Application.Platform;
using BackupPlatform.Infrastructure.Platform;
using BackupPlatform.Api.Backups;
using BackupPlatform.Api.Quota;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.Api.Restores;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Discovery;
using BackupPlatform.Application.Restores;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Discovery;
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
using BackupPlatform.Application.Support;
using BackupPlatform.Api.Support;
using BackupPlatform.Infrastructure.Support;
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
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Secrets;
using BackupPlatform.Infrastructure.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.SecretProviders.DataProtection;
using BackupPlatform.Storage;
using BackupPlatform.Api.Notifications;
using BackupPlatform.Infrastructure.Notifications;
using BackupPlatform.Notifications;
using BackupPlatform.Notifications.Senders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Api.Security;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformSecurity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DbContextOptions<PlatformDbContext>>(CreateDatabaseOptions);
        services.AddSingleton<AgentTransferAdmission>();
        services.AddSingleton<BackupPlatform.Infrastructure.Backups.BackupTransferLeaseService>();
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
        AddSupportAccess(services);
        services.AddScoped<BrowserCookieEvents>();
        AddBrowserAuthentication(services);
        AddAgentSecurity(services);
        AddAgentConnectivity(services);
        services.AddScoped<IDiscoveryStore, PostgreSqlDiscoveryStore>();
        services.AddSingleton<BackupPlatform.Api.Databases.DatabaseCredentialVault>();
        services.AddScoped<DiscoveryService>();
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IConfiguration>().GetSection("Backups").Get<BackupExecutionOptions>() ?? new BackupExecutionOptions();
            options.ThrowIfInvalid();
            return options;
        });
        services.AddScoped<IBackupRunStore, PostgreSqlBackupRunStore>();
        services.AddScoped<IBackupUiStore, PostgreSqlBackupUiStore>();
        services.AddScoped<IBackupHealthStore, PostgreSqlBackupHealthStore>();
        services.AddScoped<BackupHealthEvaluator>();
        services.AddScoped<ITenantWrapKeyStore, PostgreSqlTenantWrapKeyStore>();
        services.AddScoped<ITenantRecoveryKeyStore, PostgreSqlTenantRecoveryKeyStore>();
        services.TryAddSingleton(provider => BackupPlatform.Api.Backups.ArtifactRecoveryConfiguration.Load(provider.GetRequiredService<IConfiguration>()));
        services.AddScoped<IArtifactBreakGlassStore, PostgreSqlArtifactBreakGlassStore>();
        services.AddScoped<ArtifactBreakGlassService>();
        services.AddHostedService<BackupPlatform.Api.Backups.BreakGlassExpiryWorker>();
        services.AddScoped<BackupOrchestrator>();
        services.AddScoped<IUploadBandwidthStore, PostgreSqlUploadBandwidthStore>();
        services.AddScoped<UploadBandwidthService>();
        services.AddHostedService<StaleBackupDetector>();
        services.AddSingleton<BackupRetryWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<BackupRetryWorker>());
        AddBackupRetention(services);
        AddStorageConnections(services);
        AddRestores(services);
        AddNotifications(services);
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
    /// Storage connections: the provider catalog (one descriptor per provider type; adding a provider adds a descriptor and a tester here), the secret
    /// store credentials go through (Data Protection encryption over PostgreSQL ciphertext), the persistence and the service the endpoints use.
    /// Data Protection keeps its host-level defaults unless <c>Secrets:DataProtection:KeysDirectory</c> names a key ring directory (shared across nodes).
    /// </summary>
    private static void AddStorageConnections(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var config = provider.GetRequiredService<IConfiguration>().GetSection("Backups:Sftp");
            var network = new BackupPlatform.StorageProviders.Sftp.SftpNetworkOptions
            {
                AllowedPrivateNetworks = config.GetSection("AllowedPrivateNetworks").Get<string[]>() ?? [],
                ConnectionTimeout = TimeSpan.FromSeconds(config.GetValue<int?>("ConnectionTimeoutSeconds") ?? 15),
                OperationTimeout = TimeSpan.FromSeconds(config.GetValue<int?>("OperationTimeoutSeconds") ?? 30),
                TransferTimeout = TimeSpan.FromSeconds(config.GetValue<int?>("TransferTimeoutSeconds") ?? 3600)
            };
            return new BackupPlatform.Api.Backups.SftpConnectionProviderFactory(network);
        });
        services.AddDataProtection();
        services.AddSingleton<IConfigureOptions<DataProtectionOptions>>(provider => new ConfigureOptions<DataProtectionOptions>(options =>
        {
            var applicationName = provider.GetRequiredService<IConfiguration>()["Secrets:DataProtection:ApplicationName"];
            if (!string.IsNullOrWhiteSpace(applicationName))
                options.ApplicationDiscriminator = applicationName;
        }));
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider => new ConfigureOptions<KeyManagementOptions>(options =>
        {
            var directory = provider.GetRequiredService<IConfiguration>()["Secrets:DataProtection:KeysDirectory"];
            if (!string.IsNullOrWhiteSpace(directory))
            {
                options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(directory), provider.GetRequiredService<ILoggerFactory>());
            }
        }));
        services.TryAddSingleton<IStorageProviderCatalog>(_ => StorageProviderCatalog.BuiltIn());
        services.TryAddSingleton<ISecretCiphertextRepository, PostgreSqlSecretCiphertextRepository>();
        services.TryAddSingleton<ISecretStore, DataProtectionSecretStore>();
        services.TryAddSingleton<ISecretProvider>(provider => provider.GetRequiredService<ISecretStore>());
        services.AddScoped<IStorageConnectionStore, PostgreSqlStorageConnectionStore>();
        services.AddScoped<IStorageConnectionTester>(provider => new CentralConnectionTester(provider.GetRequiredService<IBackupStorageResolver>()));
        services.AddScoped<IStorageConnectionTester>(_ => new AgentSideConnectionTester(StorageProviderTypes.Local));
        services.AddScoped<IStorageConnectionTester>(provider =>
        {
            var factory = provider.GetRequiredService<BackupPlatform.Api.Backups.SftpConnectionProviderFactory>();
            return new SftpConnectionTester((connection, secrets) => factory.Create(connection, secrets));
        });
        services.AddHttpClient();
        services.AddHttpClient("google-drive", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton(provider =>
        {
            var config = provider.GetRequiredService<IConfiguration>().GetSection(BackupPlatform.Api.Backups.GoogleDrivePlatformOptions.SectionName);
            return Microsoft.Extensions.Options.Options.Create(config.Get<BackupPlatform.Api.Backups.GoogleDrivePlatformOptions>() ?? new());
        });
        services.TryAddSingleton<BackupPlatform.Api.Backups.PostgreSqlGoogleDriveStagingStore>();
        services.TryAddSingleton<BackupPlatform.StorageProviders.GoogleDrive.IGoogleDriveStagingStore>(provider =>
            provider.GetRequiredService<BackupPlatform.Api.Backups.PostgreSqlGoogleDriveStagingStore>());
        services.AddHostedService(provider => provider.GetRequiredService<BackupPlatform.Api.Backups.PostgreSqlGoogleDriveStagingStore>());
        services.TryAddSingleton<BackupPlatform.StorageProviders.GoogleDrive.IGoogleDriveFileCatalog>(provider =>
            new BackupPlatform.Api.Backups.EfGoogleDriveFileCatalog(
                provider.GetRequiredService<Microsoft.EntityFrameworkCore.DbContextOptions<PlatformDbContext>>(),
                provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider =>
        {
            var http = provider.GetService<HttpClient>() ?? provider.GetRequiredService<IHttpClientFactory>().CreateClient("google-drive");
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackupPlatform.Api.Backups.GoogleDrivePlatformOptions>>();
            var catalog = provider.GetRequiredService<BackupPlatform.StorageProviders.GoogleDrive.IGoogleDriveFileCatalog>();
            var secretStore = provider.GetRequiredService<ISecretStore>();
            var dbOptions = provider.GetRequiredService<Microsoft.EntityFrameworkCore.DbContextOptions<PlatformDbContext>>();
            return new BackupPlatform.Api.Backups.GoogleDriveConnectionProviderFactory(http, options, catalog, secretStore, dbOptions,
                provider.GetRequiredService<BackupPlatform.StorageProviders.GoogleDrive.IGoogleDriveStagingStore>(),
                provider.GetRequiredService<ILogger<BackupPlatform.Api.Backups.GoogleDriveConnectionProviderFactory>>());
        });
        services.AddScoped(provider =>
        {
            var http = provider.GetService<HttpClient>() ?? provider.GetRequiredService<IHttpClientFactory>().CreateClient("google-drive");
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackupPlatform.Api.Backups.GoogleDrivePlatformOptions>>();
            var dbOptions = provider.GetRequiredService<Microsoft.EntityFrameworkCore.DbContextOptions<PlatformDbContext>>();
            var secretStore = provider.GetRequiredService<ISecretStore>();
            var time = provider.GetRequiredService<TimeProvider>();
            var logger = provider.GetRequiredService<ILogger<BackupPlatform.Api.Backups.GoogleDriveOAuthService>>();
            return new BackupPlatform.Api.Backups.GoogleDriveOAuthService(http, options, dbOptions, secretStore, time, logger);
        });
        services.AddScoped<IStorageConnectionTester>(provider =>
        {
            var factory = provider.GetRequiredService<BackupPlatform.Api.Backups.GoogleDriveConnectionProviderFactory>();
            return new GoogleDriveConnectionTester((connection, secrets) => factory.Create(connection, secrets));
        });
        services.AddScoped<StorageConnectionService>();
        services.AddScoped<BackupPlatform.Storage.IStorageCapacitySnapshotStore, BackupPlatform.Infrastructure.Storage.PostgreSqlStorageCapacitySnapshotStore>();
        services.AddScoped<BackupPlatform.Storage.IStorageCapacityTaskQueue, BackupPlatform.Infrastructure.Storage.PostgreSqlStorageCapacityTaskQueue>();
        services.AddScoped<BackupPlatform.Api.Backups.AgentCapacityProbeService>();
        services.AddScoped<BackupPlatform.Storage.IPlatformStorageCapacityClient, BackupPlatform.Api.Backups.CentralStorageCapacityClient>();
        services.AddScoped<BackupPlatform.Storage.IGoogleDriveCapacityClient>(provider =>
        {
            var http = provider.GetService<HttpClient>() ?? provider.GetRequiredService<IHttpClientFactory>().CreateClient("google-drive");
            var factory = provider.GetRequiredService<BackupPlatform.Api.Backups.GoogleDriveConnectionProviderFactory>();
            var secrets = provider.GetRequiredService<ISecretProvider>();
            return new BackupPlatform.Api.Backups.GoogleDriveCapacityClient(http, factory, secrets);
        });
        services.AddScoped<BackupPlatform.Storage.StorageCapacityProbeService>();
        services.AddSingleton<BackupPlatform.Api.Storage.StorageCapacityPeriodicProbeWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<BackupPlatform.Api.Storage.StorageCapacityPeriodicProbeWorker>());
        services.AddSingleton<BackupPlatform.Application.Storage.IStorageCapacityProbeTrigger>(provider => provider.GetRequiredService<BackupPlatform.Api.Storage.StorageCapacityPeriodicProbeWorker>());
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

    /// <summary>Support access: sessions, the gate every support operation passes, the transparency reads, and the bookkeeping worker. Settings come from <c>Support</c>.</summary>
    private static void AddSupportAccess(IServiceCollection services)
    {
        services.TryAddSingleton(provider => provider.GetRequiredService<IConfiguration>().GetSection("Support").Get<SupportAccessOptions>() ?? new SupportAccessOptions());
        services.TryAddSingleton<ISupportApprovalPolicy, ConfiguredSupportApprovalPolicy>();
        services.AddScoped<ISupportAccessStore, PostgreSqlSupportAccessStore>();
        services.AddScoped<ISupportQueryStore, PostgreSqlSupportQueryStore>();
        services.AddScoped<SupportAccessService>();
        services.AddScoped<SupportRequestGate>();
        services.AddScoped<SupportRestoreDeps>();
        services.AddHostedService<SupportSessionExpiryWorker>();
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
        services.AddScoped<IReplicaRepairStore, PostgreSqlReplicaRepairStore>();
        services.AddScoped<ReplicaRepairService>();
        services.AddSingleton<BackupPlatform.Api.Backups.AutomaticReplicaRepairWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<BackupPlatform.Api.Backups.AutomaticReplicaRepairWorker>());
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
        services.AddScoped<RestoreTargetsReader>();
        services.AddSingleton<StaleRestoreDetector>();
        services.AddHostedService(provider => provider.GetRequiredService<StaleRestoreDetector>());
    }

    /// <summary>
    /// Notifications: operator limits (configuration section <c>Notifications</c>), the persistence, the detectors, the senders and the two background
    /// loops. Nothing here is referenced by backup or restore code; the loops are registered even when disabled and then do nothing.
    /// </summary>
    private static void AddNotifications(IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IConfiguration>().GetSection("Notifications").Get<NotificationOptions>() ?? new NotificationOptions();
            options.ThrowIfInvalid();
            return options;
        });
        services.TryAddSingleton<NotificationNetworkGuard>();
        services.TryAddSingleton<NotificationHttpClient>(provider => new NotificationHttpClient(provider.GetRequiredService<NotificationNetworkGuard>()));
        services.AddScoped<INotificationStore, PostgreSqlNotificationStore>();
        services.AddScoped<INotificationObservationSource, PostgreSqlNotificationObservationSource>();
        services.AddScoped<NotificationEngine>();
        services.AddScoped<NotificationDeliveryProcessor>();
        services.AddScoped<NotificationAdministrationService>();
        services.AddScoped<NotificationTransportService>();
        services.AddSingleton<IRecipientTokens, DataProtectionRecipientTokens>();
        services.AddSingleton<RecipientLinkThrottle>();
        services.AddScoped<NotificationRecipientService>();
        services.AddScoped<INotificationSender, EmailNotificationSender>();
        services.AddScoped<INotificationSender, WebhookNotificationSender>();
        services.AddScoped<INotificationSender, SmsNotificationSender>();
        services.AddSingleton<ISmsProvider, GenericHttpSmsProvider>();
        services.AddSingleton<NotificationDetectionWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<NotificationDetectionWorker>());
        services.AddSingleton<NotificationDeliveryWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<NotificationDeliveryWorker>());
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
