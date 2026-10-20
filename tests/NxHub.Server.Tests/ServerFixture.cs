using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NxHub.Simulator;

namespace NxHub.Server.Tests;

/// <summary>Hosts NX Hub in-process (real HTTP pipeline) against a TCP sys-botbase simulator.</summary>
public sealed class ServerFixture : IAsyncDisposable
{
    public const string Token = "test-token-0123456789abcdef";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly WebApplicationFactory<Program> _factory;

    public ServerFixture(
        string? dataDirectory = null,
        SimulatorFlavour flavour = SimulatorFlavour.SysBotBase,
        bool authDisabled = false,
        Action<IWebHostBuilder>? configure = null,
        SimulatorOptions? simulatorOptions = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "nxhub-tests", Guid.NewGuid().ToString("N"));
        Simulator = new SysBotSimulator(simulatorOptions ?? new SimulatorOptions { Flavour = flavour });
        Simulator.Start();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing");
            web.UseSetting("NxHub:DataDirectory", DataDirectory);
            web.UseSetting("NxHub:Auth:Token", Token);
            web.UseSetting("NxHub:Auth:Mode", authDisabled ? "Disabled" : "Token");
            web.UseSetting("NxHub:Sessions:HeartbeatSeconds", "0.3");
            web.UseSetting("NxHub:Sessions:ReconnectInitialSeconds", "0.1");
            web.UseSetting("NxHub:Sessions:ReconnectMaxSeconds", "0.5");
            web.UseSetting("NxHub:Sessions:ResponseTimeoutSeconds", "1");
            web.UseSetting("Logging:LogLevel:Default", "Warning");
            configure?.Invoke(web);
        });
        Client = CreateClient(authenticated: true);
    }

    public string DataDirectory { get; }

    public SysBotSimulator Simulator { get; }

    public HttpClient Client { get; }

    public WebApplicationFactory<Program> Factory => _factory;

    public HttpClient CreateClient(bool authenticated)
    {
        var client = _factory.CreateClient();
        if (authenticated)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }

        return client;
    }

    /// <summary>Adds the simulator as a Switch and waits until it is connected.</summary>
    public async Task<string> AddSimulatorAsync(bool enabled = true)
    {
        var response = await Client.PostAsJsonAsync("/api/switches", new { name = "Simulator", host = "127.0.0.1", port = Simulator.Port, enabled });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = created.GetProperty("id").GetString()!;
        if (enabled)
        {
            await WaitForStateAsync(id, "connected");
        }

        return id;
    }

    public async Task WaitForStateAsync(string id, string state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var status = await Client.GetFromJsonAsync<JsonElement>($"/api/switches/{id}/status", Json);
            last = status.GetProperty("state").GetString();
            if (last == state)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Switch {id} did not reach '{state}' (last '{last}').");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        await Simulator.DisposeAsync();
    }
}
