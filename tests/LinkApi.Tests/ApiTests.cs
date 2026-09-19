// Why this file exists: tests the parts of the HTTP layer that need no database
// (authentication, the owner header, the open /meta endpoint, the error shapes) by
// starting the whole app in-process with a fake store.
//
// JS/TS vs C#: WebApplicationFactory is supertest with the real app inside it: it
// boots the actual Program.cs pipeline (routing, filters, JSON, error handling) on an
// in-memory server, so these tests exercise exactly what production runs, with only
// the store swapped for a fake through the dependency-injection container.
using System.Net;
using System.Net.Http.Json;
using LinkApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinkApi.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token-0123456789";

    public ApiFactory()
    {
        // Settings are read from the environment when the app starts.
        Environment.SetEnvironmentVariable("DATABASE_URL", "postgres://u:p@localhost/db");
        Environment.SetEnvironmentVariable("LINK_BACKEND_TOKEN", Token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            // Swap the real EF store for the fake. The DbContext is registered but
            // never created, because nothing asks for it.
            services.RemoveAll<ILinkStore>();
            services.AddScoped<ILinkStore, FakeLinkStore>();
        });
}

// JS/TS vs C#: IClassFixture builds the factory ONCE and shares it across this class's
// tests, and the framework hands it to the constructor (xUnit's dependency injection).
public class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token, string? owner = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Add("Authorization", token);
        }

        if (owner is not null)
        {
            request.Headers.Add("X-Owner-Id", owner);
        }

        return request;
    }

    private static string Bearer => $"Bearer {ApiFactory.Token}";

    [Theory]
    [InlineData("GET", "/links")]
    [InlineData("POST", "/links")]
    [InlineData("PATCH", "/links/abc")]
    public async Task Api_routes_need_the_bearer_token(string method, string path)
    {
        var response = await client.SendAsync(Request(new HttpMethod(method), path, token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Missing or invalid bearer token", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Bearer nope")]
    [InlineData("Basic test-token-0123456789")]
    [InlineData("test-token-0123456789")]
    public async Task A_wrong_scheme_or_token_is_rejected(string header)
    {
        var response = await client.SendAsync(Request(HttpMethod.Get, "/links", header, "abc"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("has space")]
    public async Task A_token_without_a_usable_owner_is_a_400(string? owner)
    {
        var response = await client.SendAsync(Request(HttpMethod.Get, "/links", Bearer, owner));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Authentication_is_checked_before_the_body_is_validated()
    {
        var response = await client.PostAsJsonAsync("/links", new { targetUrl = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Validation_errors_use_the_contracts_shape()
    {
        var request = Request(HttpMethod.Post, "/links", Bearer, "owner-1");
        request.Content = JsonContent.Create(new { targetUrl = "javascript:alert(1)" });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Validation failed", body.GetProperty("error").GetString());
        Assert.Equal(
            "Enter a valid http(s) URL",
            body.GetProperty("fieldErrors").GetProperty("targetUrl")[0].GetString());
    }

    [Fact]
    public async Task Unparseable_json_is_a_400_not_a_500()
    {
        var request = Request(HttpMethod.Post, "/links", Bearer, "owner-1");
        request.Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"body\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Meta_is_open_and_briefly_cacheable()
    {
        var response = await client.GetAsync("/meta");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("C# ASP.NET Core + EF Core", await response.Content.ReadAsStringAsync());
        Assert.Equal("public, max-age=60", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task An_unknown_short_code_is_404_without_a_token()
    {
        var response = await client.GetAsync("/r/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
