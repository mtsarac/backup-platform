using System.Text.Json;
using BackupPlatform.Domain.Storage;

namespace BackupPlatform.ManagedService.Quota;

public enum QuotaScopeKind
{
    /// <summary>Every replica.</summary>
    All = 1,
    /// <summary>Replicas on a storage connection whose provider type is one of the listed keys.</summary>
    ProviderType = 2,
    /// <summary>Replicas on a storage connection whose <see cref="StorageOwnership"/> is one of the listed values.</summary>
    OwnershipType = 3,
    /// <summary>Replicas on a storage connection whose quota scope label is one of the listed labels.</summary>
    QuotaScope = 4,
    /// <summary>Replicas on one of the listed storage connections (by id).</summary>
    StorageConnection = 5
}

/// <summary>
/// What a scope rule can see of one replica: the properties of the storage connection it lives on. Every selector kind reads one of these; a new
/// provider type adds a value, never a branch here.
/// </summary>
public readonly record struct ReplicaStorageFacts(string ProviderType, StorageOwnership Ownership, string QuotaScope, Guid ConnectionId);

/// <summary>One rule of a scope. <see cref="Values"/> are canonical (trimmed, ordinal-sorted, distinct) and empty for <see cref="QuotaScopeKind.All"/>.</summary>
public sealed record QuotaScopeSelector(QuotaScopeKind Kind, IReadOnlyList<string> Values);

/// <summary>
/// Which replicas a policy counts: the union of its selectors. It is data (stored as JSON on the policy), so narrowing quota to
/// platform-managed storage is a policy update and a rebuild, never a code change. Selectors match properties of the replica's storage connection
/// (provider type, ownership, quota scope label, or the connection itself), so adding a provider needs no condition in the quota engine.
/// </summary>
public sealed class QuotaScopeRule : IEquatable<QuotaScopeRule>
{
    public const int MaximumSelectors = 8;
    public const int MaximumValuesPerSelector = 32;
    public const string Field = "includedStorageScopes";

    private readonly string _json;

    private QuotaScopeRule(IReadOnlyList<QuotaScopeSelector> selectors, string json)
    {
        Selectors = selectors;
        _json = json;
    }

    public static QuotaScopeRule All { get; } = Create([new QuotaScopeSelector(QuotaScopeKind.All, [])]);

    public IReadOnlyList<QuotaScopeSelector> Selectors { get; }

    public bool IncludesEverything => Selectors.Any(selector => selector.Kind == QuotaScopeKind.All);

    public bool Matches(ReplicaStorageFacts replica)
    {
        foreach (var selector in Selectors)
        {
            var matched = selector.Kind switch
            {
                QuotaScopeKind.All => true,
                QuotaScopeKind.ProviderType => selector.Values.Contains(replica.ProviderType, StringComparer.Ordinal),
                QuotaScopeKind.OwnershipType => selector.Values.Contains(replica.Ownership.ToString(), StringComparer.Ordinal),
                QuotaScopeKind.QuotaScope => selector.Values.Contains(replica.QuotaScope, StringComparer.Ordinal),
                QuotaScopeKind.StorageConnection => selector.Values.Contains(replica.ConnectionId.ToString("D"), StringComparer.Ordinal),
                _ => false
            };
            if (matched)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Validates and canonicalizes. Two rules that select the same replicas have the same JSON.</summary>
    public static QuotaScopeRule Create(IEnumerable<QuotaScopeSelector>? selectors)
    {
        var list = selectors?.ToList() ?? [];
        if (list.Count == 0)
        {
            throw new InvalidManagedServiceRequestException(Field, "required");
        }
        if (list.Count > MaximumSelectors)
        {
            throw new InvalidManagedServiceRequestException(Field, "too_many");
        }
        var canonical = new List<QuotaScopeSelector>();
        foreach (var selector in list)
        {
            if (selector is null || !Enum.IsDefined(selector.Kind))
            {
                throw new InvalidManagedServiceRequestException(Field, "unsupported_scope_kind");
            }
            var values = (selector.Values ?? []).Select(value => value?.Trim() ?? string.Empty).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            switch (selector.Kind)
            {
                case QuotaScopeKind.All:
                    if (values.Count != 0)
                    {
                        throw new InvalidManagedServiceRequestException(Field, "unexpected_values");
                    }
                    break;
                case QuotaScopeKind.ProviderType:
                    values = values.Select(value => value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
                    if (values.Count == 0 || values.Count > MaximumValuesPerSelector || !values.All(IsProviderKey))
                    {
                        throw new InvalidManagedServiceRequestException(Field, "invalid_value");
                    }
                    break;
                case QuotaScopeKind.OwnershipType:
                    values = values.Select(CanonicalOwnership).Distinct(StringComparer.Ordinal).ToList();
                    if (values.Count == 0 || values.Any(value => value.Length == 0))
                    {
                        throw new InvalidManagedServiceRequestException(Field, "invalid_value");
                    }
                    break;
                case QuotaScopeKind.QuotaScope:
                    values = values.Select(value => value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
                    if (values.Count == 0 || values.Count > MaximumValuesPerSelector || !values.All(StorageQuotaScopes.IsValid))
                    {
                        throw new InvalidManagedServiceRequestException(Field, "invalid_value");
                    }
                    break;
                case QuotaScopeKind.StorageConnection:
                    values = values.Select(CanonicalConnectionId).Distinct(StringComparer.Ordinal).ToList();
                    if (values.Count == 0 || values.Count > MaximumValuesPerSelector || values.Any(value => value.Length == 0))
                    {
                        throw new InvalidManagedServiceRequestException(Field, "invalid_value");
                    }
                    break;
            }
            values.Sort(StringComparer.Ordinal);
            canonical.Add(new QuotaScopeSelector(selector.Kind, values));
        }
        // All already selects everything: keeping other selectors next to it would only hide what the rule really says.
        if (canonical.Any(selector => selector.Kind == QuotaScopeKind.All))
        {
            canonical = [new QuotaScopeSelector(QuotaScopeKind.All, [])];
        }
        else
        {
            canonical = canonical.GroupBy(selector => selector.Kind)
                .Select(group => new QuotaScopeSelector(group.Key, group.SelectMany(selector => selector.Values).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()))
                .OrderBy(selector => selector.Kind).ToList();
        }
        return new QuotaScopeRule(canonical, Serialize(canonical));
    }

    /// <summary>Reads the stored (or requested) JSON form: <c>[{"kind":"providerType","values":["central"]}]</c>.</summary>
    public static QuotaScopeRule FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidManagedServiceRequestException(Field, "required");
        }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidManagedServiceRequestException(Field, "invalid_value");
            }
            var selectors = new List<QuotaScopeSelector>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidManagedServiceRequestException(Field, "invalid_value");
                }
                var parsed = ParseKind(kind.GetString());
                var values = new List<string>();
                if (element.TryGetProperty("values", out var array))
                {
                    if (array.ValueKind != JsonValueKind.Array || array.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    {
                        throw new InvalidManagedServiceRequestException(Field, "invalid_value");
                    }
                    values.AddRange(array.EnumerateArray().Select(value => value.GetString()!));
                }
                selectors.Add(new QuotaScopeSelector(parsed, values));
            }
            return Create(selectors);
        }
        catch (JsonException)
        {
            throw new InvalidManagedServiceRequestException(Field, "invalid_value");
        }
    }

    public string ToJson() => _json;

    public static string KindName(QuotaScopeKind kind) => kind switch
    {
        QuotaScopeKind.All => "all",
        QuotaScopeKind.ProviderType => "providerType",
        QuotaScopeKind.OwnershipType => "ownershipType",
        QuotaScopeKind.QuotaScope => "quotaScope",
        QuotaScopeKind.StorageConnection => "storageConnection",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Names only: numeric text such as "1" is rejected.</summary>
    public static QuotaScopeKind ParseKind(string? name) => name switch
    {
        "all" => QuotaScopeKind.All,
        "providerType" => QuotaScopeKind.ProviderType,
        "ownershipType" => QuotaScopeKind.OwnershipType,
        "quotaScope" => QuotaScopeKind.QuotaScope,
        "storageConnection" => QuotaScopeKind.StorageConnection,
        _ => throw new InvalidManagedServiceRequestException(Field, "unsupported_scope_kind")
    };

    public bool Equals(QuotaScopeRule? other) => other is not null && string.Equals(_json, other._json, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as QuotaScopeRule);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_json);

    public override string ToString() => _json;

    private static string Serialize(IReadOnlyList<QuotaScopeSelector> selectors) => JsonSerializer.Serialize(selectors.Select(selector =>
        selector.Kind == QuotaScopeKind.All
            ? (object)new { kind = KindName(selector.Kind) }
            : new { kind = KindName(selector.Kind), values = selector.Values }));

    private static bool IsProviderKey(string value) =>
        value.Length is >= 1 and <= 64 && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-') && value[0] is not ('.' or '_' or '-');

    private static string CanonicalOwnership(string value) =>
        Enum.GetNames<StorageOwnership>().FirstOrDefault(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

    /// <summary>The canonical lowercase hyphenated form; anything that is not exactly a non-empty GUID is empty (and so rejected).</summary>
    private static string CanonicalConnectionId(string value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString("D") : string.Empty;
}
