using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using NxHub.Simulator;
using NxHub.Tests.Shared;

namespace NxHub.Server.Tests;

public class ApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await Json(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Health_and_info_are_public()
    {
        await using var server = new ServerFixture();
        using var anonymous = server.CreateClient(authenticated: false);

        var health = await anonymous.GetFromJsonAsync<JsonElement>("/api/health");
        var info = await anonymous.GetFromJsonAsync<JsonElement>("/api/info");

        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.True(info.GetProperty("authRequired").GetBoolean());
        Assert.Equal("dark", info.GetProperty("theme").GetString());
    }

    [Fact]
    public async Task Api_requires_a_valid_token()
    {
        await using var server = new ServerFixture();
        using var anonymous = server.CreateClient(authenticated: false);
        using var wrong = server.CreateClient(authenticated: false);
        wrong.DefaultRequestHeaders.Authorization = new("Bearer", "not-the-token-at-all");

        var none = await anonymous.GetAsync("/api/switches");
        var bad = await wrong.GetAsync("/api/switches");

        await AssertProblemAsync(none, HttpStatusCode.Unauthorized, "unauthorized");
        await AssertProblemAsync(bad, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal("Bearer", none.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task Auth_can_be_disabled_explicitly()
    {
        await using var server = new ServerFixture(authDisabled: true);
        using var anonymous = server.CreateClient(authenticated: false);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/switches")).StatusCode);
    }

    [Fact]
    public async Task Switch_crud_round_trip()
    {
        await using var server = new ServerFixture();

        var created = await server.Client.PostAsJsonAsync("/api/switches", new { name = "OLED", host = "192.0.2.44", enabled = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await Json(created);
        var id = body.GetProperty("id").GetString();
        Assert.Equal($"/api/switches/{id}", created.Headers.Location?.ToString());
        Assert.Equal(6000, body.GetProperty("port").GetInt32());
        Assert.Equal("disconnected", body.GetProperty("status").GetProperty("state").GetString());

        var list = await server.Client.GetFromJsonAsync<JsonElement>("/api/switches");
        Assert.Equal(1, list.GetArrayLength());

        var updated = await server.Client.PutAsJsonAsync($"/api/switches/{id}", new { name = "OLED-CFW", host = "192.0.2.45", port = 6001, enabled = false });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedBody = await Json(updated);
        Assert.Equal("OLED-CFW", updatedBody.GetProperty("name").GetString());
        Assert.Equal(6001, updatedBody.GetProperty("port").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await server.Client.DeleteAsync($"/api/switches/{id}")).StatusCode);
        await AssertProblemAsync(await server.Client.GetAsync($"/api/switches/{id}"), HttpStatusCode.NotFound, "switch_not_found");
    }

    [Fact]
    public async Task Invalid_switch_input_returns_field_errors()
    {
        await using var server = new ServerFixture();

        var response = await server.Client.PostAsJsonAsync("/api/switches", new { name = "", host = "http://bad", port = 0 });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var errors = (await Json(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("host", out _));
        Assert.True(errors.TryGetProperty("port", out _));
    }

    [Fact]
    public async Task Configuration_persists_across_restarts()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "nxhub-tests", Guid.NewGuid().ToString("N"));
        await using (var first = new ServerFixture(dataDirectory))
        {
            await first.Client.PostAsJsonAsync("/api/switches", new { name = "Persistent", host = "192.0.2.7", enabled = false });
        }

        await using var second = new ServerFixture(dataDirectory);
        var list = await second.Client.GetFromJsonAsync<JsonElement>("/api/switches");
        Assert.Equal("Persistent", list[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Controller_commands_reach_the_switch()
    {
        await using var server = new ServerFixture();
        var id = await server.AddSimulatorAsync();
        var sim = server.Simulator;

        var press = await server.Client.PostAsJsonAsync($"/api/switches/{id}/controller/press", new { button = "a", durationMs = 50 });
        Assert.Equal(HttpStatusCode.OK, press.StatusCode);
        // The HTTP call returns once bytes are written; the simulator reads them asynchronously.
        await Eventually.TrueAsync(() => sim.ReceivedCommands.Contains("release A"), "simulator should receive the release");
        var received = sim.ReceivedCommands.ToList();
        Assert.True(received.IndexOf("press A") < received.IndexOf("release A"), "press must precede release");

        await server.Client.PostAsJsonAsync($"/api/switches/{id}/controller/hold", new { button = "dpadUp" });
        await Eventually.TrueAsync(() => sim.HeldButtons.Contains("DUP"), "DUP should be held on the simulator");
        var snapshot = await server.Client.GetFromJsonAsync<JsonElement>($"/api/switches/{id}/controller");
        Assert.Equal("dpadUp", snapshot.GetProperty("heldButtons")[0].GetString());

        await server.Client.PostAsJsonAsync($"/api/switches/{id}/controller/stick", new { stick = "left", x = 1.0, y = 0.0 });
        await Eventually.TrueAsync(() => sim.GetStick("LEFT") == (32767, 0), "left stick should move");

        await server.Client.PostAsJsonAsync($"/api/switches/{id}/controller/release-all", new { });
        await Eventually.TrueAsync(() => sim.HeldButtons.Count == 0 && sim.GetStick("LEFT") == (0, 0), "everything released");

        await server.Client.PostAsync($"/api/switches/{id}/controller/reset", null);
        await Eventually.TrueAsync(() => !sim.ControllerAttached, "controller detached");
    }

    [Fact]
    public async Task Configured_links_are_listed_and_unsafe_ones_dropped()
    {
        await using var server = new ServerFixture(configure: web =>
        {
            web.UseSetting("NxHub:Ui:Links:0:Name", "Trade bot");
            web.UseSetting("NxHub:Ui:Links:0:Url", "/nxhub/trade/");
            web.UseSetting("NxHub:Ui:Links:1:Name", "Bad");
            web.UseSetting("NxHub:Ui:Links:1:Url", "javascript:alert(1)");
            web.UseSetting("NxHub:Ui:Links:2:Name", "Elsewhere");
            web.UseSetting("NxHub:Ui:Links:2:Url", "//example.com/");
        });

        var info = await server.Client.GetFromJsonAsync<JsonElement>("/api/info");

        var link = Assert.Single(info.GetProperty("links").EnumerateArray());
        Assert.Equal("Trade bot", link.GetProperty("name").GetString());
        Assert.Equal("/nxhub/trade/", link.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_hand_over_frees_the_switch_and_refuses_input_until_released()
    {
        await using var server = new ServerFixture();
        var id = await server.AddSimulatorAsync();

        await AssertProblemAsync(
            await server.Client.PostAsJsonAsync($"/api/switches/{id}/lease", new { holder = "", ttlSeconds = 60 }),
            HttpStatusCode.BadRequest,
            "validation_failed");

        var lease = await server.Client.PostAsJsonAsync($"/api/switches/{id}/lease", new { holder = "Trade bot", ttlSeconds = 60 });
        Assert.Equal(HttpStatusCode.OK, lease.StatusCode);
        Assert.Equal("Trade bot", (await Json(lease)).GetProperty("holder").GetString());
        await server.WaitForStateAsync(id, "disconnected");
        await Eventually.TrueAsync(() => server.Simulator.ConnectedClients == 0, "NX Hub should close its connection to sys-botbase");

        var status = await server.Client.GetFromJsonAsync<JsonElement>($"/api/switches/{id}/status");
        Assert.Equal("Trade bot", status.GetProperty("lease").GetProperty("holder").GetString());
        await AssertProblemAsync(
            await server.Client.PostAsJsonAsync($"/api/switches/{id}/controller/press", new { button = "a" }),
            HttpStatusCode.Conflict,
            "switch_leased");
        await AssertProblemAsync(
            await server.Client.PostAsJsonAsync($"/api/switches/{id}/lease", new { holder = "Another bot" }),
            HttpStatusCode.Conflict,
            "switch_leased");

        Assert.Equal(HttpStatusCode.NoContent, (await server.Client.DeleteAsync($"/api/switches/{id}/lease")).StatusCode);
        await server.WaitForStateAsync(id, "connected");
    }

    [Theory]
    [InlineData("""{"button":"q"}""", "invalid_request")]
    [InlineData("""{}""", "validation_failed")]
    [InlineData("""{"button":"a","durationMs":1}""", "validation_failed")]
    [InlineData("""not json""", "invalid_request")]
    public async Task Bad_controller_input_is_rejected_without_touching_the_switch(string json, string code)
    {
        await using var server = new ServerFixture();
        var id = await server.AddSimulatorAsync();
        server.Simulator.ClearReceived();

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await server.Client.PostAsync($"/api/switches/{id}/controller/press", content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, code);
        Assert.DoesNotContain(server.Simulator.ReceivedCommands, c => c.StartsWith("press", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_button_error_lists_valid_buttons()
    {
        await using var server = new ServerFixture();
        var id = await server.AddSimulatorAsync();

        var response = await server.Client.PostAsJsonAsync($"/api/switches/{id}/controller/press", new { button = "start" });

        var detail = (await Json(response)).GetProperty("detail").GetString();
        Assert.Contains("dpadUp", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Controller_on_a_disconnected_switch_is_a_conflict()
    {
        await using var server = new ServerFixture();
        var id = await server.AddSimulatorAsync(enabled: false);

        var response = await server.Client.PostAsJsonAsync($"/api/switches/{id}/controller/press", new { button = "a" });

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "switch_not_connected");
    }

    [Fact]
    public async Task Connect_and_disconnect()
    {
        await using var server = new ServerFixture();
        var id = await server.AddSimulatorAsync(enabled: false);

        var connected = await Json(await server.Client.PostAsync($"/api/switches/{id}/connect", null));
        Assert.Equal("connected", connected.GetProperty("state").GetString());
        Assert.Equal("2.5", connected.GetProperty("capabilities").GetProperty("rawVersion").GetString());

        var disconnected = await Json(await server.Client.PostAsync($"/api/switches/{id}/disconnect", null));
        Assert.Equal("disconnected", disconnected.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Connect_to_nothing_reports_the_failure()
    {
        await using var server = new ServerFixture();
        var created = await Json(await server.Client.PostAsJsonAsync("/api/switches", new { name = "Ghost", host = "127.0.0.1", port = 1, enabled = false }));
        var id = created.GetProperty("id").GetString();

        var status = await Json(await server.Client.PostAsync($"/api/switches/{id}/connect", null));

        Assert.Equal("reconnecting", status.GetProperty("state").GetString());
        Assert.Equal("switch_connection_failed", status.GetProperty("lastError").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Session_recovers_when_the_switch_drops_the_connection()
    {
        await using var server = new ServerFixture();
        var id = await server.AddSimulatorAsync();

        server.Simulator.DisconnectAllClients();

        await Eventually.TrueAsync(
            async () =>
            {
                var status = await server.Client.GetFromJsonAsync<JsonElement>($"/api/switches/{id}/status", ServerFixture.Json);
                return status.GetProperty("state").GetString() == "connected" && status.GetProperty("connectionCount").GetInt32() == 2;
            },
            "session should reconnect");
    }

    [Fact]
    public async Task Diagnostics_run_and_are_retrievable()
    {
        await using var server = new ServerFixture(flavour: SimulatorFlavour.SysBotBaseCpp);
        var id = await server.AddSimulatorAsync();

        await AssertProblemAsync(await server.Client.GetAsync($"/api/switches/{id}/diagnostics/latest"), HttpStatusCode.NotFound, "no_diagnostics");
        var report = await Json(await server.Client.PostAsJsonAsync($"/api/switches/{id}/diagnostics", new { includeControllerTest = true }));
        var latest = await server.Client.GetFromJsonAsync<JsonElement>($"/api/switches/{id}/diagnostics/latest", ServerFixture.Json);

        var checks = report.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("pass", checks["sysbotbase"]);
        Assert.Equal("pass", checks["controller"]);
        Assert.Contains("SysBotBaseCpp", report.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "capabilities").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(report.GetProperty("completedAt").GetString(), latest.GetProperty("completedAt").GetString());
        Assert.NotEqual("notReady", report.GetProperty("overall").GetString());
    }

    [Fact]
    public async Task Activity_is_recorded()
    {
        await using var server = new ServerFixture();
        var id = await server.AddSimulatorAsync();
        await server.Client.PostAsJsonAsync($"/api/switches/{id}/controller/press", new { button = "home" });

        var activity = await server.Client.GetFromJsonAsync<JsonElement>($"/api/switches/{id}/activity?limit=5", ServerFixture.Json);

        Assert.Contains(activity.EnumerateArray(), e => e.GetProperty("message").GetString()!.StartsWith("Pressed Home", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenApi_document_describes_the_api()
    {
        await using var server = new ServerFixture();
        using var anonymous = server.CreateClient(authenticated: false);

        var doc = await anonymous.GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/switches/{id}/controller/press", out _));
        Assert.True(paths.TryGetProperty("/api/switches/{id}/diagnostics", out _));
        Assert.Equal("NX Hub API", doc.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Unknown_api_routes_are_problem_404s_and_ui_routes_fall_back()
    {
        await using var server = new ServerFixture();

        await AssertProblemAsync(await server.Client.GetAsync("/api/nope"), HttpStatusCode.NotFound, "not_found");
        var ui = await server.Client.GetAsync("/some/client/route");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal("text/html", ui.Content.Headers.ContentType?.MediaType);
        Assert.Contains("frame-ancestors 'none'", ui.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_token_file_is_generated_with_owner_only_permissions()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "nxhub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        await using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("NxHub:DataDirectory", dataDirectory);
            web.UseSetting("Logging:LogLevel:Default", "Warning");
        });
        using var client = factory.CreateClient();
        await client.GetAsync("/api/health");

        var tokenFile = Path.Combine(dataDirectory, "admin-token");
        var token = (await File.ReadAllTextAsync(tokenFile)).Trim();
        Assert.True(token.Length >= 40);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenFile));
        }

        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/switches")).StatusCode);
    }
}
