namespace BackupPlatform.Application.Lists;

public enum SortDirection { Asc, Desc }

/// <summary>
/// The one query shape of every list endpoint: <c>page</c>, <c>pageSize</c>, <c>search</c>, <c>sort</c>, <c>dir</c>.
/// <see cref="SortField"/> is validated against the endpoint's sort map when the store applies it, and a null
/// <see cref="Direction"/> means the field's natural default (dates newest first, text A to Z).
/// </summary>
public sealed record ListQuery(
    int Page = 1, int PageSize = ListQuery.DefaultPageSize, string? Search = null, string? SortField = null, SortDirection? Direction = null)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxPage = 1_000_000;

    public int Skip => checked((Page - 1) * PageSize);

    /// <summary>Builds a query from raw request values; throws <see cref="InvalidListQueryException"/> naming the bad parameter.</summary>
    public static ListQuery Parse(int? page, int? pageSize, string? search, string? sort, string? dir)
    {
        var p = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (p is < 1 or > MaxPage) throw new InvalidListQueryException("page");
        if (size is < 1 or > MaxPageSize) throw new InvalidListQueryException("pageSize");
        SortDirection? direction = null;
        if (!string.IsNullOrEmpty(dir))
        {
            direction = dir.ToLowerInvariant() switch
            {
                "asc" => SortDirection.Asc,
                "desc" => SortDirection.Desc,
                _ => throw new InvalidListQueryException("dir")
            };
        }
        return new ListQuery(p, size, string.IsNullOrWhiteSpace(search) ? null : search.Trim(), string.IsNullOrEmpty(sort) ? null : sort, direction);
    }
}

public sealed class InvalidListQueryException(string field) : Exception($"Invalid list query parameter '{field}'.")
{
    /// <summary>The query parameter at fault: <c>page</c>, <c>pageSize</c>, <c>sort</c> or <c>dir</c>.</summary>
    public string Field { get; } = field;
}
