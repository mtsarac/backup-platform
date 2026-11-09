using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupPlatform.Api.Security;

/// <summary>Every JSON string and dictionary key the API reads: PostgreSQL text cannot store a NUL, so one must stop here as a 400 instead of failing in the database as a 500.</summary>
public sealed class NulFreeStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => NulFree(reader.GetString());

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);

    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => NulFree(reader.GetString())!;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(value);

    private static string? NulFree(string? value) => value is not null && value.Contains('\0') ? throw new JsonException("A string must not contain a NUL character.") : value;
}
