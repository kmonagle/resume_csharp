// Why this file exists: the program's entry point. It builds the app: reads and
// validates configuration, registers services with the dependency-injection container,
// sets up the middleware pipeline, and maps the routes. Anything more interesting
// belongs in the other files.
//
// JS/TS vs C#: there are no `main()` or `class Program` here: TOP-LEVEL STATEMENTS mean
// the file's statements simply run in order, like a Node script. `WebApplication` is
// Express's `app`, but with a built-in dependency-injection container and a
// configuration system (environment variables, JSON files) attached.
using LinkApi.Configuration;
using LinkApi.Data;
using LinkApi.Endpoints;
using LinkApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Render injects PORT; locally it defaults to 8080.
builder.WebHost.UseUrls($"http://+:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}");
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
