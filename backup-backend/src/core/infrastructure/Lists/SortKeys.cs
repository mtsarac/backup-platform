using System.Linq.Expressions;

namespace BackupPlatform.Infrastructure.Lists;

/// <summary>Key selectors the shared list sort and search build once, as SQL expressions over a column.</summary>
public static class SortKeys
{
    private const string NumberPad = "000000000000";

    /// <summary>
    /// The position of <paramref name="key"/> in <paramref name="displayOrder"/> (a CASE in SQL); a value that is not listed ranks after the listed ones.
    /// </summary>
    public static Expression<Func<TEntity, int>> Rank<TEntity, TKey>(Expression<Func<TEntity, TKey>> key, IReadOnlyList<TKey> displayOrder)
    {
        Expression rank = Expression.Constant(displayOrder.Count);
        for (var index = displayOrder.Count - 1; index >= 0; index--)
        {
            rank = Expression.Condition(
                Expression.Equal(key.Body, Expression.Constant(displayOrder[index], typeof(TKey))), Expression.Constant(index), rank);
        }
        return Expression.Lambda<Func<TEntity, int>>(rank, key.Parameters);
    }

    /// <summary>The label of a stored key (a CASE in SQL); a key without a label keeps its own value.</summary>
    public static Expression<Func<TEntity, string?>> Label<TEntity>(Expression<Func<TEntity, string?>> key, IReadOnlyDictionary<string, string> labels)
    {
        var label = key.Body;
        foreach (var (stored, text) in labels)
        {
            label = Expression.Condition(Expression.Equal(key.Body, Expression.Constant(stored, typeof(string))), Expression.Constant(text, typeof(string)), label);
        }
        return Expression.Lambda<Func<TEntity, string?>>(label, key.Parameters);
    }

    /// <summary>
    /// Lower-cased text whose digit runs are zero-padded to 12 digits, so "10 GB" sorts after "5 GB" (two <c>regexp_replace</c> calls, no schema object).
    /// A number longer than 12 digits compares by its last 12; a plan or workspace name never has one.
    /// </summary>
    public static Expression<Func<TEntity, string?>> Natural<TEntity>(Expression<Func<TEntity, string?>> key)
    {
        var replace = typeof(TextFold).GetMethod(nameof(TextFold.RegexpReplace))!;
        var global = Expression.Constant("g");
        var padded = Expression.Call(replace, key.Body, Expression.Constant(@"(?<!\d)(?=\d)"), Expression.Constant(NumberPad), global);
        var trimmed = Expression.Call(replace, padded, Expression.Constant(@"\d(?=\d{" + NumberPad.Length + "})"), Expression.Constant(""), global);
        return Expression.Lambda<Func<TEntity, string?>>(Expression.Call(trimmed, nameof(string.ToLower), null), key.Parameters);
    }
}
