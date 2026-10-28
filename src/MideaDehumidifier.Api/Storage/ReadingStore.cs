using System.Globalization;
using Microsoft.Data.Sqlite;
using MideaDehumidifier.Api.Models;

namespace MideaDehumidifier.Api.Storage;

/// <summary>SQLite storage for readings and cached device credentials.</summary>
public sealed class ReadingStore
{
    private readonly string _connectionString;

    public ReadingStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS readings (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_ms           INTEGER NOT NULL,
                power_on        INTEGER NOT NULL,
                humidity        INTEGER NOT NULL,
                target_humidity REAL    NOT NULL,
                temperature_c   REAL    NOT NULL,
                mode            INTEGER NOT NULL,
                fan_speed       INTEGER NOT NULL,
                tank_level      INTEGER NOT NULL,
                tank_full       INTEGER NOT NULL,
                defrosting      INTEGER NOT NULL,
                error_code      INTEGER NOT NULL,
                raw_hex         TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_readings_ts ON readings (ts_ms);
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public void Insert(Reading r)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO readings (ts_ms, power_on, humidity, target_humidity, temperature_c, mode, fan_speed,
                                  tank_level, tank_full, defrosting, error_code, raw_hex)
            VALUES ($ts, $power, $humidity, $target, $temp, $mode, $fan, $tank, $full, $defrost, $error, $raw)
            """;
        command.Parameters.AddWithValue("$ts", r.TimestampUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$power", r.PowerOn);
        command.Parameters.AddWithValue("$humidity", r.Humidity);
        command.Parameters.AddWithValue("$target", r.TargetHumidity);
        command.Parameters.AddWithValue("$temp", r.TemperatureC);
        command.Parameters.AddWithValue("$mode", r.Mode);
        command.Parameters.AddWithValue("$fan", r.FanSpeed);
        command.Parameters.AddWithValue("$tank", r.TankLevel);
        command.Parameters.AddWithValue("$full", r.TankFull);
        command.Parameters.AddWithValue("$defrost", r.Defrosting);
        command.Parameters.AddWithValue("$error", r.ErrorCode);
        command.Parameters.AddWithValue("$raw", r.RawHex);
        command.ExecuteNonQuery();
    }

    private const string SelectColumns = """
        SELECT ts_ms, power_on, humidity, target_humidity, temperature_c, mode, fan_speed,
               tank_level, tank_full, defrosting, error_code, raw_hex
        FROM readings
        """;

    private static Reading Read(SqliteDataReader r) => new()
    {
        TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)),
        PowerOn = r.GetBoolean(1),
        Humidity = r.GetInt32(2),
        TargetHumidity = r.GetDouble(3),
        TemperatureC = r.GetDouble(4),
        Mode = r.GetInt32(5),
        FanSpeed = r.GetInt32(6),
        TankLevel = r.GetInt32(7),
        TankFull = r.GetBoolean(8),
        Defrosting = r.GetBoolean(9),
        ErrorCode = r.GetInt32(10),
        RawHex = r.GetString(11),
    };

    /// <summary>Readings with from &lt;= timestamp &lt;= to, oldest first.</summary>
    public IReadOnlyList<Reading> GetRange(DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE ts_ms BETWEEN $from AND $to ORDER BY ts_ms, id";
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        var result = new List<Reading>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    public Reading? GetLatest()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " ORDER BY ts_ms DESC, id DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public void DeleteSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    public long GetLong(string key) =>
        long.TryParse(GetSetting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
