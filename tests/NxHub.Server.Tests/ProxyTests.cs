using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace NxHub.Server.Tests;

/// <summary>Running behind a reverse proxy: sub-path hosting and proxy-authenticated users.</summary>
public class ProxyTests
{
    private const string UserHeader = "X-Site-User";

    /// <summary>Makes every in-memory request look like it came from a proxy on this machine.</summary>
    private sealed class LoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private static ServerFixture BehindProxy(bool trustHeader = true, bool loopback = true) => new(configure: web =>
    {
        web.UseSetting("NxHub:PathBase", "/nxhub");
        if (trustHeader)
        {
            web.UseSetting("NxHub:Auth:TrustedProxyUserHeader", UserHeader);
        }

        if (loopback)
        {
            web.ConfigureServices(services => services.AddSingleton<IStartupFilter, LoopbackStartupFilter>());
        }
    });

    private static HttpRequestMessage Request(HttpMethod method, string path, string? origin = null, string? fetchSite = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(UserHeader, "owner");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (fetchSite is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();

    [Fact]
    public async Task Api_and_ui_are_served_under_the_path_base_and_at_the_root()
    {
        await using var server = BehindProxy();
        using var anonymous = server.CreateClient(authenticated: false);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/nxhub/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/health")).StatusCode);
        var page = await anonymous.GetAsync("/nxhub/");
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Proxy_authenticated_user_can_read_and_make_same_origin_changes()
    {
        await using var server = BehindProxy();
        using var client = server.CreateClient(authenticated: false);

        var list = await client.SendAsync(Request(HttpMethod.Get, "/nxhub/api/switches"));
        var add = await client.SendAsync(Request(HttpMethod.Post, "/nxhub/api/switches", origin: "http://localhost", fetchSite: "same-origin", body: new { name = "Proxy", host = "192.0.2.9", enabled = false }));

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example", null)]
    [InlineData(null, "cross-site")]
    [InlineData("http://localhost", "same-site")]
    public async Task Cross_site_changes_are_blocked_for_proxy_sessions(string? origin, string? fetchSite)
    {
        await using var server = BehindProxy();
        using var client = server.CreateClient(authenticated: false);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/nxhub/api/switches", origin, fetchSite, new { name = "X", host = "192.0.2.9", enabled = false }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("cross_site_request", await CodeAsync(response));
    }

    [Fact]
    public async Task Bearer_token_still_works_cross_origin()
    {
        await using var server = BehindProxy();

        var request = new HttpRequestMessage(HttpMethod.Post, "/nxhub/api/switches") { Content = JsonContent.Create(new { name = "Script", host = "192.0.2.9", enabled = false }) };
        request.Headers.Add("Origin", "https://elsewhere.example");

        Assert.Equal(HttpStatusCode.Created, (await server.Client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Proxy_header_is_ignored_unless_configured_and_from_loopback(bool trustHeader, bool loopback)
    {
        await using var server = BehindProxy(trustHeader, loopback);
        using var client = server.CreateClient(authenticated: false);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/nxhub/api/switches"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
