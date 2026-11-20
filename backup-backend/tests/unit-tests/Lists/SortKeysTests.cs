using BackupPlatform.Application.Lists;
using BackupPlatform.Infrastructure.Lists;

namespace BackupPlatform.UnitTests.Lists;

public sealed class SortKeysTests
{
    private enum Color { Red, Green, Blue, Unlisted }

    private sealed record Row(int Id, string? Name, Color Color, Color? Maybe = null);

    private static int[] Sorted(IEnumerable<Row> rows, SortMap<Row> map, string field, SortDirection? dir = null) =>
        rows.AsQueryable().ApplySort(map, new ListQuery(SortField: field, Direction: dir), field, row => row.Id).Select(row => row.Id).ToArray();

    [Fact]
    public void Ordinal_SortsByTheDisplayOrderNotTheStoredOrder()
    {
        var map = SortMap<Row>.Create().Ordinal("color", row => row.Color, Color.Blue, Color.Red, Color.Green);
        Row[] rows = [new(1, null, Color.Red), new(2, null, Color.Green), new(3, null, Color.Blue), new(4, null, Color.Red)];
        Assert.Equal([3, 1, 4, 2], Sorted(rows, map, "color"));
        Assert.Equal([2, 1, 4, 3], Sorted(rows, map, "color", SortDirection.Desc));
    }

    [Fact]
    public void Ordinal_SortsAnUnlistedValueAfterTheListedOnesAndNullLastBothWays()
    {
        var map = SortMap<Row>.Create().Ordinal("maybe", row => row.Maybe, Color.Green, Color.Red);
        Row[] rows = [new(1, null, Color.Red, Color.Red), new(2, null, Color.Red), new(3, null, Color.Red, Color.Green), new(4, null, Color.Red, Color.Blue)];
        Assert.Equal([3, 1, 4, 2], Sorted(rows, map, "maybe"));
        Assert.Equal([4, 1, 3, 2], Sorted(rows, map, "maybe", SortDirection.Desc));
    }

    [Fact]
    public void Natural_OrdersDigitRunsByValue()
    {
        var map = SortMap<Row>.Create().Natural("name", row => row.Name);
        string[] names = ["Preview", "20 GB", "10 GB", "Legacy Unlimited", "5 GB", "1 GB", "plan 2 b", "Plan 10 a", "plan 10", "Plan 9"];
        var rows = names.Select((name, i) => new Row(i, name, Color.Red)).ToArray();
        Assert.Equal(["1 GB", "5 GB", "10 GB", "20 GB", "Legacy Unlimited", "plan 2 b", "Plan 9", "plan 10", "Plan 10 a", "Preview"], Sorted(rows, map, "name").Select(id => names[id]));
        Assert.Equal(["Preview", "Plan 10 a", "plan 10", "Plan 9", "plan 2 b", "Legacy Unlimited", "20 GB", "10 GB", "5 GB", "1 GB"], Sorted(rows, map, "name", SortDirection.Desc).Select(id => names[id]));
    }

    [Fact]
    public void Label_ReplacesAKnownKeyAndKeepsAnUnknownOne()
    {
        var label = SortKeys.Label<Row>(row => row.Name, new Dictionary<string, string> { ["sftp"] = "SFTP", ["local"] = "Local (Agent diski)" }).Compile();
        Assert.Equal("Local (Agent diski)", label(new(1, "local", Color.Red)));
        Assert.Equal("SFTP", label(new(1, "sftp", Color.Red)));
        Assert.Equal("future", label(new(1, "future", Color.Red)));
    }
}
