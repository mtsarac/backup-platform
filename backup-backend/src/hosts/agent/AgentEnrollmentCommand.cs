using BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.Agent.Host;

internal static class AgentEnrollmentCommand
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var options = Parse(args);
            var server = options.GetValueOrDefault("--server");
            var token = options.GetValueOrDefault("--token");
            var name = options.GetValueOrDefault("--name");
            var interactive = !options.ContainsKey("--non-interactive");
            if (interactive && server is null)
            {
                Console.Write("HTTPS backend address: ");
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
            return await WindowsEnrollmentCommand.RunInstallerAsync(new WindowsEnrollmentCommand.Input(
                uri.AbsoluteUri, token!, name, directory), cancellationToken).ConfigureAwait(false);
#else
            using var lease = AgentEnrollmentLease.Acquire(directory);
            var store = AgentCredentialStoreFactory.Create(directory);
            using var http = CreateHttpClient();
            var identity = await new AgentEnrollment(http, store).EnrollAsync(uri, token!, name, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Enrollment verified for agent {identity.AgentId:D}. Identity saved securely. Start this host with the same service account and store directory to connect.");
            return 0;
#endif
        }
        catch (Exception exception) when (exception is AgentEnrollmentException or AgentCredentialStoreException or ArgumentException
            or UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine(SafeError(exception));
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Enrollment was cancelled. If submitted, the token may have been consumed; check the wizard. Any saved identity was preserved.");
            return 1;
        }
    }

    internal static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    internal static string SafeError(Exception exception) => exception switch
    {
        AgentEnrollmentException or AgentCredentialStoreException => exception.Message,
        _ => "The secure enrollment store could not be used. Check the absolute store path, ownership and permissions. No existing identity was replaced."
    };

    internal static Dictionary<string, string?> Parse(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var key = args[index];
            if (key == "--non-interactive")
            {
                if (!options.TryAdd(key, null)) { throw new AgentEnrollmentException("Duplicate enrollment option."); }
                continue;
            }
            if (key is not ("--server" or "--token" or "--name" or "--store-directory") || index + 1 == args.Length
                || args[index + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(key, args[++index]))
            {
                throw new AgentEnrollmentException("Usage: enroll --server https://backend/ [--token TOKEN] [--name NAME] [--store-directory ABSOLUTE_PATH] [--non-interactive].");
            }
        }
        return options;
    }

    private static string ReadToken()
    {
        if (Console.IsInputRedirected)
        {
            throw new AgentEnrollmentException("Token prompting requires a terminal. Supply --token with --non-interactive instead.");
        }
        Console.Write("Enrollment token (hidden): ");
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
