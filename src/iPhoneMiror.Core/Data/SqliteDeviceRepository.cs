using System.Globalization;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using Microsoft.Data.Sqlite;

namespace iPhoneMiror.Core.Data;

/// <summary>SQLite persistence for recent devices and the connection event log.</summary>
public sealed class SqliteDeviceRepository : IDeviceRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteDeviceRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public static SqliteDeviceRepository ForFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new SqliteDeviceRepository(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS devices (
                udid TEXT PRIMARY KEY,
                name TEXT,
                product_type TEXT,
                product_version TEXT,
                connection_type INTEGER NOT NULL,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                auto_reconnect INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS connection_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                udid TEXT NOT NULL,
                ts TEXT NOT NULL,
                kind TEXT NOT NULL,
                detail TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_events_udid_ts ON connection_events(udid, ts);
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAsync(DeviceInfo device, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO devices (udid, name, product_type, product_version, connection_type, first_seen, last_seen, auto_reconnect)
                VALUES ($udid, $name, $pt, $pv, $ct, $now, $now, 1)
                ON CONFLICT(udid) DO UPDATE SET
                    name = COALESCE($name, name),
                    product_type = COALESCE($pt, product_type),
                    product_version = COALESCE($pv, product_version),
                    connection_type = $ct,
                    last_seen = $now;
                """;
            cmd.Parameters.AddWithValue("$udid", device.Udid);
            cmd.Parameters.AddWithValue("$name", (object?)device.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pt", (object?)device.ProductType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pv", (object?)device.ProductVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ct", (int)device.ConnectionType);
            cmd.Parameters.AddWithValue("$now", device.LastSeen.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DeviceRecord>> GetRecentAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT udid, name, product_type, product_version, connection_type, first_seen, last_seen, auto_reconnect FROM devices ORDER BY last_seen DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<DeviceRecord>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) list.Add(ReadDevice(r));
        return list;
    }

    public async Task<DeviceRecord?> GetAsync(string udid, CancellationToken cancellationToken = default)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT udid, name, product_type, product_version, connection_type, first_seen, last_seen, auto_reconnect FROM devices WHERE udid = $udid";
        cmd.Parameters.AddWithValue("$udid", udid);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDevice(r) : null;
    }

    public async Task SetAutoReconnectAsync(string udid, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE devices SET auto_reconnect = $v WHERE udid = $udid";
        cmd.Parameters.AddWithValue("$v", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$udid", udid);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string udid, CancellationToken cancellationToken = default)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM devices WHERE udid = $udid; DELETE FROM connection_events WHERE udid = $udid;";
        cmd.Parameters.AddWithValue("$udid", udid);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddEventAsync(string udid, string kind, string? detail, CancellationToken cancellationToken = default)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO connection_events (udid, ts, kind, detail) VALUES ($udid, $ts, $kind, $detail)";
        cmd.Parameters.AddWithValue("$udid", udid);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConnectionEvent>> GetEventsAsync(string? udid, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = udid is null
            ? "SELECT id, udid, ts, kind, detail FROM connection_events ORDER BY id DESC LIMIT $limit"
            : "SELECT id, udid, ts, kind, detail FROM connection_events WHERE udid = $udid ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        if (udid is not null) cmd.Parameters.AddWithValue("$udid", udid);
        var list = new List<ConnectionEvent>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new ConnectionEvent(
                r.GetInt64(0), r.GetString(1),
                DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        }
        return list;
    }

    private static DeviceRecord ReadDevice(SqliteDataReader r) => new(
        r.GetString(0),
        r.IsDBNull(1) ? null : r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        (ConnectionType)r.GetInt32(4),
        DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
        r.GetInt32(7) != 0);
}
