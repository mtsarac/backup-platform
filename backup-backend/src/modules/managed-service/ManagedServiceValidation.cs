using System.Text;
using System.Text.RegularExpressions;
using BackupPlatform.ManagedService.Plans;

namespace BackupPlatform.ManagedService;

/// <summary>Field validation shared by the entities and the administration service so the rules exist exactly once.</summary>
public static partial class ManagedServiceValidation
{
    public const int MaximumPlanNameLength = 100;
    public const int MaximumQuotaPolicyReferenceLength = 64;

    /// <summary>Trimmed display form (inner whitespace collapsed) and the case-insensitive key used for uniqueness.</summary>
    public static (string Display, string Normalized) PlanName(string? value, string field = "name")
    {
        var builder = new StringBuilder();
        var pendingSpace = false;
        foreach (var character in value ?? string.Empty)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (char.IsControl(character))
            {
                throw new InvalidManagedServiceRequestException(field, "invalid_characters");
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character);
        }
        if (builder.Length == 0)
        {
            throw new InvalidManagedServiceRequestException(field, "required");
        }
        if (builder.Length > MaximumPlanNameLength)
        {
            throw new InvalidManagedServiceRequestException(field, "too_long");
        }
        var display = builder.ToString();
        return (display, display.ToLowerInvariant());
    }

    /// <summary>Policy names follow the plan-name rules (trimmed display form, case-insensitive key).</summary>
    public static (string Display, string Normalized) PolicyName(string? value, string field = "name") => PlanName(value, field);

    /// <summary>Null means no reference. A reference is an opaque, lowercase key that a later slice resolves; it is never interpreted here.</summary>
    public static string? QuotaPolicyReference(string? value, string field = "quotaPolicyReference")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        if (trimmed.Length > MaximumQuotaPolicyReferenceLength || !ReferencePattern().IsMatch(trimmed))
        {
            throw new InvalidManagedServiceRequestException(field, "invalid_value");
        }
        return trimmed;
    }

    /// <summary>Null is the unlimited plan limit; an explicit limit must be positive and within <see cref="StorageLimits.MaximumBytes"/>.</summary>
    public static long? PlanStorageLimit(long? bytes, string field = "storageLimit")
    {
        if (bytes is { } value && !StorageLimits.IsValid(value))
        {
            throw new InvalidManagedServiceRequestException(field, "out_of_range");
        }
        return bytes;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}
