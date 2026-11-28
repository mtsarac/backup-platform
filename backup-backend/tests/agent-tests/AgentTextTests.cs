using System.Globalization;
using System.Text.RegularExpressions;
using BackupPlatform.Agent.Host;

namespace BackupPlatform.AgentTests;

public sealed partial class AgentTextTests
{
    [GeneratedRegex(@"\b(the|and|was|not|with|for|could|must|check|backend|failed|please)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishWords();

    [GeneratedRegex("[ğüşıöçĞÜŞİÖÇ]")]
    private static partial Regex TurkishLetters();

    [Fact]
    public void Every_message_exists_in_both_languages_and_each_text_is_in_its_own_language()
    {
        Assert.Equal(Enum.GetValues<AgentMessage>().Length, AgentText.All.Count);
        foreach (var (message, (english, turkish)) in AgentText.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(english) || string.IsNullOrWhiteSpace(turkish), $"{message} is empty");
            Assert.NotEqual(english, turkish);
            Assert.DoesNotMatch(TurkishLetters(), english);
            Assert.DoesNotMatch(EnglishWords(), turkish);
        }
    }

    [Theory]
    [InlineData("tr", "en", "en-US", "tr")]
    [InlineData(null, "tr", "en-US", "tr")]
    [InlineData(null, null, "tr-TR", "tr")]
    [InlineData(null, null, "en-US", "en")]
    [InlineData("fr", "de", "fr-FR", "en")]
    public void Language_comes_from_the_option_then_the_environment_then_the_ui_culture(string? option, string? environment, string culture, string expected) =>
        Assert.Equal(expected, AgentText.Resolve(option, environment, CultureInfo.GetCultureInfo(culture)));
}
