// Why this file exists: the URL Neon hands out does not work with Npgsql as-is. These
// tests pin down the conversion, so a change can't quietly break production.
using LinkApi.Configuration;
using Npgsql;

namespace LinkApi.Tests;

public class DatabaseUrlTests
{
    private const string Neon =
        "postgres://user:p%40ss@ep-x-pooler.neon.tech/db?sslmode=require&channel_binding=require";

    [Fact]
    public void Url_is_converted_to_a_connection_string()
    {
        var builder = new NpgsqlConnectionStringBuilder(DatabaseUrl.ToConnectionString(Neon));
        Assert.Equal("ep-x-pooler.neon.tech", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("db", builder.Database);
        Assert.Equal("user", builder.Username);
        Assert.Equal("p@ss", builder.Password); // percent-decoded
    }

    [Fact]
    public void Sslmode_is_translated_and_channel_binding_ignored()
    {
        var builder = new NpgsqlConnectionStringBuilder(DatabaseUrl.ToConnectionString(Neon));
        Assert.Equal(SslMode.Require, builder.SslMode);
        Assert.DoesNotContain("channel", builder.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pool_is_small_and_pgbouncer_safe()
    {
        var builder = new NpgsqlConnectionStringBuilder(DatabaseUrl.ToConnectionString(Neon));
        Assert.Equal(5, builder.MaxPoolSize);
        Assert.True(builder.NoResetOnClose);
        Assert.Equal(GssEncryptionMode.Disable, builder.GssEncryptionMode);
    }

    [Fact]
    public void Explicit_port_and_default_ssl_when_unspecified()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            DatabaseUrl.ToConnectionString("postgres://u:p@localhost:54329/links"));
        Assert.Equal(54329, builder.Port);
        Assert.Equal(new NpgsqlConnectionStringBuilder().SslMode, builder.SslMode);
    }

    [Fact]
    public void A_key_value_string_passes_through_unchanged() =>
        Assert.Equal("Host=h;Database=d", DatabaseUrl.ToConnectionString("Host=h;Database=d"));
}
