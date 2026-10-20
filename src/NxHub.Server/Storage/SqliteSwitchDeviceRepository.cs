using System.Globalization;
using Microsoft.Data.Sqlite;
using NxHub.Core.Devices;

namespace NxHub.Server.Storage;

/// <summary>SQLite-backed device storage.</summary>
public sealed class SqliteSwitchDeviceRepository : ISwitchDeviceRepository
{
    private readonly SqliteDatabase _database;

    public SqliteSwitchDeviceRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<SwitchDevice>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, host, port, enabled, created_at, updated_at FROM switches ORDER BY created_at, id;";
        var results = new List<SwitchDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<SwitchDevice?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, host, port, enabled, created_at, updated_at FROM switches WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task AddAsync(SwitchDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO switches (id, name, host, port, enabled, created_at, updated_at)
            VALUES ($id, $name, $host, $port, $enabled, $created, $updated);
            """;
        Bind(command, device);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UpdateAsync(SwitchDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE switches
            SET name = $name, host = $host, port = $port, enabled = $enabled, updated_at = $updated
            WHERE id = $id;
            """;
        Bind(command, device);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM switches WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void Bind(SqliteCommand command, SwitchDevice device)
    {
        command.Parameters.AddWithValue("$id", device.Id);
        command.Parameters.AddWithValue("$name", device.Name);
        command.Parameters.AddWithValue("$host", device.Host);
        command.Parameters.AddWithValue("$port", device.Port);
        command.Parameters.AddWithValue("$enabled", device.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$created", device.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", device.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static SwitchDevice Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Host = reader.GetString(2),
        Port = reader.GetInt32(3),
        Enabled = reader.GetInt64(4) != 0,
        CreatedAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };
}
