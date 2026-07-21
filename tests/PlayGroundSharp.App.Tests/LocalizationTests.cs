namespace PlayGroundSharp.App.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void JapaneseAndEnglishExposeTheSameResourceKeys()
    {
        var japanese = AppLocalization.Resources(AppLanguageMode.Japanese).Keys.ToHashSet(StringComparer.Ordinal);
        var english = AppLocalization.Resources(AppLanguageMode.English).Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(japanese.Except(english));
        Assert.Empty(english.Except(japanese));
    }

    [Fact]
    public void HelpTopicsStayAlignedAcrossLanguages()
    {
        var japanese = new HelpViewModel(AppLanguageMode.Japanese);
        var english = new HelpViewModel(AppLanguageMode.English);

        Assert.Equal(japanese.Topics.Count, english.Topics.Count);
        Assert.All(japanese.Topics, static topic => Assert.NotEmpty(topic.Sections));
        Assert.All(english.Topics, static topic => Assert.NotEmpty(topic.Sections));
    }
}
