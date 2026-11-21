using BackupPlatform.Application.Lists;
using BackupPlatform.Infrastructure.Lists;

namespace BackupPlatform.UnitTests.Lists;

/// <summary>The search fold contract: the frontend's <c>foldSearch</c> test uses the same groups, so the two sides cannot drift.</summary>
public sealed class TextFoldTests
{
    // Every string of a group is the same search text; the groups are different from each other.
    public static readonly string[][] Groups =
    [
        ["INDIGO", "İNDİGO", "indigo", "ındıgo", "İndigo", "Indigo"],
        ["Şifreli", "sifreli", "SIFRELI", "ŞİFRELİ", "ŞİFRELI", "şifreli"],
        ["Çağrı", "cagri", "ÇAĞRI", "CAGRI", "çaGRİ"],
        ["Öğüt Göç", "ogut goc", "ÖĞÜT GÖÇ", "OGUT GOC"],
        ["İ", "I", "i", "ı"],
        ["50% off", "50% OFF"],
        ["a_b", "A_B"]
    ];

    public static TheoryData<string, string, bool> Pairs()
    {
        var data = new TheoryData<string, string, bool>();
        for (var g = 0; g < Groups.Length; g++)
        {
            foreach (var a in Groups[g])
            {
                foreach (var b in Groups.SelectMany((group, h) => group.Select(text => (text, same: h == g)))) data.Add(a, b.text, b.same);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Fold_EqualsExactlyForTheSameSearchText(string a, string b, bool same) => Assert.Equal(same, TextFold.Fold(a) == TextFold.Fold(b));

    [Theory]
    [InlineData("İNDİGO", "indigo")]
    [InlineData("ŞİFRELİ", "sifreli")]
    [InlineData("Çağrı Öğüt Ünlü", "cagri ogut unlu")]
    [InlineData("ıI", "ii")]
    public void Fold_IsLowerCaseWithoutTurkishLetters(string text, string expected) => Assert.Equal(expected, TextFold.Fold(text));

    [Fact]
    public void Translate_IsPostgresTranslate()
    {
        Assert.Equal("a2c", TextFold.Translate("abc", "b", "2"));
        Assert.Null(TextFold.Translate(null, "b", "2"));
    }

    private sealed record Row(int Id, string Name, string Other);

    private static int[] Find(IEnumerable<Row> rows, string term) =>
        rows.AsQueryable().ApplySearch(new ListQuery(Search: term), row => row.Name, row => row.Other).Select(row => row.Id).ToArray();

    [Fact]
    public void ApplySearch_FindsEveryWritingOfTheSameText()
    {
        // The one-letter group is a substring of other groups; it is checked below.
        var words = Groups.Select((group, g) => (group, g)).Where(entry => entry.group[0].Length > 1).ToArray();
        var rows = words.SelectMany(entry => entry.group.Select((text, i) => new Row(entry.g * 100 + i, text, ""))).ToArray();
        foreach (var (group, g) in words)
        {
            var expected = group.Select((_, i) => g * 100 + i).ToArray();
            foreach (var term in group) Assert.Equal(expected, Find(rows, term));
        }
    }

    [Theory]
    [InlineData("İ")]
    [InlineData("I")]
    [InlineData("ı")]
    [InlineData("i")]
    public void ApplySearch_AllFourLettersIAreTheSameLetter(string term)
    {
        Row[] rows = [new(1, "ŞİFRELİ", ""), new(2, "ındıgo", ""), new(3, "xyz", "")];
        Assert.Equal([1, 2], Find(rows, term));
    }

    [Fact]
    public void ApplySearch_ContainsMatchesAcrossFoldedLettersAndOtherColumns()
    {
        Row[] rows = [new(1, "Üretim ŞİFRELİ yedek", ""), new(2, "plain", "İstanbul sunucusu"), new(3, "x", "y")];
        Assert.Equal([1], Find(rows, "sifre"));
        Assert.Equal([1], Find(rows, "ŞİFRELI Y"));
        Assert.Equal([2], Find(rows, "ISTANBUL"));
        Assert.Equal([2], Find(rows, "SUNUCU"));
        Assert.Equal([1], Find(rows, "uretim"));
        Assert.Empty(Find(rows, "zzz"));
    }

    [Fact]
    public void ApplySearchOr_AlsoMatchesTheExtraPredicateOnlyWhileSearching()
    {
        Row[] rows = [new(1, "a", ""), new(2, "b", "")];
        var searching = rows.AsQueryable().ApplySearchOr(new ListQuery(Search: "a"), row => row.Id == 2, row => row.Name).Select(row => row.Id).ToArray();
        Assert.Equal([1, 2], searching);
        Assert.Equal([1, 2], rows.AsQueryable().ApplySearchOr(new ListQuery(), row => false, row => row.Name).Select(row => row.Id).ToArray());
    }
}
