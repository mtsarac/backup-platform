using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Platform.Windows.Identity;

// Commands (one line of output each; secrets are never printed, only their SHA-256):
//   whoami                                         -> OK <sid>
//   save <dir> <agentId> <installationId> <credentialId> <secret> <generation> [<pendingCredentialId> <pendingSecret>]
//   load <dir>                                     -> OK none | OK <agentId> <credentialId> <generation> <secretSha256> <pendingGeneration|none>
//   delete <dir>
//   unprotect <file>                               -> OK <plaintextLength> | DPAPI-REFUSED   (the store's own entropy, the caller's own DPAPI scope)
//   mkdir <dir>                                    -> OK   (plain Directory.CreateDirectory: the caller becomes the owner)
// Exit codes: 0 success, 10 the store refused (AgentCredentialStoreException), 11 anything else, 12 DPAPI refused, 2 bad usage.
return await Run(args);

static async Task<int> Run(string[] args)
{
    try
    {
        switch (args.FirstOrDefault())
        {
            case "whoami":
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    Console.WriteLine($"OK {identity.User}");
                }

                return 0;
            case "save" when args.Length is 7 or 9:
            {
                var current = new AgentStoredCredential(Guid.Parse(args[4]), args[5], int.Parse(args[6]));
                var pending = args.Length == 9 ? new AgentStoredCredential(Guid.Parse(args[7]), args[8], current.Generation + 1) : null;
                var stored = new AgentLocalIdentity(Guid.Parse(args[2]), new Uri("https://backup.example.test/"), Guid.Parse(args[3]), current, pending);
                await new WindowsAgentCredentialStore(args[1]).SaveAsync(stored, CancellationToken.None);
                Console.WriteLine("OK");
                return 0;
            }

            case "load" when args.Length == 2:
            {
                var loaded = await new WindowsAgentCredentialStore(args[1]).LoadAsync(CancellationToken.None);
                Console.WriteLine(loaded is null
                    ? "OK none"
                    : $"OK {loaded.AgentId} {loaded.Current.CredentialId} {loaded.Current.Generation} {Sha256(loaded.Current.Secret)} {(loaded.Pending is null ? "none" : loaded.Pending.Generation.ToString())}");
                return 0;
            }

            case "delete" when args.Length == 2:
                await new WindowsAgentCredentialStore(args[1]).DeleteAsync(CancellationToken.None);
                Console.WriteLine("OK");
                return 0;
            case "unprotect" when args.Length == 2:
                try
                {
                    var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(args[1]), WindowsAgentCredentialStore.Entropy, DataProtectionScope.CurrentUser);
                    Console.WriteLine($"OK {plain.Length}");
                    CryptographicOperations.ZeroMemory(plain);
                    return 0;
                }
                catch (CryptographicException)
                {
                    Console.WriteLine("DPAPI-REFUSED");
                    return 12;
                }

            case "mkdir" when args.Length == 2:
                Directory.CreateDirectory(args[1]);
                Console.WriteLine("OK");
                return 0;
            default:
                Console.Error.WriteLine("usage error");
                return 2;
        }
    }
    catch (AgentCredentialStoreException exception)
    {
        Console.WriteLine($"REFUSED {exception.Message}");
        return 10;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"ERROR {exception.GetType().Name}");
        return 11;
    }
}

static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
