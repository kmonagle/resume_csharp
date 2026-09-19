// Why this file exists: small helpers shared by the tests: a fixed clock and a fake
// store, so the tests need no database and no mocking library.
//
// JS/TS vs C#: in Jest you'd `jest.mock` a module. .NET's normal answer is the same as
// Go's and Python's: depend on an INTERFACE (ILinkStore) and hand in a fake. The
// fake below declares `: ILinkStore`, since .NET interfaces are nominal.
using LinkApi.Contracts;
using LinkApi.Domain;
using LinkApi.Services;

namespace LinkApi.Tests;

// TimeProvider is abstract, so a test can subclass it to freeze time.
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public static class Make
{
    public static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // JS/TS vs C#: OPTIONAL PARAMETERS with defaults, and NAMED ARGUMENTS at the call
    // site (`Make.Link(maxClicks: 3)`), let each test change just the field it cares
    // about (much like passing a partial object in TS).
    public static Link Link(
        string code = "abc1234",
        bool isActive = true,
        DateTimeOffset? expiresAt = null,
        int? maxClicks = null,
        int clickCount = 0) =>
        new("1", code, "https://example.com", null, Now, expiresAt, maxClicks, clickCount, isActive);
}

// A store whose behaviour is set through its properties; anything left at its default
// means "nothing special".
public sealed class FakeLinkStore : ILinkStore
{
    public int PerOwner { get; init; }

    public int Total { get; init; }

    // JS/TS vs C#: `init` makes a property settable only while the object is being CREATED
    // (`new FakeLinkStore { TakenCodes = [...] }`), then read-only: immutability by default.
    public HashSet<string> TakenCodes { get; init; } = [];

    public Link? Found { get; init; }

    public ClaimedLink? Claim { get; init; }

    // JS/TS vs C#: `Task.FromResult(x)` wraps a value in an already-completed Task, like
    // `Promise.resolve(x)`: the fake has nothing to await, but the interface demands a Task.
    public Task<Link?> InsertAsync(string ownerId, string code, CreateLinkInput data, CancellationToken ct) =>
        Task.FromResult(TakenCodes.Contains(code) ? null : Make.Link(code));

    public Task<IReadOnlyList<Link>> ListByOwnerAsync(string ownerId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Link>>([]);

    public Task<int> CountByOwnerAsync(string ownerId, CancellationToken ct) => Task.FromResult(PerOwner);

    public Task<int> CountAllAsync(CancellationToken ct) => Task.FromResult(Total);

    public Task DeleteCreatedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.CompletedTask;

    public Task<Link?> SetActiveAsync(string ownerId, string id, bool active, CancellationToken ct) =>
        Task.FromResult<Link?>(null);

    public Task<ClaimedLink?> ClaimAsync(string code, CancellationToken ct) => Task.FromResult(Claim);

    public Task<Link?> FindByCodeAsync(string code, CancellationToken ct) => Task.FromResult(Found);

    public Task InsertClickEventAsync(string linkId, string? referrer, string? userAgent, CancellationToken ct) =>
        Task.CompletedTask;
}
