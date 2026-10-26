using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (await AgentUpdateCommands.TryRunAsync(args)) return;

if (args.FirstOrDefault() == "enroll")
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    Environment.ExitCode = await AgentEnrollmentCommand.RunAsync(args, cancellation.Token);
    return;
}

if (args.FirstOrDefault() == "certificate")
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    Environment.ExitCode = await AgentCertificateCommand.RunAsync(args, cancellation.Token);
    return;
}

#if AGENT_PLATFORM_WINDOWS
if (args.FirstOrDefault() == "--service-enroll")
{
    if (args.Length != 3) { Console.Error.WriteLine("Use the Windows Service installer to provision this agent."); Environment.ExitCode = 1; return; }
    var bootstrap = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
    bootstrap.Services.AddAgentPlatformHosting();
    bootstrap.Services.AddSingleton<IHostedService>(provider =>
        new WindowsEnrollmentWorker(args[1], args[2], provider.GetRequiredService<IHostApplicationLifetime>()));
    using var serviceHost = bootstrap.Build();
    await serviceHost.RunAsync();
    return;
}
#endif

// A service starts with an arbitrary working directory; configuration must come from the install directory.
string? storeOverride = null;
if (args.Length == 2 && args[0] == "--store-directory")
{
    storeOverride = args[1];
    args = [];
}
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
AgentUpdateCommands.ConfigureRuntime(builder.Configuration);
if (storeOverride is not null) { builder.Configuration["Agent:CredentialStoreDirectory"] = storeOverride; }
FileStream? identityLease = null;
try
{
    var directory = AgentCredentialStoreFactory.ResolveDirectory(builder.Configuration["Agent:CredentialStoreDirectory"]);
    identityLease = AgentEnrollmentLease.Acquire(directory);
    var identity = await AgentCredentialStoreFactory.Create(directory).LoadAsync(CancellationToken.None);
    if (identity is not null) { builder.Configuration["Agent:Backend:BaseUri"] = identity.ServerUri.AbsoluteUri; }
}
catch (Exception exception) when (exception is AgentEnrollmentException or AgentCredentialStoreException or ArgumentException or IOException or UnauthorizedAccessException)
{
    identityLease?.Dispose();
    Console.Error.WriteLine(AgentEnrollmentCommand.SafeError(exception));
    Environment.ExitCode = 1;
    return;
}
using var runtimeLease = identityLease;
builder.Services.AddAgentHost(builder.Configuration);
builder.Services.AddAgentPlatformHosting();
using var host = builder.Build();
await host.RunAsync();
