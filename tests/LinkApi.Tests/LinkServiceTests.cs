// Why this file exists: tests the business rules without a database, using the
// hand-written FakeLinkStore. Because the service depends on an interface, the fake is
// short and no mocking library is needed.
using LinkApi.Contracts;
using LinkApi.Domain;
using LinkApi.Services;

namespace LinkApi.Tests;

public class LinkServiceTests
{
    private static readonly CreateLinkInput Data = new("https://a.co", null, null, null, null);

    private static LinkService ServiceWith(FakeLinkStore store) =>
        new(store, new FixedTimeProvider(Make.Now));

    // JS/TS vs C#: `Assert.IsType<T>` both checks the runtime type and returns the value
    // as that type, so the next line can read its properties. It's how tests unpack
    // the result records (the C# stand-in for a discriminated union).
    // JS/TS vs C#: two small features on the next lines. `new() { PerOwner = ... }` is a
    // TARGET-TYPED `new` with an OBJECT INITIALIZER: the compiler knows the type from the
    // parameter, and the braces set properties (no constructor arguments needed). And `default`
    // is the type's zero value: for a CancellationToken it means "never cancelled", which is
    // what a test wants.
    [Fact]
    public async Task Create_enforces_limits()
    {
        var perOwner = await ServiceWith(new() { PerOwner = LinkService.MaxLinksPerOwner })
            .CreateAsync("o", Data, default);
        Assert.IsType<CreateResult.LimitReached>(perOwner);

        var total = await ServiceWith(new() { Total = LinkService.MaxLinksTotal }).CreateAsync("o", Data, default);
        Assert.IsType<CreateResult.LimitReached>(total);

        var fine = await ServiceWith(new()).CreateAsync("o", Data, default);
        Assert.IsType<CreateResult.Created>(fine);
    }

    [Fact]
    public async Task A_taken_custom_code_is_reported()
    {
        var store = new FakeLinkStore { TakenCodes = ["promo"] };
        var result = await ServiceWith(store).CreateAsync("o", Data with { ShortCode = "promo" }, default);
        Assert.IsType<CreateResult.CodeTaken>(result);
    }

    [Fact]
    public async Task Following_a_claimed_link_redirects()
    {
        var store = new FakeLinkStore { Claim = new ClaimedLink("id-1", "https://target") };
        var result = Assert.IsType<FollowResult.Followed>(await ServiceWith(store).FollowAsync("x", default));
        Assert.Equal("https://target", result.TargetUrl);
    }

    [Fact]
    public async Task An_unknown_code_is_not_found() =>
        Assert.IsType<FollowResult.NotFound>(await ServiceWith(new()).FollowAsync("x", default));

    [Fact]
    public async Task A_gone_link_says_why()
    {
        var store = new FakeLinkStore { Found = Make.Link(maxClicks: 1, clickCount: 1) };
        var result = Assert.IsType<FollowResult.Gone>(await ServiceWith(store).FollowAsync("x", default));
        Assert.Equal("This link has reached its click limit.", result.Message);
    }

    [Fact]
    public async Task A_link_that_looks_active_after_a_failed_claim_is_never_redirected()
    {
        // The link changed between the two statements: report unavailable, don't guess.
        var store = new FakeLinkStore { Found = Make.Link() };
        Assert.IsType<FollowResult.Gone>(await ServiceWith(store).FollowAsync("x", default));
    }
}
