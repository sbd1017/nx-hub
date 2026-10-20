using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NxHub.Tests.Shared;

namespace NxHub.Server.Tests;

/// <summary>Public demo mode: simulated Switch, no login, nothing visitors can change for others.</summary>
public sealed class DemoTests : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public DemoTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("NxHub:Demo:Enabled", "true");
            web.UseSetting("NxHub:DataDirectory", Path.Combine(Path.GetTempPath(), "nxhub-tests", Guid.NewGuid().ToString("N")));
            web.UseSetting("Logging:LogLevel:Default", "Warning");
        });
        _client = _factory.CreateClient();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task WaitConnectedAsync() =>
        await Eventually.TrueAsync(
            async () => (await _client.GetFromJsonAsync<JsonElement>("/api/switches/sw_demo/status")).GetProperty("state").GetString() == "connected",
            "demo Switch should connect");

    [Fact]
    public async Task Demo_is_open_and_seeded()
    {
        var info = await _client.GetFromJsonAsync<JsonElement>("/api/info");
        var macros = await _client.GetFromJsonAsync<JsonElement>("/api/macros");

        Assert.True(info.GetProperty("demo").GetBoolean());
        Assert.False(info.GetProperty("authRequired").GetBoolean());
        Assert.Equal(3, macros.GetArrayLength());
        await WaitConnectedAsync();
    }

    [Theory]
    [InlineData("POST", "/api/switches")]
    [InlineData("PUT", "/api/switches/sw_demo")]
    [InlineData("DELETE", "/api/switches/sw_demo")]
    [InlineData("POST", "/api/switches/sw_demo/disconnect")]
    [InlineData("POST", "/api/macros")]
    [InlineData("DELETE", "/api/macros/mc_demo_open")]
    public async Task Changes_that_affect_other_visitors_are_refused(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { name = "x", host = "192.0.2.1", steps = Array.Empty<object>() }) };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("demo_read_only", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Controller_drives_the_pretend_screen()
    {
        await WaitConnectedAsync();
        var before = await (await _client.GetAsync("/api/switches/sw_demo/screen?maxAgeMs=0")).Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/switches/sw_demo/controller/press", new { button = "dpadRight", durationMs = 30 })).StatusCode);
        var after = await (await _client.GetAsync("/api/switches/sw_demo/screen?maxAgeMs=0")).Content.ReadAsByteArrayAsync();

        Assert.Equal([0xFF, 0xD8, 0xFF], after[..3]);
        Assert.False(before.AsSpan().SequenceEqual(after), "the frame should change when the cursor moves");
    }

    [Fact]
    public async Task Visitor_macros_run_under_a_neutral_name()
    {
        await WaitConnectedAsync();

        var response = await _client.PostAsJsonAsync("/api/switches/sw_demo/macro/run", new { macro = new { name = "anything a visitor types", steps = new object[] { new { type = "press", button = "b" } } } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Your recording", (await Json(response)).GetProperty("run").GetProperty("macroName").GetString());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }
}
