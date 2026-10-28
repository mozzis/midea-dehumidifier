using MideaDehumidifier.Api.Models;
using MideaDehumidifier.Api.Storage;

namespace MideaDehumidifier.Tests;

public sealed class ReadingStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "midea-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ReadingStore _store;

    public ReadingStoreTests() => _store = new ReadingStore(Path.Combine(_directory, "test.db"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static Reading Make(DateTimeOffset at, int humidity, bool power) => new()
    {
        TimestampUtc = at,
        PowerOn = power,
        Humidity = humidity,
        TargetHumidity = 50.5,
        TemperatureC = 21.5,
        Mode = 3, FanSpeed = 40, TankLevel = 2, TankFull = false, Defrosting = true, ErrorCode = 0, RawHex = "c801",
    };

    [Fact]
    public void Round_trips_a_reading()
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_123);
        _store.Insert(Make(at, 47, true));

        var loaded = Assert.Single(_store.GetRange(at.AddMinutes(-1), at.AddMinutes(1)));

        Assert.Equal(Make(at, 47, true), loaded);
    }

    [Fact]
    public void Range_query_is_ordered_and_bounded()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        foreach (var i in new[] { 3, 1, 2, 0, 4 }) _store.Insert(Make(t0.AddMinutes(i), 40 + i, true));

        var range = _store.GetRange(t0.AddMinutes(1), t0.AddMinutes(3));

        Assert.Equal([41, 42, 43], range.Select(r => r.Humidity));
        Assert.Equal(44, _store.GetLatest()!.Humidity);
    }

    [Fact]
    public void Latest_is_null_when_empty() => Assert.Null(_store.GetLatest());

    [Fact]
    public void Settings_can_be_set_replaced_and_deleted()
    {
        Assert.Null(_store.GetSetting("token"));
        _store.SetSetting("token", "aa");
        _store.SetSetting("token", "bb");
        Assert.Equal("bb", _store.GetSetting("token"));
        _store.DeleteSetting("token");
        Assert.Null(_store.GetSetting("token"));
    }
}
