namespace BackupPlatform.BackupProviders.MSSQL.Discovery;

/// <summary>
/// <c>SERVERPROPERTY('EngineEdition')</c>: the stable engine class. Prefer it over edition text; it is a small closed set that
/// does not change with marketing names. Values this code does not know map to <see cref="Unknown"/> and keep the raw number in
/// <see cref="SqlServerEdition.RawEngineEdition"/>.
/// </summary>
public enum SqlServerEngineEdition
{
    Unknown = 0,
    /// <summary>Personal or Desktop engine (legacy).</summary>
    Personal = 1,
    /// <summary>Standard, Web and Business Intelligence.</summary>
    Standard = 2,
    /// <summary>Enterprise, Developer and Evaluation.</summary>
    Enterprise = 3,
    Express = 4,
    AzureSqlDatabase = 5,
    AzureSynapseDedicatedPool = 6,
    AzureSqlManagedInstance = 8,
    AzureSqlEdge = 9,
    AzureSynapseServerless = 11
}

/// <summary>A normalized product family derived from the edition text. Only used to separate editions that share an engine edition.</summary>
public enum SqlServerEditionFamily
{
    Unknown = 0,
    Enterprise,
    Standard,
    Web,
    BusinessIntelligence,
    Developer,
    Express,
    Evaluation,
    Personal,
    Azure
}

/// <summary>
/// The edition of a server: the original text, a normalized family and the engine edition. Capability rules key on
/// <see cref="Engine"/> first and consult <see cref="Family"/> only where one engine edition covers several products.
/// </summary>
public readonly record struct SqlServerEdition(
    string? Text,
    SqlServerEditionFamily Family,
    SqlServerEngineEdition Engine,
    int? RawEngineEdition)
{
    public static SqlServerEdition FromServerProperties(string? editionText, int? engineEdition)
    {
        var text = string.IsNullOrWhiteSpace(editionText) ? null : editionText.Trim();
        var engine = engineEdition is { } raw && Enum.IsDefined((SqlServerEngineEdition)raw) && raw != 0
            ? (SqlServerEngineEdition)raw
            : SqlServerEngineEdition.Unknown;
        return new SqlServerEdition(text, ParseFamily(text), engine, engineEdition);
    }

    // Order matters: "Enterprise Evaluation Edition" is Enterprise, "Express Edition with Advanced Services" is Express.
    private static readonly (string Fragment, SqlServerEditionFamily Family)[] FamilyFragments =
    [
        ("Azure", SqlServerEditionFamily.Azure),
        ("Business Intelligence", SqlServerEditionFamily.BusinessIntelligence),
        ("Enterprise", SqlServerEditionFamily.Enterprise),
        ("Developer", SqlServerEditionFamily.Developer),
        ("Standard", SqlServerEditionFamily.Standard),
        ("Web", SqlServerEditionFamily.Web),
        ("Express", SqlServerEditionFamily.Express),
        ("Evaluation", SqlServerEditionFamily.Evaluation),
        ("Personal", SqlServerEditionFamily.Personal),
        ("Desktop", SqlServerEditionFamily.Personal)
    ];

    private static SqlServerEditionFamily ParseFamily(string? text)
    {
        if (text is null)
        {
            return SqlServerEditionFamily.Unknown;
        }

        foreach (var (fragment, family) in FamilyFragments)
        {
            if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return family;
            }
        }

        return SqlServerEditionFamily.Unknown;
    }
}
