using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using NxHub.Core.Devices;
using NxHub.Core.Macros;
using NxHub.Protocol;
using NxHub.Simulator;

namespace NxHub.Server.Demo;

/// <summary>
/// Everything that makes a public demo instance: an in-process simulated Switch with an
/// interactive pretend screen, in-memory storage seeded with one Switch and example macros, and a
/// guard that refuses configuration changes and saving. State resets whenever the process restarts.
/// </summary>
public static class DemoMode
{
    public const string SwitchId = "sw_demo";

    public static void AddDemoMode(this IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var simulator = new SysBotSimulator(new SimulatorOptions { InteractiveScreen = true });
            simulator.Start();
            return simulator;
        });
        services.AddSingleton<ISwitchDeviceRepository>(sp => new InMemorySwitchDeviceRepository(
        [
            new SwitchDevice
            {
                Id = SwitchId,
                Name = "Demo Switch",
                Host = "127.0.0.1",
                Port = sp.GetRequiredService<SysBotSimulator>().Port,
                Enabled = true,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            },
        ]));
        services.AddSingleton<IMacroRepository>(_ => new InMemoryMacroRepository(ExampleMacros()));
    }

    private static IEnumerable<Macro> ExampleMacros()
    {
        static MacroStep Press(SwitchButton button, int ms = 80) => new() { Type = MacroStepType.Press, Button = button, DurationMs = ms };
        static MacroStep Wait(int ms) => new() { Type = MacroStepType.Wait, DurationMs = ms };
        static Macro Make(string id, string name, string description, int repeat, params MacroStep[] steps) => new()
        {
            Id = id,
            Name = name,
            Description = description,
            Steps = steps,
            Repeat = repeat,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        yield return Make(
            "mc_demo_scroll",
            "Scroll to the end",
            "Moves right across the whole menu.",
            1,
            new MacroStep { Type = MacroStepType.Repeat, Count = 5, Steps = [Press(SwitchButton.DpadRight), Wait(400)] });
        yield return Make(
            "mc_demo_open",
            "Open and close",
            "Opens the selected tile, waits, then goes back.",
            1,
            Press(SwitchButton.A),
            Wait(1500),
            Press(SwitchButton.B));
        yield return Make(
            "mc_demo_tour",
            "Tour every tile",
            "Back to the start, then opens and closes each tile in turn.",
            1,
            new MacroStep { Type = MacroStepType.Repeat, Count = 5, Steps = [Press(SwitchButton.DpadLeft), Wait(150)] },
            new MacroStep
            {
                Type = MacroStepType.Repeat,
                Count = 6,
                Steps = [Press(SwitchButton.A), Wait(1200), Press(SwitchButton.B), Wait(500), Press(SwitchButton.DpadRight), Wait(500)],
            });
    }
}

/// <summary>
/// Refuses anything that would change shared configuration on the public demo, so one visitor
/// cannot break it (or write text) for everyone else.
/// </summary>
public sealed class DemoGuardMiddleware
{
    private readonly RequestDelegate _next;

    public DemoGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problems)
    {
        if (IsBlocked(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Not available in the demo",
                Detail = "This public demo controls a simulated Switch shared with other visitors, so settings, saving and disconnecting are switched off. Install NX Hub to use them.",
            };
            problem.Extensions["code"] = "demo_read_only";
            await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
            return;
        }

        await _next(context);
    }

    private static bool IsBlocked(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api/macros", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!path.StartsWith("/api/switches", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Allowed: controller input, running macros, diagnostics, connect (harmless if already connected).
        return !(path.Contains("/controller/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/macro/run", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/macro/stop", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/macro/pause", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/macro/resume", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/diagnostics", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/connect", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Demo-mode storage: lives only as long as the process.</summary>
public sealed class InMemorySwitchDeviceRepository : ISwitchDeviceRepository
{
    private readonly ConcurrentDictionary<string, SwitchDevice> _devices;

    public InMemorySwitchDeviceRepository(IEnumerable<SwitchDevice> seed) =>
        _devices = new ConcurrentDictionary<string, SwitchDevice>(seed.ToDictionary(d => d.Id));

    public Task<IReadOnlyList<SwitchDevice>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SwitchDevice>>([.. _devices.Values.OrderBy(d => d.CreatedAt)]);

    public Task<SwitchDevice?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_devices.GetValueOrDefault(id));

    public Task AddAsync(SwitchDevice device, CancellationToken cancellationToken)
    {
        _devices[device.Id] = device;
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(SwitchDevice device, CancellationToken cancellationToken)
    {
        if (!_devices.ContainsKey(device.Id))
        {
            return Task.FromResult(false);
        }

        _devices[device.Id] = device;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_devices.TryRemove(id, out _));
}

public sealed class InMemoryMacroRepository : IMacroRepository
{
    private readonly ConcurrentDictionary<string, Macro> _macros;

    public InMemoryMacroRepository(IEnumerable<Macro> seed) =>
        _macros = new ConcurrentDictionary<string, Macro>(seed.ToDictionary(m => m.Id));

    public Task<IReadOnlyList<Macro>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Macro>>([.. _macros.Values.OrderBy(m => m.Id, StringComparer.Ordinal)]);

    public Task<Macro?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_macros.GetValueOrDefault(id));

    public Task AddAsync(Macro macro, CancellationToken cancellationToken)
    {
        _macros[macro.Id] = macro;
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(Macro macro, CancellationToken cancellationToken)
    {
        if (!_macros.ContainsKey(macro.Id))
        {
            return Task.FromResult(false);
        }

        _macros[macro.Id] = macro;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_macros.TryRemove(id, out _));
}
