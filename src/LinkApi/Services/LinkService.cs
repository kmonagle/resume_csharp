// Why this file exists: the business rules, between HTTP (the endpoints) and SQL (the
// store): demo size limits, retention cleanup, short-code generation with retry, and
// how a redirect decides 404 vs 410. It is the C# counterpart of the Next.js app's
// local adapter (src/server/link-api/local.ts) and of the Go and Python services, and
// the contract tests hold all of them to the same behaviour.
using LinkApi.Contracts;
using LinkApi.Domain;

namespace LinkApi.Services;

public sealed record ClaimedLink(string LinkId, string TargetUrl);

// JS/TS vs C#: an INTERFACE is declared explicitly and classes must say they
// implement it (`EfLinkStore : ILinkStore`). This is NOMINAL typing, unlike
// TypeScript's and Go's structural interfaces, where having the right shape is enough.
// The `I` prefix is a .NET naming convention. The service depends on this, not on EF
// Core, so tests hand it a small fake.
public interface ILinkStore
{
    /// <summary>Returns the new link, or null when the short code is already taken.</summary>
    Task<Link?> InsertAsync(string ownerId, string code, CreateLinkInput data, CancellationToken ct);

    Task<IReadOnlyList<Link>> ListByOwnerAsync(string ownerId, CancellationToken ct);

    Task<int> CountByOwnerAsync(string ownerId, CancellationToken ct);

    Task<int> CountAllAsync(CancellationToken ct);

    Task DeleteCreatedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct);

    Task<Link?> SetActiveAsync(string ownerId, string id, bool active, CancellationToken ct);

    /// <summary>Atomically checks the rules and counts the click; null if not redeemable.</summary>
    Task<ClaimedLink?> ClaimAsync(string code, CancellationToken ct);

    Task<Link?> FindByCodeAsync(string code, CancellationToken ct);

    Task InsertClickEventAsync(string linkId, string? referrer, string? userAgent, CancellationToken ct);
}

// JS/TS vs C#: RESULT TYPES. Expected outcomes are values, not exceptions: the endpoint
// decides what "code taken" means for the client. A closed hierarchy of records is the
// C# version of a TS discriminated union: the base has a PRIVATE constructor, and the
// nested cases are the only types that can inherit it. The endpoint branches with a
// `switch` expression over them.
public abstract record CreateResult
{
    private CreateResult()
    {
    }

    public sealed record Created(Link Link) : CreateResult;

    public sealed record CodeTaken : CreateResult;

    public sealed record LimitReached(string Message) : CreateResult;
}

public abstract record FollowResult
{
    private FollowResult()
    {
    }

    public sealed record Followed(string LinkId, string TargetUrl) : FollowResult;

    public sealed record NotFound : FollowResult;

    // The human-readable reason: the contract's 410 body is plain text.
    public sealed record Gone(string Message) : FollowResult;
}

// JS/TS vs C#: a PRIMARY CONSTRUCTOR again: `store` and `clock` become private fields
// available throughout the class. Both arrive by DEPENDENCY INJECTION: the framework
// builds this class and supplies them (ASP.NET Core has a DI container built in, where
// Node needs a library or manual wiring).
//
// TimeProvider is .NET's built-in clock abstraction: production uses the real one,
// tests substitute a fixed clock. No `DateTimeOffset.UtcNow` calls scattered around.
public sealed class LinkService(ILinkStore store, TimeProvider clock)
{
    // Guard rails for a public demo; the same numbers as the other implementations.
    public const int MaxLinksPerOwner = 20;
    public const int MaxLinksTotal = 5000;
    public const int RetentionDays = 30;
    private const int MaxCodeAttempts = 5;

    private static readonly Dictionary<string, string> GoneMessages = new()
    {
        ["expired"] = "This link has expired.",
        ["max_clicks"] = "This link has reached its click limit.",
        ["disabled"] = "This link has been deactivated.",
    };

    public async Task<CreateResult> CreateAsync(
        string ownerId, CreateLinkInput data, CancellationToken ct)
    {
        // Lazy cleanup instead of a cron job: whoever creates a link also sweeps
        // expired demo data.
        await store.DeleteCreatedBeforeAsync(clock.GetUtcNow().AddDays(-RetentionDays), ct);

        // Soft limits: count-then-insert can be overshot by a burst of concurrent
        // requests. Fine for abuse control, unlike max_clicks, which is a correctness
        // guarantee and is enforced atomically in SQL.
        if (await store.CountByOwnerAsync(ownerId, ct) >= MaxLinksPerOwner)
        {
            return new CreateResult.LimitReached($"Demo limit: {MaxLinksPerOwner} links per visitor.");
        }

        if (await store.CountAllAsync(ct) >= MaxLinksTotal)
        {
            return new CreateResult.LimitReached("Demo limit: the service is full right now.");
        }

        // A custom code either works or is "taken"; retrying would not help.
        if (data.ShortCode is { } custom)
        {
            var created = await store.InsertAsync(ownerId, custom, data, ct);
            return created is null ? new CreateResult.CodeTaken() : new CreateResult.Created(created);
        }

        // A generated code that collides is just bad luck: try a fresh one.
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var link = await store.InsertAsync(ownerId, ShortCode.Generate(), data, ct);
            if (link is not null)
            {
                return new CreateResult.Created(link);
            }
        }

        // An unexpected failure IS thrown; the app's exception handler turns it into
        // a 500 with nothing internal in the body.
        throw new InvalidOperationException("Could not generate a unique short code");
    }

    public Task<IReadOnlyList<Link>> ListAsync(string ownerId, CancellationToken ct) =>
        store.ListByOwnerAsync(ownerId, ct);

    /// <summary>Returns null when the link does not exist for this owner.</summary>
    public Task<Link?> SetActiveAsync(string ownerId, string id, bool active, CancellationToken ct) =>
        store.SetActiveAsync(ownerId, id, active, ct);

    public async Task<FollowResult> FollowAsync(string code, CancellationToken ct)
    {
        var claimed = await store.ClaimAsync(code, ct);
        if (claimed is not null)
        {
            return new FollowResult.Followed(claimed.LinkId, claimed.TargetUrl);
        }

        // Nothing was claimed: no such link (404) or it exists but is not redeemable
        // (410). This lookup may be non-atomic because it only chooses the error
        // message; the decision was made atomically above.
        var link = await store.FindByCodeAsync(code, ct);
        if (link is null)
        {
            return new FollowResult.NotFound();
        }

        // The link changed between the two statements (for example it was re-enabled)
        // if it now looks active: report it as unavailable rather than guess.
        var status = link.GetStatus(clock.GetUtcNow());
        var reason = status == LinkStatus.Active ? LinkStatus.Disabled : status;
        return new FollowResult.Gone(GoneMessages[reason.ToWire()]);
    }

    /// <summary>
    /// Logs the analytics row. Separate from FollowAsync so the endpoint can run it
    /// after the redirect has been sent.
    /// </summary>
    public Task RecordClickAsync(string linkId, string? referrer, string? userAgent, CancellationToken ct) =>
        store.InsertClickEventAsync(linkId, referrer, userAgent, ct);
}
