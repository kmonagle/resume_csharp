// Why this file exists: pins down the domain rules (status precedence, code
// generation) that must match the contract and the other implementations.
//
// JS/TS vs C#: xUnit is the mainstream .NET test framework (Jest's role). A test is a
// method marked with the [Fact] ATTRIBUTE, and there is no `describe`/`it` nesting:
// the class is the group. [Theory] + [InlineData] is Jest's `test.each`: one method,
// many cases. Assertions are static methods on `Assert`, and `dotnet test` finds and
// runs everything.
using System.Text.RegularExpressions;
using LinkApi.Domain;

namespace LinkApi.Tests;

public class DomainTests
{
    private static readonly DateTimeOffset Past = Make.Now.AddHours(-1);

    [Fact]
    public void Link_is_active_by_default() =>
        Assert.Equal(LinkStatus.Active, Make.Link().GetStatus(Make.Now));

    [Fact]
    public void Disabled_beats_expired() =>
        Assert.Equal(LinkStatus.Disabled, Make.Link(isActive: false, expiresAt: Past).GetStatus(Make.Now));

    [Fact]
    public void Link_is_expired_once_expiry_has_passed() =>
        Assert.Equal(LinkStatus.Expired, Make.Link(expiresAt: Past).GetStatus(Make.Now));

    [Theory]
    [InlineData(2, LinkStatus.Active)]
    [InlineData(3, LinkStatus.MaxClicks)]
    [InlineData(4, LinkStatus.MaxClicks)]
    public void Click_limit_is_reached_exactly_at_the_limit(int clicks, LinkStatus expected) =>
        Assert.Equal(expected, Make.Link(maxClicks: 3, clickCount: clicks).GetStatus(Make.Now));

    [Fact]
    public void Expired_beats_max_clicks() =>
        Assert.Equal(
            LinkStatus.Expired,
            Make.Link(expiresAt: Past, maxClicks: 1, clickCount: 1).GetStatus(Make.Now));

    [Theory]
    [InlineData(LinkStatus.Active, "active")]
    [InlineData(LinkStatus.Expired, "expired")]
    [InlineData(LinkStatus.MaxClicks, "max_clicks")]
    [InlineData(LinkStatus.Disabled, "disabled")]
    public void Status_has_the_contracts_wire_names(LinkStatus status, string wire) =>
        Assert.Equal(wire, status.ToWire());

    [Fact]
    public void Generated_codes_are_seven_base62_characters_and_random()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => ShortCode.Generate()).ToHashSet();
        Assert.All(codes, code => Assert.Matches(new Regex("^[0-9A-Za-z]{7}$"), code));
        // 62^7 possibilities: a collision within 200 draws would mean a broken RNG.
        Assert.Equal(200, codes.Count);
    }
}
