// Why this file exists: authentication for the API routes. It rejects requests
// without the shared bearer token (401) or without a usable X-Owner-Id (400), and
// hands the owner id to the endpoint.
//
// JS/TS vs C#: this is MIDDLEWARE scoped to a group of routes, the .NET equivalent of
// an Express `router.use(auth)`. Minimal APIs call it an ENDPOINT FILTER: code that
// runs around the handler and can short-circuit by returning a result instead of
// calling `next`. The order matters: the token is checked BEFORE the owner, and
// filters run BEFORE the body is read, so an unauthenticated caller never learns
// anything about the body's validity.
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LinkApi.Configuration;
using LinkApi.Contracts;
using Microsoft.Extensions.Options;

namespace LinkApi.Endpoints;

// Only the HASH of the token is kept in memory.
//
// JS/TS vs C#: `IOptions<AppSettings>` is how the bound configuration reaches a class
// (see Program.cs). This is registered as a SINGLETON (one instance for the whole app)
// because it is stateless and its hash never changes; DI containers have three
// lifetimes: singleton, scoped (one per request) and transient (new every time).
public sealed class TokenVerifier(IOptions<AppSettings> settings)
{
    private readonly byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Value.Token));

    public bool Matches(string presented)
    {
        // Compare SHA-256 hashes with FixedTimeEquals. Hashing makes both sides always
        // 32 bytes (so length leaks nothing), and FixedTimeEquals takes the same time
        // whatever the input, so response timing can't be used to guess the token byte
        // by byte, which a plain `==` would allow.
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}

public sealed partial class RequireOwnerFilter(TokenVerifier tokens) : IEndpointFilter
{
    private const string BearerPrefix = "Bearer ";
    private const string OwnerItemKey = "owner";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex OwnerPattern();

    // JS/TS vs C#: ValueTask is a lighter Task for code that often completes
    // synchronously. `object?` is the handler's return value (an IResult here).
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Live, per-owner data must never be cached by browsers, proxies or the BFF.
        http.Response.Headers.CacheControl = "no-store";

        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal)
            || !tokens.Matches(header[BearerPrefix.Length..]))
        {
            return ApiResults.Error(401, "Missing or invalid bearer token");
        }

        var owner = http.Request.Headers["X-Owner-Id"].ToString();
        if (!OwnerPattern().IsMatch(owner))
        {
            return ApiResults.Error(400, "X-Owner-Id header is required (1-64 chars: letters, numbers, - and _)");
        }

        // HttpContext.Items is a per-request dictionary, the .NET twin of setting
        // `req.user = ...` in Express.
        http.Items[OwnerItemKey] = owner;
        return await next(context);
    }

    // JS/TS vs C#: the `[..]` above is a RANGE: `header[BearerPrefix.Length..]` is
    // "everything from that index on", like `header.slice(n)`.
    // JS/TS vs C#: `(string)x` is a CAST, checked at RUNTIME (a wrong type throws
    // InvalidCastException). TypeScript's `x as string` is only a compile-time claim and
    // vanishes when the code is compiled. The `!` silences the null warning, promising that the
    // filter always sets this before a handler can run.
    public static string GetOwner(HttpContext http) => (string)http.Items[OwnerItemKey]!;
}
