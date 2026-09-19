// Why this file exists: validation is the trust boundary for user input. These tests
// cover the security-relevant cases (URL schemes) and the form-specific ones (blank
// fields, code format, expiry), matching the Go, Python and Next.js suites.
using LinkApi.Contracts;

namespace LinkApi.Tests;

public class CreateLinkValidatorTests
{
    private static readonly DateTimeOffset Now = Make.Now;

    // JS/TS vs C#: `with` copies a record while changing some fields: a non-destructive
    // update, like `{ ...ok, title: "x" }`.
    private static readonly CreateLinkRequest Ok = new("https://example.com/path", null, null, null, null);

    private static ValidationErrors ErrorsFor(CreateLinkRequest request) =>
        CreateLinkValidator.Validate(request, Now).Errors;

    [Fact]
    public void Accepts_a_minimal_link()
    {
        var (input, errors) = CreateLinkValidator.Validate(Ok, Now);
        Assert.Empty(errors);
        Assert.Equal("https://example.com/path", input!.TargetUrl);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ftp://x.com/a")]
    [InlineData("nope")]
    [InlineData("http://")]
    public void Rejects_non_http_targets(string url) =>
        Assert.Contains("targetUrl", ErrorsFor(Ok with { TargetUrl = url }).Keys);

    [Fact]
    public void Target_url_is_required() =>
        Assert.Equal("Required", ErrorsFor(Ok with { TargetUrl = null })["targetUrl"][0]);

    [Fact]
    public void Blank_optional_fields_are_treated_as_absent()
    {
        var (input, errors) = CreateLinkValidator.Validate(
            Ok with { Title = "  ", ExpiresAt = "", ShortCode = "" }, Now);
        Assert.Empty(errors);
        Assert.Null(input!.Title);
        Assert.Null(input.ExpiresAt);
        Assert.Null(input.ShortCode);
    }

    [Fact]
    public void Title_is_trimmed_and_capped()
    {
        Assert.Equal("Docs", CreateLinkValidator.Validate(Ok with { Title = "  Docs  " }, Now).Input!.Title);
        Assert.Contains("title", ErrorsFor(Ok with { Title = new string('x', 101) }).Keys);
    }

    [Fact]
    public void Title_length_counts_characters_not_utf16_units() =>
        // 100 emoji is 200 UTF-16 units but 100 characters: still valid.
        Assert.Empty(ErrorsFor(Ok with { Title = string.Concat(Enumerable.Repeat("😀", 100)) }));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2_000_000)]
    public void Max_clicks_must_be_in_range(int value) =>
        Assert.Contains("maxClicks", ErrorsFor(Ok with { MaxClicks = value }).Keys);

    [Fact]
    public void Max_clicks_accepts_a_valid_number() =>
        Assert.Equal(5, CreateLinkValidator.Validate(Ok with { MaxClicks = 5 }, Now).Input!.MaxClicks);

    [Theory]
    [InlineData("my-promo_1", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("has space", false)]
    [InlineData("a/b/c", false)]
    public void Short_code_format(string code, bool valid) =>
        Assert.Equal(valid, !ErrorsFor(Ok with { ShortCode = code }).ContainsKey("shortCode"));

    [Fact]
    public void Expiry_must_be_an_iso_datetime_in_the_future()
    {
        var future = Now.AddHours(1).ToString("o");
        var past = Now.AddHours(-1).ToString("o");
        Assert.NotNull(CreateLinkValidator.Validate(Ok with { ExpiresAt = future }, Now).Input!.ExpiresAt);
        Assert.Contains("expiresAt", ErrorsFor(Ok with { ExpiresAt = past }).Keys);
        Assert.Contains("expiresAt", ErrorsFor(Ok with { ExpiresAt = "2030-01-01" }).Keys); // no time, no offset
    }
}
