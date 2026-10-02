using BackupPlatform.Agent.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// A service or scheduled task starts with an arbitrary working directory; configuration must come from the install directory.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddAgentHost(builder.Configuration);

using var host = builder.Build();
await host.RunAsync();
