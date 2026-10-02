using System.Text.Json;
using BackupPlatform.Domain.Agents;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackupPlatform.Infrastructure.Persistence.Conversions;

/// <summary>
/// Projects immutable software metadata into a persistence-only JSON document, rather than
/// serializing domain objects or mapping software as an owned entity. This scalar mapping
/// lets EF bind the existing Agent constructor without changing the domain.
/// </summary>
internal sealed class AgentSoftwareConverter : ValueConverter<AgentSoftware, string>
{
    public AgentSoftwareConverter()
        : base(software => Serialize(software), json => Deserialize(json))
    {
    }

    private static string Serialize(AgentSoftware software)
    {
        var document = new SoftwareDocument
        {
            Platform = (int)software.Platform,
            Version = software.Version.ToString(),
            ProtocolMajor = software.ProtocolVersion.Major,
            ProtocolMinor = software.ProtocolVersion.Minor
        };
        return JsonSerializer.Serialize(document);
    }

    private static AgentSoftware Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<SoftwareDocument>(json)
            ?? throw new JsonException("Agent software must be a JSON object.");
        return new AgentSoftware(
            (AgentPlatform)document.Platform,
            Version.Parse(document.Version),
            new AgentProtocolVersion(document.ProtocolMajor, document.ProtocolMinor));
    }
}

/// <summary>The JSON storage shape is internal to persistence, not a domain or wire contract.</summary>
internal sealed record SoftwareDocument
{
    public required int Platform { get; init; }
    public required string Version { get; init; }
    public required int ProtocolMajor { get; init; }
    public required int ProtocolMinor { get; init; }
}
