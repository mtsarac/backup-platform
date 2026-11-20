using System.Linq.Expressions;
using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Lists;

/// <summary>
/// The sort allowlist of one list endpoint: public field name to key selector. Declare it once per entity,
/// e.g. <c>SortMap&lt;Row&gt;.Create().Text("name", r =&gt; r.Name).Date("createdAt", r =&gt; r.CreatedAt).Value("size", r =&gt; r.Size)</c>.
/// </summary>
public sealed class SortMap<TEntity>
{
    private readonly Dictionary<string, (SortDirection Default, Func<IQueryable<TEntity>, SortDirection, IOrderedQueryable<TEntity>> Order)> _fields = new(StringComparer.Ordinal);

    public static SortMap<TEntity> Create() => new();

    /// <summary>Case-insensitive text key, A to Z by default.</summary>
    public SortMap<TEntity> Text(string field, Expression<Func<TEntity, string?>> key)
    {
        var lowered = Expression.Lambda<Func<TEntity, string?>>(Expression.Call(key.Body, nameof(string.ToLower), null), key.Parameters);
        return Add(field, SortDirection.Asc, key, lowered);
    }

    /// <summary>Numbers, booleans and enums (their stored order), smallest first by default.</summary>
    public SortMap<TEntity> Value<TKey>(string field, Expression<Func<TEntity, TKey>> key) => Add(field, SortDirection.Asc, key, key);

    /// <summary>Dates, newest first by default.</summary>
    public SortMap<TEntity> Date<TKey>(string field, Expression<Func<TEntity, TKey>> key) => Add(field, SortDirection.Desc, key, key);

    internal IOrderedQueryable<TEntity> Apply(IQueryable<TEntity> query, string field, SortDirection? direction)
    {
        if (!_fields.TryGetValue(field, out var entry)) throw new InvalidListQueryException("sort");
        return entry.Order(query, direction ?? entry.Default);
    }

    private SortMap<TEntity> Add<TKey, TOrder>(string field, SortDirection defaultDirection, Expression<Func<TEntity, TKey>> key, Expression<Func<TEntity, TOrder>> order)
    {
        // Postgres puts NULL last ascending but first descending; the leading IS NULL flag keeps them last both ways.
        var nullable = !typeof(TKey).IsValueType || Nullable.GetUnderlyingType(typeof(TKey)) is not null;
        var isNull = nullable ? Expression.Lambda<Func<TEntity, bool>>(Expression.Equal(key.Body, Expression.Constant(null, typeof(TKey))), key.Parameters) : null;
        _fields.Add(field, (defaultDirection, (query, direction) =>
        {
            var ordered = isNull is null ? null : query.OrderBy(isNull);
            return direction == SortDirection.Asc
                ? ordered is null ? query.OrderBy(order) : ordered.ThenBy(order)
                : ordered is null ? query.OrderByDescending(order) : ordered.ThenByDescending(order);
        }));
        return this;
    }
}

public static class ListQueryExtensions
{
    /// <summary>
    /// Orders by the requested field (or <paramref name="defaultField"/> when none), then by the unique
    /// <paramref name="tiebreaker"/> so paging is stable. Throws <see cref="InvalidListQueryException"/> for a field outside the map.
    /// </summary>
    public static IOrderedQueryable<TEntity> ApplySort<TEntity, TUnique>(
        this IQueryable<TEntity> source, SortMap<TEntity> map, ListQuery query, string defaultField, Expression<Func<TEntity, TUnique>> tiebreaker) =>
        map.Apply(source, query.SortField ?? defaultField, query.Direction).ThenBy(tiebreaker);

    /// <summary>Case-insensitive contains match of <see cref="ListQuery.Search"/> on any of the columns; the term is a bound parameter, wildcards are literal.</summary>
    public static IQueryable<TEntity> ApplySearch<TEntity>(this IQueryable<TEntity> source, ListQuery query, params Expression<Func<TEntity, string?>>[] columns)
    {
        if (query.Search is null) return source;
        var parameter = Expression.Parameter(typeof(TEntity), "row");
        // A closure member, not a constant, so EF binds it as a parameter instead of inlining it into the SQL.
        var term = Expression.Property(Expression.Constant(new SearchTerm(query.Search.ToLowerInvariant())), nameof(SearchTerm.Value));
        var match = columns
            .Select(column => (Expression)Expression.Call(
                Expression.Call(new Rebind(column.Parameters[0], parameter).Visit(column.Body), nameof(string.ToLower), null),
                nameof(string.Contains), null, term))
            .Aggregate(Expression.OrElse);
        return source.Where(Expression.Lambda<Func<TEntity, bool>>(match, parameter));
    }

    /// <summary>Counts, then fetches the requested page of an already searched and sorted query.</summary>
    public static async Task<PagedResult<TEntity>> ToPagedResultAsync<TEntity>(this IQueryable<TEntity> source, ListQuery query, CancellationToken cancellationToken)
    {
        var total = await source.LongCountAsync(cancellationToken);
        var items = await source.Skip(query.Skip).Take(query.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<TEntity>(items, total, query.Page, query.PageSize);
    }

    private sealed record SearchTerm(string Value);

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
