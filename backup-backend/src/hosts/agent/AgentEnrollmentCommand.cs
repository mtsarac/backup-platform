using BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.Agent.Host;

internal static class AgentEnrollmentCommand
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        AgentText.ConfigureFrom(args);
        try
        {
            var options = Parse(args);
            var server = options.GetValueOrDefault("--server");
            var environmentToken = Environment.GetEnvironmentVariable("BACKUP_AGENT_ENROLLMENT_TOKEN");
            Environment.SetEnvironmentVariable("BACKUP_AGENT_ENROLLMENT_TOKEN", null);
            var token = options.GetValueOrDefault("--token") ?? environmentToken;
            var name = options.GetValueOrDefault("--name");
            var interactive = !options.ContainsKey("--non-interactive");
            if (interactive && server is null)
            {
                Console.Write(AgentText.Get(AgentMessage.PromptServer));
                server = Console.ReadLine();
            }
            var uri = AgentEnrollment.ValidateServer(server);
            if (interactive && token is null)
            {
                token = ReadToken();
            }
            AgentEnrollment.ValidateInput(token ?? string.Empty, name);
            var directory = AgentCredentialStoreFactory.ResolveDirectory(options.GetValueOrDefault("--store-directory")
                ?? Environment.GetEnvironmentVariable("Agent__CredentialStoreDirectory"));
#if AGENT_PLATFORM_WINDOWS
            if (!options.ContainsKey("--console"))
            {
                return await WindowsEnrollmentCommand.RunInstallerAsync(new WindowsEnrollmentCommand.Input(
                    uri.AbsoluteUri, token!, name, directory), cancellationToken).ConfigureAwait(false);
            }
#endif
            using var lease = AgentEnrollmentLease.Acquire(directory);
            var store = AgentCredentialStoreFactory.Create(directory);
            using var http = CreateHttpClient();
            var identity = await new AgentEnrollment(http, store).EnrollAsync(uri, token!, name, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(AgentText.Format(AgentMessage.EnrollmentVerified, identity.AgentId));
            return 0;
        }
        catch (Exception exception) when (exception is AgentEnrollmentException or AgentCredentialStoreException or ArgumentException
            or UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine(SafeError(exception));
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(AgentText.Get(AgentMessage.EnrollmentCancelled));
            return 1;
        }
    }

    internal static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    internal static string SafeError(Exception exception) => exception switch
    {
        AgentEnrollmentException or AgentCredentialStoreException => exception.Message,
        _ => AgentText.Get(AgentMessage.StoreUnusable)
    };

    internal static Dictionary<string, string?> Parse(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var key = args[index];
            if (key is "--non-interactive" or "--console")
            {
                if (!options.TryAdd(key, null)) { throw new AgentEnrollmentException(AgentText.Get(AgentMessage.DuplicateOption)); }
                continue;
            }
            if (key is not ("--server" or "--token" or "--name" or "--store-directory" or "--language") || index + 1 == args.Length
                || args[index + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(key, args[++index]))
            {
                throw new AgentEnrollmentException(AgentText.Get(AgentMessage.EnrollUsage));
            }
            if (key == "--language" && options[key] is not ("tr" or "en"))
            {
                throw new AgentEnrollmentException(AgentText.Get(AgentMessage.EnrollUsage));
            }
        }
        return options;
    }

    private static string ReadToken()
    {
        if (Console.IsInputRedirected)
        {
            throw new AgentEnrollmentException(AgentText.Get(AgentMessage.TokenPromptNeedsTerminal));
        }
        Console.Write(AgentText.Get(AgentMessage.PromptToken));
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return new string(characters.ToArray()); }
            if (key.Key == ConsoleKey.Backspace) { if (characters.Count > 0) { characters.RemoveAt(characters.Count - 1); } continue; }
            if (!char.IsControl(key.KeyChar)) { characters.Add(key.KeyChar); }
        }
    }
}
