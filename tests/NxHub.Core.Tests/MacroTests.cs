using NxHub.Core.Errors;
using NxHub.Core.Macros;
using NxHub.Core.Sessions;
using NxHub.Protocol;
using NxHub.Tests.Shared;

namespace NxHub.Core.Tests;

public class MacroTests
{
    private static Macro MacroOf(int repeat, params MacroStep[] steps) => new()
    {
        Id = "mc_test",
        Name = "Test",
        Steps = steps,
        Repeat = repeat,
        CreatedAt = SessionHarness.Start,
        UpdatedAt = SessionHarness.Start,
    };

    private static MacroStep Press(SwitchButton b, int ms = 50) => new() { Type = MacroStepType.Press, Button = b, DurationMs = ms };

    private static MacroStep Wait(int ms) => new() { Type = MacroStepType.Wait, DurationMs = ms };

    private static async Task<SessionHarness> ConnectedAsync()
    {
        var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);
        h.Switch.ClearSent();
        return h;
    }

    [Fact]
    public void Validation_reports_each_bad_field()
    {
        var input = new MacroInput(
            "",
            null,
            [
                new MacroStep { Type = MacroStepType.Press },
                new MacroStep { Type = MacroStepType.Wait, DurationMs = 0 },
                new MacroStep { Type = MacroStepType.Stick, Stick = SwitchStick.Left, X = 2, Y = 0 },
                new MacroStep { Type = MacroStepType.Repeat, Count = 2, Steps = [] },
            ],
            -1);

        var error = Assert.Throws<ValidationFailedException>(() => input.Validate());

        Assert.Equal(
            ["name", "repeat", "steps[0].button", "steps[1].durationMs", "steps[2].x", "steps[3].steps"],
            error.Errors.Keys.Order());
    }

    [Fact]
    public void Looping_forever_needs_a_step_that_takes_time()
    {
        var input = new MacroInput("Loop", null, [new MacroStep { Type = MacroStepType.Release, Button = SwitchButton.A }], 0);
        var error = Assert.Throws<ValidationFailedException>(() => input.Validate());
        Assert.True(error.Errors.ContainsKey("repeat"));
    }

    [Fact]
    public void Repeats_cannot_nest_too_deep()
    {
        MacroStep nest(int depth) => depth == 0
            ? Press(SwitchButton.A)
            : new MacroStep { Type = MacroStepType.Repeat, Count = 2, Steps = [nest(depth - 1)] };

        Assert.Throws<ValidationFailedException>(() => new MacroInput("Deep", null, [nest(MacroRules.MaxDepth)], 1).Validate());
        new MacroInput("Ok", null, [nest(MacroRules.MaxDepth - 1)], 1).Validate();
    }

    [Fact]
    public async Task Runs_steps_in_order_including_nested_repeats()
    {
        await using var h = await ConnectedAsync();
        var macro = MacroOf(
            2,
            Press(SwitchButton.A),
            new MacroStep { Type = MacroStepType.Repeat, Count = 2, Steps = [Press(SwitchButton.B)] },
            new MacroStep { Type = MacroStepType.Stick, Stick = SwitchStick.Left, X = 0, Y = 1 });

        h.Session.Macros.Start(macro);
        await h.AdvanceUntilAsync(() => h.Session.Macros.State?.Status == MacroRunStatus.Completed, "macro should finish", TimeSpan.FromMilliseconds(50));

        var presses = h.Switch.Sent.Where(s => s.StartsWith("press", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["press A", "press B", "press B", "press A", "press B", "press B"], presses);
        Assert.Equal(8, h.Session.Macros.State!.StepsExecuted);
        // The stick was left pushed by the macro; the end of the run recentres it.
        Assert.Equal("setStick LEFT 0 0", h.Switch.Sent[^1]);
        Assert.Empty(h.Session.Controller.GetSnapshot().Sticks);
    }

    [Fact]
    public async Task Holds_left_by_a_macro_are_released_when_it_ends()
    {
        await using var h = await ConnectedAsync();

        h.Session.Macros.Start(MacroOf(1, new MacroStep { Type = MacroStepType.Hold, Button = SwitchButton.ZR }, Wait(100)));
        await h.AdvanceUntilAsync(() => h.Session.Macros.State?.IsActive == false, "macro should end", TimeSpan.FromMilliseconds(50));

        Assert.Equal(["press ZR", "release ZR"], h.Switch.Sent);
        Assert.Empty(h.Session.Controller.GetSnapshot().HeldButtons);
    }

    [Fact]
    public async Task Stop_ends_a_looping_macro_and_releases_inputs()
    {
        await using var h = await ConnectedAsync();
        h.Session.Macros.Start(MacroOf(0, new MacroStep { Type = MacroStepType.Hold, Button = SwitchButton.L }, Wait(1000)));
        await Eventually.TrueAsync(() => h.Switch.Sent.Contains("press L"), "hold should be sent");

        var state = await h.Session.Macros.StopAsync();

        Assert.Equal(MacroRunStatus.Stopped, state!.Status);
        Assert.Null(state.Iterations);
        Assert.Contains("release L", h.Switch.Sent);
        Assert.Empty(h.Session.Controller.GetSnapshot().HeldButtons);
    }

    [Fact]
    public async Task Only_one_macro_runs_at_a_time()
    {
        await using var h = await ConnectedAsync();
        h.Session.Macros.Start(MacroOf(0, Wait(1000)));

        var error = Assert.Throws<NxHubException>(() => h.Session.Macros.Start(MacroOf(1, Wait(10))));

        Assert.Equal("macro_running", error.Code);
        await h.Session.Macros.StopAsync();
    }

    [Fact]
    public async Task Pause_holds_before_the_next_step_and_resume_continues()
    {
        await using var h = await ConnectedAsync();
        h.Session.Macros.Start(MacroOf(1, Wait(100), Press(SwitchButton.X)));
        await Eventually.TrueAsync(() => h.Session.Macros.State?.CurrentStep?.StartsWith("Wait", StringComparison.Ordinal) == true, "wait step should start");
        h.Session.Macros.Pause();

        await h.AdvanceUntilAsync(() => h.Session.Macros.State?.StepsExecuted == 1, "wait step should finish", TimeSpan.FromMilliseconds(50));
        await Task.Delay(100);
        Assert.DoesNotContain("press X", h.Switch.Sent);
        Assert.Equal(MacroRunStatus.Paused, h.Session.Macros.State!.Status);

        h.Session.Macros.Resume();
        await h.AdvanceUntilAsync(() => h.Session.Macros.State?.Status == MacroRunStatus.Completed, "should complete after resume", TimeSpan.FromMilliseconds(50));
        Assert.Contains("press X", h.Switch.Sent);
    }

    [Fact]
    public async Task Losing_the_connection_fails_the_run()
    {
        await using var h = await ConnectedAsync();
        h.Session.Macros.Start(MacroOf(0, Press(SwitchButton.A), Wait(50)));
        await Eventually.TrueAsync(() => h.Switch.Sent.Contains("press A"), "macro should start pressing");

        h.Switch.Responsive = false;
        h.Switch.DropConnections();
        await h.AdvanceUntilAsync(() => h.Session.Macros.State?.Status == MacroRunStatus.Failed, "macro should fail", TimeSpan.FromMilliseconds(100));

        Assert.NotNull(h.Session.Macros.State!.Error);
    }

    [Fact]
    public async Task Cannot_start_when_not_connected()
    {
        await using var h = new SessionHarness(enabled: false);
        var error = Assert.Throws<NxHubException>(() => h.Session.Macros.Start(MacroOf(1, Wait(10))));
        Assert.Equal("switch_not_connected", error.Code);
    }

    [Fact]
    public async Task Disconnect_stops_a_running_macro()
    {
        await using var h = await ConnectedAsync();
        h.Session.Macros.Start(MacroOf(0, Wait(1000)));

        await h.Session.DisconnectAsync(CancellationToken.None);

        Assert.Equal(MacroRunStatus.Stopped, h.Session.Macros.State!.Status);
    }

    [Fact]
    public async Task Library_validates_and_stores()
    {
        var repository = new InMemoryMacros();
        var library = new MacroLibrary(repository, new Events.EventHub(), TimeProvider.System);

        var saved = await library.AddAsync(new MacroInput(" Skip intro ", null, [Press(SwitchButton.A), Wait(500)], null), CancellationToken.None);
        await Assert.ThrowsAsync<ValidationFailedException>(() => library.AddAsync(new MacroInput("x", null, [], 1), CancellationToken.None));
        var missing = await Assert.ThrowsAsync<NxHubException>(() => library.GetAsync("mc_nope", CancellationToken.None));

        Assert.Equal("Skip intro", saved.Name);
        Assert.Equal(1, saved.Repeat);
        Assert.StartsWith("mc_", saved.Id, StringComparison.Ordinal);
        Assert.Equal("macro_not_found", missing.Code);
    }

    private sealed class InMemoryMacros : IMacroRepository
    {
        private readonly Dictionary<string, Macro> _items = [];

        public Task<IReadOnlyList<Macro>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Macro>>([.. _items.Values]);

        public Task<Macro?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_items.GetValueOrDefault(id));

        public Task AddAsync(Macro macro, CancellationToken cancellationToken)
        {
            _items.Add(macro.Id, macro);
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(Macro macro, CancellationToken cancellationToken)
        {
            var found = _items.ContainsKey(macro.Id);
            if (found)
            {
                _items[macro.Id] = macro;
            }

            return Task.FromResult(found);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_items.Remove(id));
    }
}
