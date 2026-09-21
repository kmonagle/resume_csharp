// Why this file exists: the ONLY class that runs queries. Everything above it (the
// service, the endpoints) works with plain domain objects, so the queries can be read,
// and audited against the shared schema, in one place. The schema is owned by the
// Next.js repo's migrations; this service never migrates.
//
// JS/TS vs C#: this is the only code that runs queries (Next.js holds no data). Compare
// a typical Drizzle/Prisma data layer in a Node app. Queries are written in LINQ: `db.Links.Where(l => l.OwnerId == owner)` looks
// like array methods, but the lambda is an EXPRESSION TREE, not a function: EF Core
// reads its structure and translates it to SQL. That is why you can only use
// translatable things inside it (a random C# method has no SQL equivalent).
using LinkApi.Contracts;
using LinkApi.Domain;
using LinkApi.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LinkApi.Data;

// All queries run through the ONE DbContext (so one connection) it is given. The
// context is scoped to the request; see Program.cs.
public sealed class EfLinkStore(LinksDbContext db) : ILinkStore
{
    // JS/TS vs C#: `async`/`await` work like JS: Task<T> is Promise<T>. Two
    // differences: there is no separate "then" chain (you only ever await), and the
    // CancellationToken is passed explicitly down the call chain (JS's AbortSignal), so
    // a request the client abandons stops its query.
    public async Task<Link?> InsertAsync(
        string ownerId, string code, CreateLinkInput data, CancellationToken ct)
    {
        var entity = new LinkEntity
        {
            Id = Guid.NewGuid().ToString(),
            ShortCode = code,
            TargetUrl = data.TargetUrl,
            Title = data.Title,
            OwnerId = ownerId,
            ExpiresAt = data.ExpiresAt,
            MaxClicks = data.MaxClicks,
            IsActive = true,
        };
        db.Links.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
            return ToLink(entity);
        }
        // JS/TS vs C#: an EXCEPTION FILTER (`when`) decides whether this catch applies,
        // and the property pattern `{ SqlState: ... }` inspects the inner exception.
        // Unlike Go, C# reports failure with exceptions, so a unique-violation
        // arrives as one. The DATABASE arbitrates a race between two requests for the
        // same code (SQLSTATE 23505): a SELECT-then-INSERT would leave a gap in which
        // both pass the check. (The other implementations use ON CONFLICT DO NOTHING;
        // EF Core has no ON CONFLICT, and catching the violation is the idiomatic
        // equivalent.)
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The context still tracks the failed insert; detach it so the next
            // attempt (a retry with a fresh code) doesn't re-send it.
            db.Entry(entity).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<IReadOnlyList<Link>> ListByOwnerAsync(string ownerId, CancellationToken ct)
    {
        // AsNoTracking: we only read, so skip EF's change tracking. The query builds
        // up (Where, OrderBy) and only runs when awaited (ToListAsync): it is LAZY,
        // like an iterator, until then.
        var entities = await db.Links
            .AsNoTracking()
            .Where(l => l.OwnerId == ownerId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);
        // JS/TS vs C#: `Select(ToLink)` passes a METHOD GROUP (the method itself, as a function
        // value), like `.map(toLink)`. Select is lazy, so `ToList()` forces it to run now.
        return entities.Select(ToLink).ToList();
    }

    // JS/TS vs C#: these methods aren't marked `async` and don't `await`: they just RETURN the
    // Task that EF Core gives them, which is cheaper and equivalent when a method only forwards
    // a promise (in JS, `return db.count()` from a non-async function).
    public Task<int> CountByOwnerAsync(string ownerId, CancellationToken ct) =>
        db.Links.CountAsync(l => l.OwnerId == ownerId, ct);

    public Task<int> CountAllAsync(CancellationToken ct) => db.Links.CountAsync(ct);

    // click_events rows go with their link via the foreign key's ON DELETE CASCADE.
    public Task DeleteCreatedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        db.Links.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(ct);

    // Scoped by owner: another owner's id matches nothing, so the caller sees "not
    // found" and can never toggle a link it does not own.
    public async Task<Link?> SetActiveAsync(
        string ownerId, string id, bool active, CancellationToken ct)
    {
        var entity = await db.Links.FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == ownerId, ct);
        if (entity is null)
        {
            return null;
        }

        // Change tracking in action: assign properties, then SaveChanges writes just
        // what changed. (updated_at is set by hand: only the Drizzle app has an ORM
        // hook that does it automatically.)
        entity.IsActive = active;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToLink(entity);
    }

    /// <summary>
    /// Checks every redeemability rule AND counts the click in ONE statement.
    ///
    /// Loading the link, checking MaxClicks in C#, then saving would let two
    /// concurrent requests both read "9 of 10", both pass, and both redirect (ending
    /// at 11). In a single UPDATE, Postgres locks the row: the second request waits,
    /// then re-evaluates the WHERE clause against the already-incremented row (10) and
    /// matches nothing. The database enforces the limit however many instances or
    /// languages are calling it. Must stay identical to the other implementations.
    ///
    /// EF Core cannot express UPDATE ... RETURNING, so the claim is decided by the
    /// number of ROWS AFFECTED (1 = claimed, 0 = not redeemable); the target URL is
    /// then read with a plain SELECT. That is safe: a link's target never changes, and
    /// the decision itself was already made atomically.
    /// </summary>
    public async Task<ClaimedLink?> ClaimAsync(string code, CancellationToken ct)
    {
        // JS/TS vs C#: ExecuteUpdateAsync runs ONE `UPDATE ... WHERE ...` in the
        // database without loading anything. `l.ClickCount + 1` inside SetProperty is
        // translated to SQL (`click_count = click_count + 1`) and evaluated by
        // Postgres on the current row, NOT a C# addition.
        var claimed = await db.Links
            .Where(l => l.ShortCode == code
                && l.IsActive
                && (l.ExpiresAt == null || l.ExpiresAt > DateTimeOffset.UtcNow)
                && (l.MaxClicks == null || l.ClickCount < l.MaxClicks))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(l => l.ClickCount, l => l.ClickCount + 1)
                    // Written as a lambda (`l => ...UtcNow`), EF translates this to the
                    // database's now(), like the other implementations. Passed as a plain
                    // value (`, DateTimeOffset.UtcNow)`) it would be captured as a
                    // client-side parameter instead: a subtle difference in the SQL.
                    .SetProperty(l => l.UpdatedAt, l => DateTimeOffset.UtcNow),
                ct);

        if (claimed == 0)
        {
            return null;
        }

        // JS/TS vs C#: `Select` projects just the columns we need (like `.map`), so
        // EF only fetches those two.
        return await db.Links
            .AsNoTracking()
            .Where(l => l.ShortCode == code)
            .Select(l => new ClaimedLink(l.Id, l.TargetUrl))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Link?> FindByCodeAsync(string code, CancellationToken ct)
    {
        var entity = await db.Links.AsNoTracking().FirstOrDefaultAsync(l => l.ShortCode == code, ct);
        return entity is null ? null : ToLink(entity);
    }

    public async Task InsertClickEventAsync(
        string linkId, string? referrer, string? userAgent, CancellationToken ct)
    {
        db.ClickEvents.Add(new ClickEventEntity
        {
            Id = Guid.NewGuid().ToString(),
            LinkId = linkId,
            Referrer = referrer,
            UserAgent = userAgent,
        });
        await db.SaveChangesAsync(ct);
    }

    // Map the EF entity to the domain object, so nothing above the store ever sees a
    // tracked EF object.
    private static Link ToLink(LinkEntity e) => new(
        e.Id, e.ShortCode, e.TargetUrl, e.Title, e.CreatedAt, e.ExpiresAt,
        e.MaxClicks, e.ClickCount, e.IsActive);
}
