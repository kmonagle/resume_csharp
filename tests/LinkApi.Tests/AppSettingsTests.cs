// Why this file exists: environment mistakes should fail fast and clearly, and the
// token's minimum length is a security rule worth pinning down.
using LinkApi.Configuration;

namespace LinkApi.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Valid_settings_have_no_problems() =>
        Assert.Empty(new AppSettings { DatabaseUrl = "postgres://x/db", Token = "0123456789abcdef" }.Problems());

    [Fact]
    public void Both_settings_are_required() =>
        Assert.Equal(2, new AppSettings().Problems().Count);

    [Fact]
    public void Token_must_be_at_least_sixteen_characters() =>
        Assert.Single(new AppSettings { DatabaseUrl = "postgres://x/db", Token = "too-short" }.Problems());
}
