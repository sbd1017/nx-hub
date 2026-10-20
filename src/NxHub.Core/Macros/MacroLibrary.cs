using NxHub.Core.Errors;
using NxHub.Core.Events;

namespace NxHub.Core.Macros;

/// <summary>Saved macros: validation plus storage. Shared by every Switch.</summary>
public sealed class MacroLibrary
{
    private readonly IMacroRepository _repository;
    private readonly IEventPublisher _events;
    private readonly TimeProvider _time;

    public MacroLibrary(IMacroRepository repository, IEventPublisher events, TimeProvider time)
    {
        _repository = repository;
        _events = events;
        _time = time;
    }

    public Task<IReadOnlyList<Macro>> ListAsync(CancellationToken cancellationToken) => _repository.ListAsync(cancellationToken);

    public async Task<Macro> GetAsync(string id, CancellationToken cancellationToken) =>
        await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw NotFound(id);

    public async Task<Macro> AddAsync(MacroInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (name, description, steps, repeat) = input.Validate();
        var now = _time.GetUtcNow();
        var macro = new Macro
        {
            Id = Macro.NewId(),
            Name = name,
            Description = description,
            Steps = steps,
            Repeat = repeat,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _repository.AddAsync(macro, cancellationToken).ConfigureAwait(false);
        _events.Publish(new NxHubEvent(EventTypes.MacroSaved, null, now, macro));
        return macro;
    }

    public async Task<Macro> UpdateAsync(string id, MacroInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var existing = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        var (name, description, steps, repeat) = input.Validate();
        var macro = existing with { Name = name, Description = description, Steps = steps, Repeat = repeat, UpdatedAt = _time.GetUtcNow() };
        if (!await _repository.UpdateAsync(macro, cancellationToken).ConfigureAwait(false))
        {
            throw NotFound(id);
        }

        _events.Publish(new NxHubEvent(EventTypes.MacroSaved, null, macro.UpdatedAt, macro));
        return macro;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (!await _repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false))
        {
            throw NotFound(id);
        }

        _events.Publish(new NxHubEvent(EventTypes.MacroDeleted, null, _time.GetUtcNow(), new { id }));
    }

    /// <summary>Builds an unsaved macro for a one-off run.</summary>
    public Macro Transient(MacroInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (name, description, steps, repeat) = input.Validate();
        var now = _time.GetUtcNow();
        return new Macro { Id = "adhoc", Name = name, Description = description, Steps = steps, Repeat = repeat, CreatedAt = now, UpdatedAt = now };
    }

    private static NxHubException NotFound(string id) =>
        new(ErrorKind.NotFound, "macro_not_found", $"No macro with id '{id}' exists.");
}
