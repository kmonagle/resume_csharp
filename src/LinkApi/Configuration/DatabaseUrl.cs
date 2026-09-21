// Why this file exists: Neon and Render hand out a database URL
// (postgres://user:pw@host/db?sslmode=require), but Npgsql, the .NET Postgres
// driver, wants a key=value connection string. This converts one to the other and
// applies the settings that matter for a shared, PgBouncer-fronted database.
//
// JS/TS vs C#: Node's postgres.js driver accepts a URL directly; Npgsql does not, and it
// has no built-in URL parsing, so we do it here.
using System.Web;
using Npgsql;

namespace LinkApi.Configuration;

public static class DatabaseUrl
{
    public static string ToConnectionString(string url)
    {
        // Already a key=value string (for example "Host=...;Database=..."): use as is.
        if (!url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var uri = new Uri(url);
        // JS/TS vs C#: `Split(':', 2)` limits the split to two parts, so a password
        // that itself contains ':' survives. `var` infers the type (string[]): it is
        // still statically typed, unlike a JS variable.
        var credentials = uri.UserInfo.Split(':', 2);

        // JS/TS vs C#: an OBJECT INITIALIZER sets properties right after construction,
        // like `Object.assign(new Builder(), {...})` but checked by the compiler.
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            // URL credentials are percent-encoded; the connection string is not.
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null,

            // Small pool: several services share one database.
            MaxPoolSize = 5,
            // Neon's pooled URL goes through PgBouncer in transaction mode. Npgsql
            // normally resets a connection (DISCARD ALL) each time it returns to its
            // pool, which is wrong when PgBouncer is what owns the server connection.
            // (The .NET twin of the Next.js app's `prepare: false`: Npgsql doesn't
            // prepare statements automatically, so nothing else is needed.)
            NoResetOnClose = true,
            // Npgsql otherwise tries to load a Kerberos library (libgssapi_krb5) on the first
            // connection. The runtime image doesn't ship it, and although the failure is
            // harmless it prints an alarming "Error: cannot open shared object file" line.
            // We authenticate with a password and never use GSS.
            GssEncryptionMode = GssEncryptionMode.Disable,
        };

        // libpq's `sslmode` becomes Npgsql's SslMode. `channel_binding` (which Neon's
        // copy button adds) is simply never read, so it is ignored.
        var query = HttpUtility.ParseQueryString(uri.Query);
        // JS/TS vs C#: a SWITCH EXPRESSION is an expression, not a statement: it
        // returns a value, and `_` is the default arm. There is no fall-through.
        builder.SslMode = query["sslmode"] switch
        {
            "disable" => SslMode.Disable,
            "require" => SslMode.Require,
            "verify-ca" => SslMode.VerifyCA,
            "verify-full" => SslMode.VerifyFull,
            _ => builder.SslMode, // unspecified: keep Npgsql's default (Prefer)
        };

        return builder.ConnectionString;
    }
}
