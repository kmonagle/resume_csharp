// Why this file exists: the domain model, free of HTTP and SQL. It holds the one rule
// every implementation of the contract must agree on: "is this link usable, and if
// not, why?". The SQL in EfLinkStore.ClaimAsync applies the same rules; keep them
// in step.
//
// JS/TS vs C#: this file imports nothing from the web or database layers. Keeping
// the domain free of I/O is a convention, but the `using` list makes it easy to check.
namespace LinkApi.Domain;

// JS/TS vs C#: an ENUM is a real runtime type with a fixed set of named values
// (numbers underneath). It is not a string union like TypeScript's
// "active" | "expired": the wire format ("max_clicks") is mapped explicitly below.
public enum LinkStatus
{
    Active,
    Expired,
    MaxClicks,
    Disabled,
}

public static class LinkStatusExtensions
{
    // JS/TS vs C#: an EXTENSION METHOD adds a method to an existing type without
    // touching it: `status.ToWire()` reads like a method on the enum. The `this`
    // keyword on the first parameter is what makes it one. (Same idea as adding to a
    // prototype in JS, but resolved at compile time and scoped by `using`.)
    public static string ToWire(this LinkStatus status) => status switch
    {
        LinkStatus.Active => "active",
        LinkStatus.Expired => "expired",
        LinkStatus.MaxClicks => "max_clicks",
        LinkStatus.Disabled => "disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

// JS/TS vs C#: a RECORD is an immutable data type with value equality. Declaring it
// with a parameter list (a PRIMARY CONSTRUCTOR) creates the constructor and the
// properties in one line: this is `type Link = Readonly<{...}>` plus a class. Two
// records with equal fields are `==`, unlike two JS objects. `string?` and `int?`
// are the NULLABLE forms; the compiler makes you check them before use.
public sealed record Link(
    string Id,
    string ShortCode,
    string TargetUrl,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    int? MaxClicks,
    int ClickCount,
    bool IsActive)
{
    // `now` is a parameter, not DateTimeOffset.UtcNow inside, so the method is
    // deterministic and trivial to test.
    public LinkStatus GetStatus(DateTimeOffset now)
    {
        if (!IsActive)
        {
            return LinkStatus.Disabled;
        }

        // JS/TS vs C#: `ExpiresAt is { } expiresAt` is a PATTERN: "if it is not null,
        // bind its value to expiresAt". It tests and unwraps in one step, where TS
        // would narrow with `if (link.expiresAt && ...)`.
        if (ExpiresAt is { } expiresAt && expiresAt < now)
        {
            return LinkStatus.Expired;
        }

        if (MaxClicks is { } max && ClickCount >= max)
        {
            return LinkStatus.MaxClicks;
        }

        return LinkStatus.Active;
    }
}
