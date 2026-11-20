using System.Globalization;
using System.Numerics;

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

    /// <summary>
    /// Reads the raw <c>page</c> value: any whole number, however large (a number beyond <see cref="long"/> saturates), so that <see cref="Parse"/> can clamp it
    /// instead of the request binder refusing it. Throws <see cref="InvalidListQueryException"/> for text that is not a number.
    /// </summary>
    public static long? ParsePage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) throw new InvalidListQueryException("page");
        return (long)BigInteger.Clamp(number, long.MinValue, long.MaxValue);
    }

    /// <summary>
    /// Builds a query from raw request values; throws <see cref="InvalidListQueryException"/> naming the bad parameter. A <c>page</c> beyond
    /// <see cref="MaxPage"/> is clamped to it (the list then answers an empty page with the true total) rather than refused.
    /// </summary>
    public static ListQuery Parse(long? page, int? pageSize, string? search, string? sort, string? dir)
    {
        var size = pageSize ?? DefaultPageSize;
        if ((page ?? 1) < 1) throw new InvalidListQueryException("page");
        var p = (int)Math.Min(page ?? 1, MaxPage);
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
