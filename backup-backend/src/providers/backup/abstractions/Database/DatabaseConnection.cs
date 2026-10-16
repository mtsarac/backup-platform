namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>Where a database instance listens. Carries no secret and no engine-specific connection string.</summary>
public sealed record DatabaseEndpoint
{
    public DatabaseEndpoint(string host, int? port = null, string? instanceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (host.Any(char.IsControl) || host.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Host names must not contain whitespace or control characters.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Ports are 1..65535.");
        }

        if (port is not null && host.Contains('\\'))
        {
            throw new ArgumentException("A host naming an instance (host\\instance) cannot also carry a port: the port would silently win and could reach a different instance.", nameof(port));
        }

        if (instanceName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
            if (instanceName.Any(char.IsControl))
            {
                throw new ArgumentException("Instance names must not contain control characters.", nameof(instanceName));
            }

            if (port is not null)
            {
                throw new ArgumentException("An endpoint addresses an instance by name or by port, not both.", nameof(instanceName));
            }
        }

        Host = host;
        Port = port;
        InstanceName = instanceName;
    }

    public string Host { get; }
    public int? Port { get; }

    /// <summary>A named instance resolved by the engine's own listener service; null for the default instance or an explicit port.</summary>
    public string? InstanceName { get; }

    /// <summary>
    /// The instance the caller meant, when it named one (<see cref="InstanceName"/> or a <c>host\instance</c> host); null when it addressed the default instance or a bare port,
    /// which a provider cannot compare against what answered. A provider uses it to refuse a connection that reached a different instance.
    /// </summary>
    public string? ExpectedInstanceName => InstanceName ?? (Host.IndexOf('\\') is var slash and >= 0 && slash < Host.Length - 1 ? Host[(slash + 1)..] : null);
}

/// <summary>
/// Connection behaviour for short-lived diagnostic sessions. Every timeout is finite so an agent shutdown cannot be held
/// up by an unresponsive server.
/// </summary>
public sealed record DatabaseConnectionOptions
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MaximumConnectTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumCommandTimeout = TimeSpan.FromHours(1);

    public DatabaseConnectionOptions(
        TimeSpan? connectTimeout = null,
        TimeSpan? commandTimeout = null,
        bool requireEncryption = true,
        bool trustServerCertificate = false)
    {
        ConnectTimeout = Validate(connectTimeout ?? DefaultConnectTimeout, MaximumConnectTimeout, nameof(connectTimeout));
        CommandTimeout = Validate(commandTimeout ?? DefaultCommandTimeout, MaximumCommandTimeout, nameof(commandTimeout));
        RequireEncryption = requireEncryption;
        TrustServerCertificate = trustServerCertificate;
    }

    /// <summary>Whole seconds, at least one.</summary>
    public TimeSpan ConnectTimeout { get; }

    /// <summary>Whole seconds, at least one. An infinite command timeout is deliberately not expressible.</summary>
    public TimeSpan CommandTimeout { get; }

    /// <summary>Encrypt the transport. Defaults to true; turning it off is an explicit decision.</summary>
    public bool RequireEncryption { get; }

    /// <summary>Accept a server certificate that does not chain to a trusted root (self-signed development servers). Defaults to false.</summary>
    public bool TrustServerCertificate { get; }

    private static TimeSpan Validate(TimeSpan value, TimeSpan maximum, string parameterName)
    {
        if (value < TimeSpan.FromSeconds(1) || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Timeouts must be between 1 second and {maximum}.");
        }

        // Providers express timeouts in whole seconds; round up so the configured value is never shortened.
        return TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds));
    }
}

/// <summary>Non-secret connection configuration: the endpoint and how to talk to it. Credentials travel separately.</summary>
public sealed record DatabaseConnectionSettings
{
    public DatabaseConnectionSettings(DatabaseEndpoint endpoint, DatabaseConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Endpoint = endpoint;
        Options = options ?? new DatabaseConnectionOptions();
    }

    public DatabaseEndpoint Endpoint { get; }
    public DatabaseConnectionOptions Options { get; }
}
