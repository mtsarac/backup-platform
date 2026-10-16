using System.Globalization;
using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.Agent.Core.Discovery;

internal sealed record DockerPortMapping(int? HostPort, int ContainerPort);

internal sealed record DockerContainer(string Name, string Image, IReadOnlyList<DockerPortMapping> Ports);

internal sealed record PostgreSqlCluster(string Version, string Name, int Port, string Status);

/// <summary>
/// Pure text parsers of everything discovery reads from tools, files and the wire. Kept free of I/O so each one is testable
/// with the literal output of the tool it understands; anything they cannot understand yields null/empty, never an exception.
/// </summary>
internal static class DiscoveryParsers
{
    public const int MssqlDefaultPort = 1433;
    public const int PostgreSqlDefaultPort = 5432;

    /// <summary>The <c>docker ps --format</c> template matching <see cref="ParseDockerLine"/>.</summary>
    public const string DockerFormat = "{{.Names}}|{{.Image}}|{{.Ports}}";

    private const int MaximumExpandedPortRange = 64;

    public static int? ParsePort(string? text) =>
        int.TryParse(text?.Trim().Trim('\'', '"'), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;

    // ---- docker -------------------------------------------------------------------------------------------------

    public static DockerContainer? ParseDockerLine(string line)
    {
        var columns = line.Split('|', 3);
        if (columns.Length != 3 || columns[0].Trim() is not { Length: > 0 } name || columns[1].Trim() is not { Length: > 0 } image)
        {
            return null;
        }
        return new DockerContainer(name, image, ParseDockerPorts(columns[2]));
    }

    /// <summary>
    /// Parses a <c>Ports</c> column such as <c>0.0.0.0:14333-&gt;1433/tcp, :::14333-&gt;1433/tcp, 5432/tcp</c>. Only tcp mappings reachable through
    /// the loopback address are published ports; a mapping bound to another interface address cannot be reached as <c>localhost</c>
    /// and is ignored. Exposed-only ports (<c>5432/tcp</c>) carry no host port. IPv4 and IPv6 duplicates collapse.
    /// </summary>
    public static IReadOnlyList<DockerPortMapping> ParseDockerPorts(string ports)
    {
        var mappings = new List<DockerPortMapping>();
        foreach (var entry in ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var protocolAt = entry.LastIndexOf('/');
            if (protocolAt >= 0 && !entry[(protocolAt + 1)..].Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var mapping = protocolAt >= 0 ? entry[..protocolAt] : entry;
            var arrow = mapping.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                AddRange(mappings, null, mapping);
                continue;
            }
            var published = mapping[..arrow];
            var portAt = published.LastIndexOf(':');
            var address = portAt >= 0 ? published[..portAt] : string.Empty;
            if (address is not ("" or "0.0.0.0" or "::" or ":::" or "[::]" or "127.0.0.1" or "::1" or "[::1]"))
            {
                continue;
            }
            AddRange(mappings, portAt >= 0 ? published[(portAt + 1)..] : published, mapping[(arrow + 2)..]);
        }
        return mappings.Distinct().ToList();
    }

    private static void AddRange(List<DockerPortMapping> mappings, string? host, string container)
    {
        var containerPorts = ExpandPorts(container);
        var hostPorts = host is null ? null : ExpandPorts(host);
        if (hostPorts is not null && hostPorts.Count != containerPorts.Count)
        {
            return;
        }
        for (var i = 0; i < containerPorts.Count; i++)
        {
            mappings.Add(new DockerPortMapping(hostPorts?[i], containerPorts[i]));
        }
    }

    /// <summary>One port or an <c>a-b</c> range (bounded); anything else is no port.</summary>
    private static List<int> ExpandPorts(string text)
    {
        var dash = text.IndexOf('-');
        if (dash < 0)
        {
            return ParsePort(text) is { } single ? [single] : [];
        }
        if (ParsePort(text[..dash]) is not { } first || ParsePort(text[(dash + 1)..]) is not { } last
            || last < first || last - first >= MaximumExpandedPortRange)
        {
            return [];
        }
        return [.. Enumerable.Range(first, last - first + 1)];
    }

    /// <summary>
    /// The engine a container runs, from its image repository and the container port: an image name alone is not enough (a
    /// metrics exporter is called <c>postgres-exporter</c>), so a published/exposed port must also be the engine's well-known port.
    /// </summary>
    public static string? ClassifyDockerEngine(string image, int containerPort)
    {
        var engine = EngineOfImage(image);
        return engine switch
        {
            DiscoveryEngines.MsSql when containerPort == MssqlDefaultPort => engine,
            DiscoveryEngines.PostgreSql when containerPort == PostgreSqlDefaultPort => engine,
            _ => null,
        };
    }

    /// <summary>The engine an image name suggests, ignoring registry, tag and digest; null for anything else.</summary>
    public static string? EngineOfImage(string image)
    {
        var repository = image.ToLowerInvariant();
        var cut = repository.IndexOfAny(['@', ':']);
        if (cut >= 0 && !repository[cut..].Contains('/', StringComparison.Ordinal))
        {
            repository = repository[..cut];
        }
        if (repository.Contains("exporter", StringComparison.Ordinal))
        {
            return null;
        }
        if (repository.Contains("mssql", StringComparison.Ordinal) || repository.Contains("sql-server", StringComparison.Ordinal))
        {
            return DiscoveryEngines.MsSql;
        }
        return repository.Contains("postgres", StringComparison.Ordinal) || repository.Contains("postgis", StringComparison.Ordinal)
            || repository.Contains("timescaledb", StringComparison.Ordinal)
            ? DiscoveryEngines.PostgreSql
            : null;
    }

    // ---- TDS pre-login / PostgreSQL SSLRequest ------------------------------------------------------------------

    /// <summary>The PostgreSQL <c>SSLRequest</c> startup message; every PostgreSQL server answers one byte, <c>S</c> or <c>N</c>.</summary>
    public static ReadOnlySpan<byte> PostgreSqlSslRequest => [0x00, 0x00, 0x00, 0x08, 0x04, 0xD2, 0x16, 0x2F];

    public static bool IsPostgreSqlSslAnswer(byte answer) => answer is (byte)'S' or (byte)'N';

    /// <summary>A credential-free TDS PRELOGIN packet (47 bytes) asking for version, encryption, instance, thread and MARS options.</summary>
    public static byte[] BuildMsSqlPreLogin()
    {
        // Option table: token, big-endian offset into the payload, length; terminated by 0xFF. Data starts after the 26 table bytes.
        byte[] payload =
        [
            0x00, 0x00, 26, 0x00, 6,
            0x01, 0x00, 32, 0x00, 1,
            0x02, 0x00, 33, 0x00, 1,
            0x03, 0x00, 34, 0x00, 4,
            0x04, 0x00, 38, 0x00, 1,
            0xFF,
            0, 0, 0, 0, 0, 0, // client version
            0x02,             // encryption: not supported by the client
            0x00,             // instance name: empty
            0, 0, 0, 0,       // thread id
            0x00,             // MARS off
        ];
        var packet = new byte[8 + payload.Length];
        packet[0] = 0x12; // PRELOGIN
        packet[1] = 0x01; // end of message
        packet[2] = (byte)(packet.Length >> 8);
        packet[3] = (byte)packet.Length;
        packet[6] = 0x01; // packet id
        payload.CopyTo(packet, 8);
        return packet;
    }

    /// <summary>
    /// Recognises a TDS tabular-result packet (the PRELOGIN answer) and, when present, reads the server version from its VERSION option
    /// as <c>major.minor.build</c>. <paramref name="version"/> is null when the answer carried none.
    /// </summary>
    public static bool TryParsePreLoginResponse(ReadOnlySpan<byte> response, out string? version)
    {
        version = null;
        if (response.Length < 8 || response[0] != 0x04)
        {
            return false;
        }
        var payload = response[8..];
        for (var at = 0; at + 5 <= payload.Length && payload[at] != 0xFF; at += 5)
        {
            if (payload[at] != 0x00)
            {
                continue;
            }
            var offset = (payload[at + 1] << 8) | payload[at + 2];
            var length = (payload[at + 3] << 8) | payload[at + 4];
            if (length >= 6 && offset + 6 <= payload.Length)
            {
                version = string.Create(CultureInfo.InvariantCulture, $"{payload[offset]}.{payload[offset + 1]}.{(payload[offset + 2] << 8) | payload[offset + 3]}");
            }
            break;
        }
        return true;
    }

    // ---- configuration files and tools --------------------------------------------------------------------------

    /// <summary>The effective <c>port</c> of a postgresql.conf: the last uncommented assignment wins, as in the server.</summary>
    public static int? ParsePostgreSqlConfPort(string text)
    {
        int? port = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment].TrimEnd();
            }
            if (!line.StartsWith("port", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var rest = line[4..];
            // "portal = ..." or any other key sharing the prefix is not the port.
            if (rest.Length == 0 || (rest[0] != '=' && !char.IsWhiteSpace(rest[0])))
            {
                continue;
            }
            port = ParsePort(rest.TrimStart().TrimStart('=')) ?? port;
        }
        return port;
    }

    /// <summary>The <c>tcpport</c> of the <c>[network]</c> section of mssql.conf; null when absent (the default 1433 applies).</summary>
    public static int? ParseMssqlConfTcpPort(string text)
    {
        var inNetwork = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inNetwork = line.TrimEnd().Equals("[network]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var equals = line.IndexOf('=');
            if (inNetwork && equals > 0 && line[..equals].Trim().Equals("tcpport", StringComparison.OrdinalIgnoreCase))
            {
                return ParsePort(line[(equals + 1)..]);
            }
        }
        return null;
    }

    /// <summary>One <c>pg_lsclusters --no-header</c> line: <c>Ver Cluster Port Status Owner Data-directory Log-file</c>.</summary>
    public static PostgreSqlCluster? ParsePgLsClustersLine(string line)
    {
        var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return columns.Length >= 4 && ParsePort(columns[2]) is { } port ? new PostgreSqlCluster(columns[0], columns[1], port, columns[3]) : null;
    }

    /// <summary>
    /// Names (without <c>.service</c>) of the running SQL Server and PostgreSQL units in <c>systemctl list-units --no-legend --plain</c>
    /// output. A template instance such as <c>postgresql@16-main</c> keeps its instance part.
    /// </summary>
    public static IReadOnlyList<string> ParseSystemdUnits(string output)
    {
        var units = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var first = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null && first.EndsWith(".service", StringComparison.Ordinal)
                && first[..^".service".Length] is var name
                && (name.StartsWith("mssql-server", StringComparison.Ordinal) || name.StartsWith("postgresql", StringComparison.Ordinal)))
            {
                units.Add(name);
            }
        }
        return units;
    }
}
