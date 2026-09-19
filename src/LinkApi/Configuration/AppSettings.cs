// Why this file exists: the service's configuration, as a typed object that is
// validated once at startup, so a missing or malformed setting stops the process
// with a clear message instead of failing on some later request.
//
// JS/TS vs C#: this is the counterpart of src/server/env.ts in the Next.js repo.
// ASP.NET Core's answer is the OPTIONS PATTERN: bind configuration onto a class, and
// inject `IOptions<AppSettings>` wherever it is needed (see Program.cs).
namespace LinkApi.Configuration;

// JS/TS vs C#: `sealed` means "no subclasses". It is the sensible default for a
// class you didn't design for inheritance (and it lets the runtime optimise calls).
public sealed class AppSettings
{
    public const int MinTokenLength = 16;

    public string DatabaseUrl { get; set; } = "";

    // The shared secret the Next.js BFF sends as a bearer token. Render's free tier
    // has no private networking, so this service is publicly reachable; without the
    // token anyone could call it with any X-Owner-Id.
    public string Token { get; set; } = "";

    // JS/TS vs C#: `{ get; set; }` is a PROPERTY: a field with generated accessors.
    // Properties (not public fields) are how C# exposes state, so you can add logic
    // later without changing callers. `= ""` is the initial value.

    /// <summary>Returns a description of every problem, or an empty list if valid.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(DatabaseUrl))
        {
            problems.Add("DATABASE_URL is required");
        }

        if (Token.Length < MinTokenLength)
        {
            problems.Add($"LINK_BACKEND_TOKEN is required (at least {MinTokenLength} characters)");
        }

        return problems;
    }
}
