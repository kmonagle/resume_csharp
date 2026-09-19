// Why this file exists: the HTTP layer, and nothing else. It maps requests onto the
// service and service results onto the status codes docs/openapi.yaml promises.
// Business rules live in LinkService; SQL lives in EfLinkStore.
//
// JS/TS vs C#: this is the counterpart of the route handlers under src/app/api in the
// Next.js repo. These are MINIMAL APIs: routes are plain functions registered with
// MapGet/MapPost, in the style of Express, not the older controller classes with
// attributes. Handler parameters are filled in by the framework: services come from
// dependency injection, `HttpContext` is the request/response, and route values such
// as {id} bind by name.
using LinkApi.Contracts;
using LinkApi.Services;

namespace LinkApi.Endpoints;

public static class LinkEndpoints
{
    public const string Implementation = "C# ASP.NET Core + EF Core";
    public const string ContractVersion = "1";

    // JS/TS vs C#: `this IEndpointRouteBuilder app` makes this an extension method, so
    // Program.cs can write `app.MapLinkEndpoints()`.
    public static IEndpointRouteBuilder MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        // A GROUP shares a prefix and a filter: every route inside needs the token.
        var api = app.MapGroup("/links").AddEndpointFilter<RequireOwnerFilter>();
        api.MapPost("", CreateLink);
        api.MapGet("", ListLinks);
        api.MapPatch("/{id}", SetLinkActive);

        // Open endpoints: anonymous visitors follow short links, and /meta is a
        // harmless identity probe (also usable as Render's health check).
        app.MapGet("/r/{code}", FollowLink);
        app.MapGet("/meta", GetMeta);
        return app;
    }

    private static async Task<IResult> CreateLink(HttpContext http, LinkService service, TimeProvider clock)
    {
        var body = await ReadBodyAsync<CreateLinkRequest>(http);
        if (body is null)
        {
            return ApiResults.InvalidBody();
        }

        var now = clock.GetUtcNow();
        var (input, errors) = CreateLinkValidator.Validate(body, now);
        if (input is null)
        {
            return ApiResults.Validation(errors);
        }

        var result = await service.CreateAsync(RequireOwnerFilter.GetOwner(http), input, http.RequestAborted);

        // JS/TS vs C#: a SWITCH EXPRESSION with TYPE PATTERNS: each arm tests the
        // runtime type of `result` and binds it (`Created created`). It is like
        // switching on a discriminated union's tag in TypeScript, and the compiler
        // can warn when a case is missing.
        return result switch
        {
            CreateResult.Created created => Results.Json(LinkDto.From(created.Link, now), statusCode: 201),
            CreateResult.CodeTaken => ApiResults.Error(409, "That code is already taken"),
            CreateResult.LimitReached limited => ApiResults.Error(429, limited.Message),
            // JS/TS vs C#: `throw` is allowed as an EXPRESSION here, as one arm of the switch.
            // `$"..."` is string interpolation, the same as a JS template literal.
            _ => throw new InvalidOperationException($"Unhandled result {result}"),
        };
    }

    private static async Task<IResult> ListLinks(HttpContext http, LinkService service, TimeProvider clock)
    {
        var links = await service.ListAsync(RequireOwnerFilter.GetOwner(http), http.RequestAborted);
        var now = clock.GetUtcNow();
        return Results.Json(links.Select(link => LinkDto.From(link, now)));
    }

    // A tiny request type declared right here, since nothing else uses it. `bool?` is
    // nullable, so a missing field is distinguishable from `false`.
    private sealed record SetActiveRequest(bool? IsActive);

    private static async Task<IResult> SetLinkActive(
        string id, HttpContext http, LinkService service, TimeProvider clock)
    {
        var body = await ReadBodyAsync<SetActiveRequest>(http);
        if (body is null)
        {
            return ApiResults.InvalidBody();
        }

        if (body.IsActive is not { } isActive)
        {
            return ApiResults.Validation(new ValidationErrors { { "isActive", "Required" } });
        }

        var link = await service.SetActiveAsync(RequireOwnerFilter.GetOwner(http), id, isActive, http.RequestAborted);

        // 404, not 403, for someone else's link: do not confirm it exists.
        return link is null
            ? ApiResults.Error(404, "Link not found")
            : Results.Json(LinkDto.From(link, clock.GetUtcNow()));
    }

    private static async Task<IResult> FollowLink(
        string code, HttpContext http, LinkService service, IServiceScopeFactory scopes)
    {
        var result = await service.FollowAsync(code, http.RequestAborted);

        // Never cache: every hit must reach the atomic claim.
        http.Response.Headers.CacheControl = "no-store";

        switch (result)
        {
            case FollowResult.Followed followed:
                var referrer = HeaderOrNull(http, "Referer");
                var userAgent = HeaderOrNull(http, "User-Agent");

                // Log the click AFTER responding so analytics never slows the redirect.
                //
                // JS/TS vs C#: OnCompleted registers a callback that runs once the
                // response has been sent: the counterpart of Next's `after()` and Go's
                // goroutine. It can't use this request's services (the scope, and the
                // DbContext in it, is finished by then), so it opens its own scope. A
                // durable version would queue to a BackgroundService instead; a failed
                // click log is acceptable to lose here, so it is logged and dropped.
                http.Response.OnCompleted(async () =>
                {
                    // JS/TS vs C#: `await using` disposes the scope (and the database context
                    // inside it) when this block ends, even on an exception: like `finally`
                    // with the cleanup written next to the setup. There is no JS keyword for it.
                    await using var scope = scopes.CreateAsyncScope();
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<LinkService>().RecordClickAsync(
                            followed.LinkId, referrer, userAgent, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        // JS/TS vs C#: STRUCTURED LOGGING. The first argument is the exception
                        // (logged with its stack trace) and the second is a message TEMPLATE; the
                        // log system keeps the parts separate, which console.log can't do. Loggers
                        // are created per category ("LinkApi.Clicks") so output can be filtered.
                        scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("LinkApi.Clicks")
                            .LogError(ex, "Recording click event failed");
                    }
                });

                // 307 keeps the request method and never gets cached by the browser
                // as a permanent move.
                return Results.Redirect(followed.TargetUrl, permanent: false, preserveMethod: true);

            case FollowResult.Gone gone:
                // 410 Gone (not 404): the link existed but is no longer available.
                return Results.Text(gone.Message, statusCode: 410);

            default:
                return Results.Text("Not found", statusCode: 404);
        }
    }

    private static IResult GetMeta(HttpContext http)
    {
        // Changes only on redeploy, so briefly cacheable (unlike live link data).
        http.Response.Headers.CacheControl = "public, max-age=60";
        return Results.Json(new { implementation = Implementation, contractVersion = ContractVersion });
    }

    // Reads the JSON body into T. Returns null for anything that is not a JSON body of
    // the expected shape: malformed JSON, a wrong-typed value, an empty body. That is
    // the client's mistake, so it must become a 400 and never a 500.
    private static async Task<T?> ReadBodyAsync<T>(HttpContext http)
        where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(http.RequestAborted);
        }
        // JS/TS vs C#: `is A or B` is a PATTERN with `or`: "ex is any of these types". Catching
        // ONLY these (not every Exception) means a real bug still surfaces as a 500.
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                                       or BadHttpRequestException)
        {
            return null;
        }
    }

    // JS/TS vs C#: a header can carry several values; `.ToString()` joins them, and an
    // empty result means "absent", which becomes null (SQL NULL) for the store.
    private static string? HeaderOrNull(HttpContext http, string name)
    {
        var value = http.Request.Headers[name].ToString();
        return value.Length == 0 ? null : value;
    }
}
