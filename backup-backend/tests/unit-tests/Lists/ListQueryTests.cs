using BackupPlatform.Application.Lists;
using BackupPlatform.Infrastructure.Lists;

namespace BackupPlatform.UnitTests.Lists;

public sealed class ListQueryTests
{
    private sealed record Row(int Id, string Name, DateTimeOffset? At, int Size);

    private static readonly SortMap<Row> Map = SortMap<Row>.Create()
        .Text("name", r => r.Name)
        .Date("at", r => r.At)
        .Value("size", r => r.Size);

    private static readonly Row[] Rows =
    [
        new(1, "beta", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), 5),
        new(2, "Alpha", null, 5),
        new(3, "alpha", new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), 1),
        new(4, "Gamma", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 9)
    ];

    private static int[] Sorted(ListQuery query) => Rows.AsQueryable().ApplySort(Map, query, "name", r => r.Id).Select(r => r.Id).ToArray();

    [Fact]
    public void Parse_AppliesDefaultsAndNormalisesBlankValues()
    {
        var query = ListQuery.Parse(null, null, "  ", "", null);
        Assert.Equal(new ListQuery(1, 25, null, null, null), query);
        Assert.Equal(0, query.Skip);
    }

    [Fact]
    public void Parse_ReadsAllParameters()
    {
        var query = ListQuery.Parse(3, 10, " Bob ", "name", "DESC");
        Assert.Equal(new ListQuery(3, 10, "Bob", "name", SortDirection.Desc), query);
        Assert.Equal(20, query.Skip);
    }

    [Theory]
    [InlineData(0, null, null, "page")]
    [InlineData(-5, null, null, "page")]
    [InlineData(1, 0, null, "pageSize")]
    [InlineData(1, 101, null, "pageSize")]
    [InlineData(1, null, "up", "dir")]
    public void Parse_RejectsBadValuesNamingTheField(long page, int? pageSize, string? dir, string field) =>
        Assert.Equal(field, Assert.Throws<InvalidListQueryException>(() => ListQuery.Parse(page, pageSize, null, null, dir)).Field);

    [Theory]
    [InlineData(1_000_001L, ListQuery.MaxPage)]
    [InlineData(2_147_483_648L, ListQuery.MaxPage)]
    [InlineData(long.MaxValue, ListQuery.MaxPage)]
    [InlineData(1_000_000L, ListQuery.MaxPage)]
    [InlineData(7L, 7)]
    public void Parse_ClampsAnAbsurdPageInsteadOfRefusingIt(long page, int expected)
    {
        var query = ListQuery.Parse(page, 100, null, null, null);
        Assert.Equal(expected, query.Page);
        Assert.Equal((expected - 1) * 100L, query.Skip);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("3", 3L)]
    [InlineData("+3", 3L)]
    [InlineData("2147483648", 2_147_483_648L)]
    [InlineData("99999999999999999999999999", long.MaxValue)]
    [InlineData("-99999999999999999999999999", long.MinValue)]
    public void ParsePage_ReadsAnyWholeNumber(string? raw, long? expected) => Assert.Equal(expected, ListQuery.ParsePage(raw));

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1,2")]
    public void ParsePage_RejectsText(string raw) => Assert.Equal("page", Assert.Throws<InvalidListQueryException>(() => ListQuery.ParsePage(raw)).Field);

    [Fact]
    public void ApplySort_UsesDefaultFieldWhenNoneRequested() => Assert.Equal([2, 3, 1, 4], Sorted(new ListQuery()));

    [Fact]
    public void ApplySort_TextIsCaseInsensitiveAndTiesBreakByTiebreaker()
    {
        Assert.Equal([2, 3, 1, 4], Sorted(new ListQuery(SortField: "name")));
        Assert.Equal([4, 1, 2, 3], Sorted(new ListQuery(SortField: "name", Direction: SortDirection.Desc)));
    }

    [Fact]
    public void ApplySort_DatesDefaultToNewestFirstWithNullsLastBothWays()
    {
        Assert.Equal([3, 1, 4, 2], Sorted(new ListQuery(SortField: "at")));
        Assert.Equal([4, 1, 3, 2], Sorted(new ListQuery(SortField: "at", Direction: SortDirection.Asc)));
    }

    [Fact]
    public void ApplySort_ValuesDefaultToAscendingAndTieBreakDeterministically()
    {
        Assert.Equal([3, 1, 2, 4], Sorted(new ListQuery(SortField: "size")));
        Assert.Equal([4, 1, 2, 3], Sorted(new ListQuery(SortField: "size", Direction: SortDirection.Desc)));
    }

    [Fact]
    public void ApplySort_RejectsFieldOutsideTheMap() =>
        Assert.Equal("sort", Assert.Throws<InvalidListQueryException>(() => Sorted(new ListQuery(SortField: "secret"))).Field);

    [Fact]
    public void SortMap_RejectsDuplicateField() => Assert.ThrowsAny<ArgumentException>(() => SortMap<Row>.Create().Text("a", r => r.Name).Value("a", r => r.Id));

    [Fact]
    public void ApplySearch_MatchesAnyColumnIgnoringCaseAndTreatsWildcardsLiterally()
    {
        var rows = new[] { new Row(1, "50% off", null, 0), new Row(2, "Alpha", null, 0), new Row(3, "a_b", null, 0) };
        int[] Find(string term) => rows.AsQueryable().ApplySearch(new ListQuery(Search: term), r => r.Name).Select(r => r.Id).ToArray();
        Assert.Equal([2], Find("ALPH"));
        Assert.Equal([1], Find("%"));
        Assert.Equal([3], Find("_"));
        Assert.Equal([1, 2, 3], rows.AsQueryable().ApplySearch(new ListQuery(), r => r.Name).Select(r => r.Id).ToArray());
    }
}
