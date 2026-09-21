// Why this file exists: the program's entry point. It builds the app: reads and
// validates configuration, registers services with the dependency-injection container,
// sets up the middleware pipeline, and maps the routes. Anything more interesting
// belongs in the other files.
//
// JS/TS vs C#: there are no `main()` or `class Program` here: TOP-LEVEL STATEMENTS mean
// the file's statements simply run in order, like a Node script. `WebApplication` is
// Express's `app`, but with a built-in dependency-injection container and a
// configuration system (environment variables, JSON files) attached.
//
// Ten things that are different from JS/TS, all of which show up in this codebase:
//
//  1. COMPILED AND STATICALLY TYPED. `dotnet build` turns the code into DLLs, and type errors
//     stop the build (nothing is "any" unless you ask). `var x = ...` still has a fixed type; the
//     compiler just infers it. There is no separate type-checking step to forget, as with tsc.
//  2. NAMESPACES AND CONVENTIONS. `namespace X;` at the top of a file, `using` to import. Types,
//     methods and properties are PascalCase; locals and parameters are camelCase; interfaces start
//     with `I`. Access is explicit: `public`, `private`, `internal` (the default for types:
//     visible only inside the same project), `sealed` (no subclasses), `static`.
//  3. NULL IS TRACKED BY THE COMPILER. There is only `null` (no undefined). With nullable
//     reference types on, `string` can never be null and `string?` can, and the compiler warns
//     (here: errors) when you might dereference a null. `?.` and `??` work as in JS; `!` says
//     "trust me, it's not null" and is checked by nothing at runtime.
//  4. VALUE TYPES VS REFERENCE TYPES. `int`, `bool`, `DateTimeOffset` and structs are COPIED on
//     assignment; classes and records are shared references. `int?` is a real wrapper type
//     (Nullable<int>), not a union with null. Records compare by value; classes by identity.
//  5. REAL CLASSES, PROPERTIES, INTERFACES. State is exposed as properties (`{ get; set; }`), not
//     public fields. Interfaces are NOMINAL: a class must say `: IThing` to implement one (unlike
//     TypeScript's and Go's structural typing). `enum`, `record` and `interface` are all
//     first-class runtime types, not erased at build time like TS types.
//  6. EXCEPTIONS ARE TYPED AND NORMAL. `try`/`catch (SpecificException)`/`finally`, with `when`
//     filters. Unlike Go, failure is thrown, not returned, but this code still returns EXPECTED
//     outcomes (code taken, limit reached) as values, and reserves exceptions for real failures.
//  7. DISPOSAL IS DETERMINISTIC. Things holding a database connection or scope implement
//     IDisposable/IAsyncDisposable, and `using` / `await using` releases them at the end of the
//     block. The garbage collector frees memory, but it is not relied on to close connections.
//  8. ASYNC LOOKS LIKE JS BUT IS DIFFERENT UNDER THE HOOD. `Task<T>` is Promise<T> and `await`
//     is the same, but continuations run on a THREAD POOL (ASP.NET Core serves requests on many
//     threads, unlike Node's single event loop), a CancellationToken is passed explicitly, and
//     you never block on a Task (`.Result`, `.Wait()`): that ties up a thread and can deadlock.
//  9. LINQ. `Where`/`Select`/`OrderBy` read like array methods, but they are LAZY (nothing runs
//     until you enumerate with `ToList()`, `First()`, `foreach`), and on a database query the
//     lambdas are expression trees that EF Core translates to SQL rather than running them.
// 10. THE FRAMEWORK COMES WITH THE LANGUAGE. Dependency injection, configuration, logging and
//     the middleware pipeline are built in (Node needs libraries for each), and `dotnet` is the
//     single CLI for build, test, format and packages (NuGet, the npm of .NET).
using LinkApi.Configuration;
using LinkApi.Data;
using LinkApi.Endpoints;
using LinkApi.Services;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Render injects PORT; locally it defaults to 8080. The official .NET images already set
// ASPNETCORE_HTTP_PORTS (Kestrel's own port setting), so we overwrite THAT with PORT rather
// than calling UseUrls: mixing the two makes ASP.NET print an "Overriding HTTP_PORTS" warning
// on every start.
// JS/TS vs C#: `??` is the same null-coalescing operator as in JS: the right side is used only
// when the left is null (an unset environment variable reads as null, not "").
builder.Configuration["HTTP_PORTS"] = Environment.GetEnvironmentVariable("PORT") ?? "8080";
// Cap request bodies at 1 MiB so a huge upload can't exhaust memory.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 1 << 20);

// --- configuration ---------------------------------------------------------------
// JS/TS vs C#: the OPTIONS PATTERN. Configuration values (here, environment variables)
// are bound onto the AppSettings class, validated, and made injectable as
// IOptions<AppSettings>. ValidateOnStart makes a bad setting stop the process at
// startup with a clear message, not fail later on some request.
builder.Services
    .AddOptions<AppSettings>()
    .Configure<IConfiguration>((settings, config) =>
    {
        settings.DatabaseUrl = config["DATABASE_URL"] ?? "";
        settings.Token = config["LINK_BACKEND_TOKEN"] ?? "";
    })
    .Validate(
        settings => settings.Problems().Count == 0,
        "Invalid configuration: DATABASE_URL is required and LINK_BACKEND_TOKEN must be at least 16 characters")
    .ValidateOnStart();

// --- JSON ------------------------------------------------------------------------
// JS/TS vs C#: System.Text.Json is lenient about types by default only where told to be;
// we make numbers strict, so `"maxClicks": "5"` (a string) is a 400 like the contract
// says, instead of being quietly read as 5.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

// --- services (dependency injection) ------------------------------------------------
// JS/TS vs C#: registering a service tells the container how to build it. Lifetimes:
//   Singleton: one instance for the whole app   Scoped: one per request
//   Transient: a new instance every time it is asked for
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenVerifier>();

// DbContext is scoped: one per request, so one connection and one unit of work per
// request. The options callback runs when the first DbContext is created (not at
// startup), so building the app never touches the database, which is what lets tests
// start it without one.
builder.Services.AddDbContext<LinksDbContext>((services, options) =>
{
    var settings = services.GetRequiredService<IOptions<AppSettings>>().Value;
    options
        .UseNpgsql(DatabaseUrl.ToConnectionString(settings.DatabaseUrl))
        // Turns `ShortCode` into the column `short_code`, matching the shared schema.
        .UseSnakeCaseNamingConvention();
});
builder.Services.AddScoped<ILinkStore, EfLinkStore>();
builder.Services.AddScoped<LinkService>();

var app = builder.Build();

// --- middleware pipeline ---------------------------------------------------------------
// JS/TS vs C#: `app.Use...` calls build the pipeline in order, exactly like Express
// middleware. This handler turns any unhandled exception into a contract-shaped 500
// with nothing internal in the body (the real cause is logged).
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.Headers.CacheControl = "no-store";
    await context.Response.WriteAsJsonAsync(new { error = "Internal error" });
}));

app.MapLinkEndpoints();

app.Run();

// JS/TS vs C#: top-level statements generate a hidden `Program` class. Declaring it
// `public partial` here lets the test project reference it (WebApplicationFactory<Program>
// starts the whole app in-process for tests).
public partial class Program
{
}
