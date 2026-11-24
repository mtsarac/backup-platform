using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host;

internal sealed class AgentLogSender(AgentLogJournal journal, IAgentCredentialStore identities, AgentBackendEndpoint endpoint,
    HttpClient http, ILogger<AgentLogSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(new EventId(900), "Agent started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = await identities.LoadAsync(stoppingToken);
                if (identity is not null)
                {
                    foreach (var path in journal.Pending())
                    {
                        if (!journal.TryClaim(path)) continue;
                        try
                        {
                            var lines = await File.ReadAllLinesAsync(path, stoppingToken);
                            var entries = lines.Select(line => JsonSerializer.Deserialize<AgentLogEntry>(line, JsonSerializerOptions.Web)!).ToArray();
                            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUri, "api/agent/logs"));
                            request.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
                            request.Content = JsonContent.Create(entries, options: JsonSerializerOptions.Web);
                            using var response = await http.SendAsync(request, stoppingToken);
                            if (!response.IsSuccessStatusCode) break;
                            journal.Acknowledge(path);
                        }
                        finally { journal.Release(path); }
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or AgentCredentialStoreException or UnauthorizedAccessException
                || exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
            {
                // The pending segment stays on disk and is retried on the next pass.
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
