using BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.AgentTests.Support;

internal static class TestIdentity
{
    public static readonly string Secret = "TopSecret".PadRight(43, 'x');
    public static readonly string RotatedSecret = "RotatedSecret".PadRight(43, 'y');
    public static readonly Guid AgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid CredentialId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static AgentLocalIdentity Create(string? secret = null, Guid? credentialId = null, Guid? agentId = null, int generation = 1) =>
        new(agentId ?? AgentId, new Uri("https://backup.example.test/"), Guid.Parse("33333333-3333-3333-3333-333333333333"),
            new AgentStoredCredential(credentialId ?? CredentialId, secret ?? Secret, generation));
}
