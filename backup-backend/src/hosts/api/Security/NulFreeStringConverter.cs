using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupPlatform.Api.Security;

/// <summary>
/// Every JSON string and dictionary key a user-facing route reads: PostgreSQL text cannot store a NUL, so one must stop here as a 400 instead of failing in the database as a 500.
/// Agent routes (<c>/api/agent/</c>) are skipped: they validate their own payload and answer with the offending field (a capability snapshot, a discovery report), which a bare 400 here would hide.
/// </summary>
public sealed class NulFreeStringConverter(IHttpContextAccessor accessor) : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => NulFree(reader.GetString());

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);

    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => NulFree(reader.GetString())!;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(value);

    private string? NulFree(string? value) =>
        value is not null && value.Contains('\0') && accessor.HttpContext?.Request.Path.StartsWithSegments("/api/agent") != true
            ? throw new JsonException("A string must not contain a NUL character.")
            : value;
}
