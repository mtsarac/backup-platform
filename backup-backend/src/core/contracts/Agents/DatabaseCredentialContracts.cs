namespace BackupPlatform.Contracts.Agents;

/// <summary>A database login typed by a user. Write-only: it is encrypted on arrival and no view ever returns it.</summary>
public sealed record DatabaseLoginInput(string UserName, string Password)
{
    public override string ToString() => "DatabaseLoginInput { [redacted] }";
}

/// <summary>An agent asking for the login behind a reference it was given in a task, scan plan or restore.</summary>
public sealed record DatabaseCredentialRequest
{
    public string Reference { get; init; } = string.Empty;
}

/// <summary>The decrypted login, sent only to the agent the reference was minted for. The agent keeps it in memory for one operation.</summary>
public sealed record DatabaseCredentialResponse
{
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

    public override string ToString() => "DatabaseCredentialResponse { [redacted] }";
}
