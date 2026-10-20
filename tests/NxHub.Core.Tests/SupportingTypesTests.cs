using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NxHub.Core.Activity;
using NxHub.Core.Devices;
using NxHub.Core.Diagnostics;
using NxHub.Core.Errors;
using NxHub.Core.Events;
using NxHub.Core.Sessions;
using NxHub.Tests.Shared;

namespace NxHub.Core.Tests;

public class SupportingTypesTests
{
    [Theory]
    [InlineData("OLED", "192.168.1.50", 6000)]
    [InlineData("  Spaced  ", "switch.local", 6000)]
    [InlineData("V6", "fe80::1", 1)]
    public void Valid_device_input_is_normalised(string name, string host, int port)
    {
        var (n, h, p, enabled) = new SwitchDeviceInput(name, host, port, null).Validate();
        Assert.Equal(name.Trim(), n);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
        Assert.True(enabled);
    }

    [Theory]
    [InlineData(null, "192.168.1.50", 6000, "name")]
    [InlineData("ok", "", 6000, "host")]
    [InlineData("ok", "http://192.168.1.50", 6000, "host")]
    [InlineData("ok", "192.168.1.50:6000", 6000, "host")]
    [InlineData("ok", "192.168.1.50", 0, "port")]
    [InlineData("ok", "192.168.1.50", 70000, "port")]
    public void Invalid_device_input_reports_the_field(string? name, string host, int port, string field)
    {
        var error = Assert.Throws<ValidationFailedException>(() => new SwitchDeviceInput(name, host, port, true).Validate());
        Assert.True(error.Errors.ContainsKey(field), $"expected an error for {field}");
    }

    [Fact]
    public void Default_port_is_6000()
    {
        Assert.Equal(6000, new SwitchDeviceInput("a", "10.0.0.2", null, null).Validate().Port);
    }

    [Fact]
    public void Device_ids_are_prefixed_and_unique()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => SwitchDevice.NewId()).ToHashSet();
        Assert.Equal(200, ids.Count);
        Assert.All(ids, id => Assert.Matches("^sw_[a-z2-9]{12}$", id));
    }

    [Fact]
    public void Backoff_doubles_and_caps()
    {
        var options = new SessionOptions { ReconnectJitter = 0, ReconnectInitialDelay = TimeSpan.FromSeconds(1), ReconnectMaxDelay = TimeSpan.FromSeconds(30) };
        Assert.Equal([1, 2, 4, 8, 16, 30, 30], Enumerable.Range(1, 7).Select(a => options.GetBackoffDelay(a).TotalSeconds));
    }

    [Fact]
    public void Backoff_jitter_stays_within_bounds()
    {
        var options = new SessionOptions { ReconnectJitter = 0.2, ReconnectInitialDelay = TimeSpan.FromSeconds(10), ReconnectMaxDelay = TimeSpan.FromSeconds(10) };
        var random = new Random(42);
        for (var i = 0; i < 100; i++)
        {
            var delay = options.GetBackoffDelay(1, random).TotalSeconds;
            Assert.InRange(delay, 8, 12);
        }
    }

    [Fact]
    public void Event_hub_fans_out_and_drops_oldest_for_slow_subscribers()
    {
        var hub = new EventHub();
        using var fast = hub.Subscribe(capacity: 10);
        using var slow = hub.Subscribe(capacity: 2);

        for (var i = 0; i < 5; i++)
        {
            hub.Publish(new NxHubEvent("test", null, DateTimeOffset.UnixEpoch, i));
        }

        var fastItems = Drain(fast);
        var slowItems = Drain(slow);
        Assert.Equal([0, 1, 2, 3, 4], fastItems);
        Assert.Equal([3, 4], slowItems);
    }

    [Fact]
    public void Unsubscribed_readers_stop_receiving()
    {
        var hub = new EventHub();
        var subscription = hub.Subscribe();
        subscription.Dispose();
        hub.Publish(new NxHubEvent("test", null, DateTimeOffset.UnixEpoch, 1));
        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public void Activity_log_is_bounded_and_newest_first()
    {
        var log = new ActivityLog("sw", new EventHub(), new FakeTimeProvider(), capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            log.Add(ActivityLevel.Info, ActivityCategory.Connection, $"m{i}");
        }

        Assert.Equal(["m5", "m4", "m3"], log.GetRecent(10).Select(e => e.Message));
        Assert.Equal(5, log.GetRecent(1)[0].Sequence);
    }

    [Fact]
    public async Task Manager_adds_updates_and_removes_switches()
    {
        var repository = new InMemoryRepository();
        var fake = new FakeSwitch();
        var hub = new EventHub();
        var options = new SessionOptions { ReconnectJitter = 0 };
        var diagnostics = new DiagnosticsRunner(new SystemNetworkProbe(), fake, options, hub, TimeProvider.System, NullLogger<DiagnosticsRunner>.Instance);
        await using var manager = new SwitchManager(repository, fake, options, hub, diagnostics, NullLoggerFactory.Instance, TimeProvider.System);
        await manager.StartAsync(CancellationToken.None);

        var session = await manager.AddAsync(new SwitchDeviceInput("OLED", "192.0.2.5", null, true), CancellationToken.None);
        await Eventually.TrueAsync(() => session.State == ConnectionState.Connected, "new switch should connect");
        Assert.Single(await repository.ListAsync(CancellationToken.None));

        await manager.UpdateAsync(session.SwitchId, new SwitchDeviceInput("OLED renamed", "192.0.2.5", 6000, true), CancellationToken.None);
        Assert.Equal("OLED renamed", (await repository.GetAsync(session.SwitchId, CancellationToken.None))?.Name);

        await manager.DeleteAsync(session.SwitchId, CancellationToken.None);
        Assert.Empty(await repository.ListAsync(CancellationToken.None));
        Assert.Equal(0, fake.OpenConnections);
        var missing = Assert.Throws<NxHubException>(() => manager.GetSession(session.SwitchId));
        Assert.Equal("switch_not_found", missing.Code);
    }

    private static List<int> Drain(EventSubscription subscription)
    {
        var items = new List<int>();
        while (subscription.Reader.TryRead(out var e))
        {
            items.Add((int)e.Data!);
        }

        return items;
    }

    private sealed class InMemoryRepository : ISwitchDeviceRepository
    {
        private readonly Dictionary<string, SwitchDevice> _devices = [];

        public Task<IReadOnlyList<SwitchDevice>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SwitchDevice>>([.. _devices.Values]);

        public Task<SwitchDevice?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(_devices.GetValueOrDefault(id));

        public Task AddAsync(SwitchDevice device, CancellationToken cancellationToken)
        {
            _devices.Add(device.Id, device);
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

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_devices.Remove(id));
    }
}
