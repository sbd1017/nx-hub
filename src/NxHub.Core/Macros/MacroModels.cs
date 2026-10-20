using System.Globalization;
using System.Text.Json.Serialization;
using NxHub.Core.Errors;
using NxHub.Protocol;

namespace NxHub.Core.Macros;

public enum MacroStepType
{
    /// <summary>Press and release a button (<c>button</c>, optional <c>durationMs</c>, default 100).</summary>
    Press,

    /// <summary>Hold a button until a later <c>release</c> step (or the end of the macro).</summary>
    Hold,

    Release,

    /// <summary>Move a stick to <c>x</c>/<c>y</c> (-1..1). With <c>durationMs</c> it recentres afterwards.</summary>
    Stick,

    /// <summary>Pause for <c>durationMs</c>.</summary>
    Wait,

    /// <summary>Run nested <c>steps</c> <c>count</c> times.</summary>
    Repeat,
}

/// <summary>One step of a macro. Which fields apply depends on <see cref="Type"/>.</summary>
public sealed record MacroStep
{
    public required MacroStepType Type { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SwitchButton? Button { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SwitchStick? Stick { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? X { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Y { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MacroStep>? Steps { get; init; }

    public string Describe() => Type switch
    {
        MacroStepType.Press => $"Press {Button} ({DurationMs ?? MacroRules.DefaultPressMs} ms)",
        MacroStepType.Hold => $"Hold {Button}",
        MacroStepType.Release => $"Release {Button}",
        MacroStepType.Stick => string.Create(CultureInfo.InvariantCulture, $"{Stick} stick {X:0.##}, {Y:0.##}{(DurationMs is { } ms ? $" for {ms} ms" : string.Empty)}"),
        MacroStepType.Wait => $"Wait {DurationMs} ms",
        MacroStepType.Repeat => $"Repeat ×{Count}",
        _ => Type.ToString(),
    };
}

/// <summary>A saved, reusable sequence of controller inputs. Macros are not tied to one Switch.</summary>
public sealed record Macro
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required IReadOnlyList<MacroStep> Steps { get; init; }

    /// <summary>How many times to run the whole macro. 0 = until stopped.</summary>
    public int Repeat { get; init; } = 1;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public static string NewId() => ShortId.New("mc_");
}

/// <summary>User-supplied macro fields, validated before saving or running.</summary>
public sealed record MacroInput(string? Name, string? Description, IReadOnlyList<MacroStep>? Steps, int? Repeat)
{
    public (string Name, string? Description, IReadOnlyList<MacroStep> Steps, int Repeat) Validate()
    {
        var errors = new Dictionary<string, string[]>();
        var name = Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 64 || name.Any(char.IsControl))
        {
            errors["name"] = ["Name is required (1–64 characters)."];
        }

        var description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        if (description is { Length: > 500 })
        {
            errors["description"] = ["Description must be at most 500 characters."];
        }

        var repeat = Repeat ?? 1;
        if (repeat is < 0 or > MacroRules.MaxRepeat)
        {
            errors["repeat"] = [$"Repeat must be between 0 (until stopped) and {MacroRules.MaxRepeat}."];
        }

        var steps = Steps ?? [];
        if (steps.Count == 0)
        {
            errors["steps"] = ["A macro needs at least one step."];
        }
        else
        {
            var total = 0;
            ValidateSteps(steps, "steps", 1, ref total, errors);
            if (total > MacroRules.MaxSteps)
            {
                errors["steps"] = [$"A macro can have at most {MacroRules.MaxSteps} steps in total."];
            }

            if (repeat == 0 && !TakesTime(steps))
            {
                errors["repeat"] = ["A macro that repeats until stopped needs at least one step that takes time (a press, wait, or timed stick move)."];
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return (name, description, steps, repeat);
    }

    private static void ValidateSteps(IReadOnlyList<MacroStep> steps, string path, int depth, ref int total, Dictionary<string, string[]> errors)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            total++;
            var step = steps[i];
            var at = string.Create(CultureInfo.InvariantCulture, $"{path}[{i}]");
            if (step is null)
            {
                errors[at] = ["Step is missing."];
                continue;
            }

            switch (step.Type)
            {
                case MacroStepType.Press:
                    Require(step.Button is not null, at + ".button", "Choose a button.", errors);
                    if (step.DurationMs is { } press && press is < MacroRules.MinPressMs or > MacroRules.MaxPressMs)
                    {
                        errors[at + ".durationMs"] = [$"Press duration must be {MacroRules.MinPressMs}–{MacroRules.MaxPressMs} ms."];
                    }

                    break;
                case MacroStepType.Hold:
                case MacroStepType.Release:
                    Require(step.Button is not null, at + ".button", "Choose a button.", errors);
                    break;
                case MacroStepType.Stick:
                    Require(step.Stick is not null, at + ".stick", "Choose a stick.", errors);
                    Require(step.X is { } x && double.IsFinite(x) && Math.Abs(x) <= 1 && step.Y is { } y && double.IsFinite(y) && Math.Abs(y) <= 1, at + ".x", "x and y must be between -1 and 1.", errors);
                    if (step.DurationMs is { } move && move is < MacroRules.MinPressMs or > MacroRules.MaxPressMs)
                    {
                        errors[at + ".durationMs"] = [$"Stick duration must be {MacroRules.MinPressMs}–{MacroRules.MaxPressMs} ms."];
                    }

                    break;
                case MacroStepType.Wait:
                    Require(step.DurationMs is >= 1 and <= MacroRules.MaxWaitMs, at + ".durationMs", $"Wait must be 1–{MacroRules.MaxWaitMs} ms.", errors);
                    break;
                case MacroStepType.Repeat:
                    Require(step.Count is >= 1 and <= MacroRules.MaxRepeat, at + ".count", $"Repeat count must be 1–{MacroRules.MaxRepeat}.", errors);
                    if (depth >= MacroRules.MaxDepth)
                    {
                        errors[at] = [$"Repeats can be nested at most {MacroRules.MaxDepth - 1} deep."];
                    }
                    else if (step.Steps is not { Count: > 0 } nested)
                    {
                        errors[at + ".steps"] = ["A repeat needs at least one step."];
                    }
                    else
                    {
                        ValidateSteps(nested, at + ".steps", depth + 1, ref total, errors);
                    }

                    break;
                default:
                    errors[at + ".type"] = ["Unknown step type."];
                    break;
            }
        }
    }

    private static bool TakesTime(IReadOnlyList<MacroStep> steps) => steps.Any(s => s.Type switch
    {
        MacroStepType.Press or MacroStepType.Wait => true,
        MacroStepType.Stick => s.DurationMs is not null,
        MacroStepType.Repeat => s.Steps is { } nested && TakesTime(nested),
        _ => false,
    });

    private static void Require(bool ok, string field, string message, Dictionary<string, string[]> errors)
    {
        if (!ok)
        {
            errors[field] = [message];
        }
    }
}

public static class MacroRules
{
    public const int DefaultPressMs = 100;
    public const int MinPressMs = 20;
    public const int MaxPressMs = 10_000;
    public const int MaxWaitMs = 3_600_000;
    public const int MaxRepeat = 100_000;
    public const int MaxSteps = 1_000;
    public const int MaxDepth = 4;
}

public interface IMacroRepository
{
    Task<IReadOnlyList<Macro>> ListAsync(CancellationToken cancellationToken);

    Task<Macro?> GetAsync(string id, CancellationToken cancellationToken);

    Task AddAsync(Macro macro, CancellationToken cancellationToken);

    Task<bool> UpdateAsync(Macro macro, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}
