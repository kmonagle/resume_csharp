// Why this file exists: the request shape for POST /links and the validation rules
// for it, the trust boundary for user input. The rules and messages match
// docs/openapi.yaml and the Next.js, Go and Python implementations exactly, because
// the contract tests hold every backend to the same behaviour.
//
// JS/TS vs C#: this is the counterpart of src/shared/schemas/link-schema.ts (zod).
// .NET has attribute-based validation ([Required], [MaxLength]) and libraries such as
// FluentValidation, but their messages and field naming don't line up with the
// contract, so the rules are written out plainly here, which also keeps them
// unit-testable with no framework involved.
using System.Globalization;
using System.Text.RegularExpressions;

namespace LinkApi.Contracts;

// What the JSON deserialises into. Every property is nullable so we can tell "absent"
// from "present", and so a missing field becomes a validation message rather than a
// crash. JSON of the wrong TYPE (a number for targetUrl) fails deserialisation and
// becomes a 400 at the endpoint.
public sealed record CreateLinkRequest(
    string? TargetUrl,
    string? Title,
    string? ExpiresAt,
    int? MaxClicks,
    string? ShortCode);

// A request that passed validation. Later code can trust it.
public sealed record CreateLinkInput(
    string TargetUrl,
    string? Title,
    DateTimeOffset? ExpiresAt,
    int? MaxClicks,
    string? ShortCode);

// JS/TS vs C#: a Dictionary<string, List<string>> is a `Record<string, string[]>`.
// Subclassing it just gives the type a name and an Add helper.
public sealed class ValidationErrors : Dictionary<string, List<string>>
{
    public void Add(string field, string message)
    {
        if (!TryGetValue(field, out var messages))
        {
            messages = [];
            this[field] = messages;
        }

        messages.Add(message);
    }
}

public static partial class CreateLinkValidator
{
    private const int MaxUrlLength = 2048;
    private const int MaxTitleLength = 100;
    private const int MaxMaxClicks = 1_000_000;

    // JS/TS vs C#: [GeneratedRegex] is a SOURCE GENERATOR: at compile time it writes
    // the regex matching code for you, so there is no startup cost and no runtime
    // parsing. The method is `partial`: you declare it, the generator fills the body
    // (hence `partial` on the class too). ATTRIBUTES like this one, in square
    // brackets, attach metadata to code, and tools (the compiler, serialisers, test
    // runners) read them. Think of decorators, but consumed at build time.
    [GeneratedRegex("^[A-Za-z0-9_-]{3,32}$")]
    private static partial Regex ShortCodePattern();

    // Requires an explicit UTC offset (or Z), so the moment is unambiguous.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}:\d{2}(\.\d{1,7})?([Zz]|[+-]\d{2}:\d{2})$")]
    private static partial Regex Rfc3339Pattern();

    public static (CreateLinkInput? Input, ValidationErrors Errors) Validate(
        CreateLinkRequest request, DateTimeOffset now)
    {
        var errors = new ValidationErrors();

        string? targetUrl = null;
        if (request.TargetUrl is null)
        {
            errors.Add("targetUrl", "Required");
        }
        else if (!IsHttpUrl(request.TargetUrl))
        {
            errors.Add("targetUrl", "Enter a valid http(s) URL");
        }
        else
        {
            targetUrl = request.TargetUrl;
        }

        string? title = null;
        if (!IsBlank(request.Title))
        {
            title = request.Title!.Trim();
            // JS/TS vs C#: string.Length counts UTF-16 units (like JS's .length), so
            // an emoji counts as 2. Counting RUNES (Unicode code points) counts what a
            // human sees, and matches the other implementations.
            if (title.EnumerateRunes().Count() > MaxTitleLength)
            {
                errors.Add("title", "Too long");
                title = null;
            }
        }

        DateTimeOffset? expiresAt = null;
        if (!IsBlank(request.ExpiresAt))
        {
            var text = request.ExpiresAt!.Trim();
            // JS/TS vs C#: `out var x` is an OUT PARAMETER: TryParse returns a bool and
            // hands the parsed value back through the argument. It is C#'s way of
            // returning two things; the "Try" prefix marks methods that report failure
            // by return value instead of throwing.
            if (!Rfc3339Pattern().IsMatch(text)
                || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                errors.Add("expiresAt", "Enter a valid date and time");
            }
            else if (parsed <= now)
            {
                errors.Add("expiresAt", "Expiry must be in the future");
            }
            else
            {
                expiresAt = parsed;
            }
        }

        if (request.MaxClicks is { } maxClicks)
        {
            if (maxClicks < 1)
            {
                errors.Add("maxClicks", "Must be at least 1");
            }
            else if (maxClicks > MaxMaxClicks)
            {
                errors.Add("maxClicks", "Must be at most 1,000,000");
            }
        }

        string? shortCode = null;
        if (!IsBlank(request.ShortCode))
        {
            var code = request.ShortCode!.Trim();
            if (ShortCodePattern().IsMatch(code))
            {
                shortCode = code;
            }
            else
            {
                errors.Add("shortCode", "3-32 characters: letters, numbers, - and _");
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        // The compiler can't see that `targetUrl` is non-null whenever `errors` is
        // empty, so the null-forgiving operator `!` says "trust me": a promise to the
        // compiler, checked by nothing at runtime, so use it sparingly.
        return (new CreateLinkInput(targetUrl!, title, expiresAt, request.MaxClicks, shortCode), errors);
    }

    // HTML forms send "" for untouched fields; treat "" and whitespace as absent.
    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static bool IsHttpUrl(string value)
    {
        // Only http(s): a shortener that redirects to `javascript:` or `data:` URLs is
        // an XSS gadget. Uri.TryCreate is lenient about some shapes, so we check the
        // scheme and host ourselves.
        return value.Length <= MaxUrlLength
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host);
    }
}
