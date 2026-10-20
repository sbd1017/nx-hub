using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NxHub.Simulator;
using NxHub.Tests.Shared;

namespace NxHub.Server.Tests;

public class MacroApiTests
{
    private static readonly object TwoPresses = new
    {
        name = "Two presses",
        steps = new object[]
        {
            new { type = "press", button = "a", durationMs = 40 },
            new { type = "wait", durationMs = 50 },
            new { type = "press", button = "dpadRight", durationMs = 40 },
        },
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Macro_crud_and_persistence()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "nxhub-tests", Guid.NewGuid().ToString("N"));
        string id;
        await using (var server = new ServerFixture(dataDirectory))
        {
            var created = await server.Client.PostAsJsonAsync("/api/macros", TwoPresses);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var body = await Json(created);
            id = body.GetProperty("id").GetString()!;
            Assert.Equal(3, body.GetProperty("steps").GetArrayLength());
            Assert.False(body.GetProperty("steps")[1].TryGetProperty("button", out _), "unused fields are omitted");

            var updated = await server.Client.PutAsJsonAsync($"/api/macros/{id}", new { name = "Renamed", steps = new object[] { new { type = "wait", durationMs = 10 } }, repeat = 3 });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        }

        await using var restarted = new ServerFixture(dataDirectory);
        var macro = await restarted.Client.GetFromJsonAsync<JsonElement>($"/api/macros/{id}");
        Assert.Equal("Renamed", macro.GetProperty("name").GetString());
        Assert.Equal(3, macro.GetProperty("repeat").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await restarted.Client.DeleteAsync($"/api/macros/{id}")).StatusCode);
        var gone = await restarted.Client.GetAsync($"/api/macros/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("macro_not_found", (await Json(gone)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_macro_returns_field_errors()
    {
        await using var server = new ServerFixture();

        var response = await server.Client.PostAsJsonAsync("/api/macros", new { name = "Bad", steps = new object[] { new { type = "press" } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await Json(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("steps[0].button", out _));
    }

    [Fact]
    public async Task Saved_macro_runs_on_the_switch()
    {
        await using var server = new ServerFixture();
        var switchId = await server.AddSimulatorAsync();
        var macroId = (await Json(await server.Client.PostAsJsonAsync("/api/macros", TwoPresses))).GetProperty("id").GetString();

        var started = await server.Client.PostAsJsonAsync($"/api/switches/{switchId}/macro/run", new { macroId });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        await Eventually.TrueAsync(
            async () => (await server.Client.GetFromJsonAsync<JsonElement>($"/api/switches/{switchId}/macro")).GetProperty("run").GetProperty("status").GetString() == "completed",
            "macro should complete");
        await Eventually.TrueAsync(() => server.Simulator.ReceivedCommands.Contains("release DRIGHT"), "simulator should see both presses");
        var received = server.Simulator.ReceivedCommands.ToList();
        Assert.True(received.IndexOf("press A") < received.IndexOf("press DRIGHT"));
    }

    [Fact]
    public async Task Inline_macro_can_be_stopped_and_second_run_conflicts()
    {
        await using var server = new ServerFixture();
        var switchId = await server.AddSimulatorAsync();
        var looping = new { macro = new { name = "Loop", steps = new object[] { new { type = "press", button = "b", durationMs = 30 }, new { type = "wait", durationMs = 50 } }, repeat = 0 } };

        Assert.Equal(HttpStatusCode.Accepted, (await server.Client.PostAsJsonAsync($"/api/switches/{switchId}/macro/run", looping)).StatusCode);
        var second = await server.Client.PostAsJsonAsync($"/api/switches/{switchId}/macro/run", looping);
        var stopped = await Json(await server.Client.PostAsync($"/api/switches/{switchId}/macro/stop", null));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("macro_running", (await Json(second)).GetProperty("code").GetString());
        Assert.Equal("stopped", stopped.GetProperty("run").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Screen_endpoint_returns_a_jpeg()
    {
        await using var server = new ServerFixture();
        var switchId = await server.AddSimulatorAsync();

        var response = await server.Client.GetAsync($"/api/switches/{switchId}/screen?maxAgeMs=0");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([0xFF, 0xD8, 0xFF], bytes[..3]);
        Assert.Equal("1", response.Headers.GetValues("X-Frame-Sequence").Single());
    }

    [Fact]
    public async Task Screen_unavailable_is_a_clear_422()
    {
        await using var server = new ServerFixture(simulatorOptions: new SimulatorOptions { ScreenshotsWork = false });
        var switchId = await server.AddSimulatorAsync();

        var response = await server.Client.GetAsync($"/api/switches/{switchId}/screen");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("screenshot_unavailable", (await Json(response)).GetProperty("code").GetString());
    }
}
