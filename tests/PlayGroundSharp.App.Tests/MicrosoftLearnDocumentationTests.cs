namespace PlayGroundSharp.App.Tests;

public sealed class MicrosoftLearnDocumentationTests
{
    [Theory]
    [InlineData(AppLanguageMode.Japanese, "net10.0", "https://learn.microsoft.com/ja-jp/dotnet/api/system.string.contains?view=net-10.0")]
    [InlineData(AppLanguageMode.English, "net9.0", "https://learn.microsoft.com/en-us/dotnet/api/system.string.contains?view=net-9.0")]
    [InlineData(AppLanguageMode.Japanese, "net10.0-windows", "https://learn.microsoft.com/ja-jp/dotnet/api/system.string.contains?view=net-10.0")]
    public void CreatesLocalizedMicrosoftLearnUri(
        AppLanguageMode language,
        string targetFramework,
        string expected)
    {
        var uri = MicrosoftLearnDocumentation.CreateUri(
            "system.string.contains",
            language,
            targetFramework);

        Assert.Equal(expected, uri.AbsoluteUri);
    }
}
