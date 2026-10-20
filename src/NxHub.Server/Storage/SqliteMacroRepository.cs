using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NxHub.Core.Macros;

namespace NxHub.Server.Storage;

/// <summary>SQLite-backed macro storage. The definition (description, steps, repeat) is stored as JSON.</summary>
public sealed class SqliteMacroRepository : IMacroRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SqliteDatabase _database;

    public SqliteMacroRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<Macro>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, definition, created_at, updated_at FROM macros ORDER BY name COLLATE NOCASE, id;";
        var results = new List<Macro>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<Macro?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, definition, created_at, updated_at FROM macros WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task AddAsync(Macro macro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(macro);
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO macros (id, name, definition, created_at, updated_at) VALUES ($id, $name, $definition, $created, $updated);";
        Bind(command, macro);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UpdateAsync(Macro macro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(macro);
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE macros SET name = $name, definition = $definition, updated_at = $updated WHERE id = $id;";
        Bind(command, macro);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM macros WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void Bind(SqliteCommand command, Macro macro)
    {
        var definition = new StoredDefinition(macro.Description, macro.Steps, macro.Repeat);
        command.Parameters.AddWithValue("$id", macro.Id);
        command.Parameters.AddWithValue("$name", macro.Name);
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(definition, Json));
        command.Parameters.AddWithValue("$created", macro.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", macro.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static Macro Map(SqliteDataReader reader)
    {
        var definition = JsonSerializer.Deserialize<StoredDefinition>(reader.GetString(2), Json)
            ?? throw new InvalidOperationException("Corrupt macro definition.");
        return new Macro
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Description = definition.Description,
            Steps = definition.Steps,
            Repeat = definition.Repeat,
            CreatedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };
    }

    private sealed record StoredDefinition(string? Description, IReadOnlyList<MacroStep> Steps, int Repeat);
}
