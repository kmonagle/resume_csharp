// Why this file exists: the response shapes (the wire format) and the helpers that
// build contract-shaped errors, so every endpoint answers failures the same way
// instead of improvising.
using System.Globalization;
using LinkApi.Domain;

namespace LinkApi.Contracts;

// The wire format for a link (the contract's `Link` schema).
//
// JS/TS vs C#: a separate type from the domain `Link` on purpose, the same split as
// toLinkDto() in the Next.js repo: the domain type holds real DateTimeOffsets, the
// wire type holds ISO strings. System.Text.Json turns these PascalCase properties into
// camelCase JSON automatically (the web default), so `ShortCode` is sent as
// "shortCode".
public sealed record LinkDto(
    string Id,
    string ShortCode,
    string TargetUrl,
    string? Title,
    string CreatedAt,
    string? ExpiresAt,
    int? MaxClicks,
    int ClickCount,
    bool IsActive,
    string Status)
{
    // JS/TS vs C#: `=>` after a method signature is an EXPRESSION-BODIED MEMBER: a method whose
    // whole body is one expression, returned implicitly (an arrow function's short form, but on
    // a method). `new(...)` with no type name is TARGET-TYPED: the compiler knows the type is
    // LinkDto from the return type.
    public static LinkDto From(Link link, DateTimeOffset now) => new(
        link.Id,
        link.ShortCode,
        link.TargetUrl,
        link.Title,
        Iso(link.CreatedAt),
        link.ExpiresAt is { } expiresAt ? Iso(expiresAt) : null,
        link.MaxClicks,
        link.ClickCount,
        link.IsActive,
        link.GetStatus(now).ToWire());

    // Millisecond precision, UTC, "Z" suffix: identical to JavaScript's
    // Date.toISOString(), so every backend serialises timestamps the same way.
    // (InvariantCulture stops the machine's locale from changing the format.)
    private static string Iso(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

public sealed record ErrorResponse(string Error);

public sealed record ValidationErrorResponse(string Error, ValidationErrors FieldErrors);

// Results.Json builds an IResult: a description of the response that ASP.NET Core
// executes after the handler returns (compare returning `Response.json(...)` from a
// Next.js route handler).
public static class ApiResults
{
    public static IResult Error(int status, string message) =>
        Results.Json(new ErrorResponse(message), statusCode: status);

    public static IResult Validation(ValidationErrors errors) =>
        Results.Json(new ValidationErrorResponse("Validation failed", errors), statusCode: 400);

    // A body that isn't JSON, or has values of the wrong type, is the CLIENT's mistake:
    // it must be a 400, never a 500.
    public static IResult InvalidBody() =>
        Validation(new ValidationErrors { { "body", "Body must be valid JSON of the expected shape" } });
}
