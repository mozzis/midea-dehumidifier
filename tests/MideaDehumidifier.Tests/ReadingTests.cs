using MideaDehumidifier.Api.Models;

namespace MideaDehumidifier.Tests;

public class HumidityBandTests
{
    [Theory]
    [InlineData(34, 40, HumidityBand.Low)]
    [InlineData(35, 40, HumidityBand.Normal)]
    [InlineData(40, 40, HumidityBand.Normal)]
    [InlineData(45, 40, HumidityBand.Normal)]
    [InlineData(46, 40, HumidityBand.High)]
    [InlineData(60, 50, HumidityBand.High)]
    [InlineData(44.9, 50, HumidityBand.Low)]
    public void Uses_five_point_band_around_setpoint(double humidity, double target, HumidityBand expected) =>
        Assert.Equal(expected, HumidityBands.Classify(humidity, target));
}

public class ReadingSamplerTests
{
    private static Reading Make(int minute, int humidity, bool power = true) => new()
    {
        TimestampUtc = DateTimeOffset.UnixEpoch.AddMinutes(minute),
        PowerOn = power,
        Humidity = humidity,
        TargetHumidity = 50,
        TemperatureC = 20,
        Mode = 1, FanSpeed = 40, TankLevel = 0, TankFull = false, Defrosting = false, ErrorCode = 0, RawHex = "",
    };

    [Fact]
    public void Returns_input_when_under_limit()
    {
        var readings = Enumerable.Range(0, 10).Select(i => Make(i, 50)).ToList();
        Assert.Same(readings, ReadingSampler.Downsample(readings, 100));
    }

    [Fact]
    public void Reduces_count_but_keeps_ends()
    {
        var readings = Enumerable.Range(0, 1000).Select(i => Make(i, 50)).ToList();
        var result = ReadingSampler.Downsample(readings, 100);
        Assert.InRange(result.Count, 90, 110);
        Assert.Equal(readings[0], result[0]);
        Assert.Equal(readings[^1], result[^1]);
    }

    [Fact]
    public void Keeps_every_band_and_power_transition()
    {
        // Humidity stays in band except for one low reading, and power flips once.
        var readings = Enumerable.Range(0, 1000).Select(i => Make(i, i == 503 ? 30 : 50, power: i < 777)).ToList();
        var result = ReadingSampler.Downsample(readings, 100);

        Assert.Contains(result, r => r.Humidity == 30);
        Assert.Contains(result, r => r.TimestampUtc == readings[776].TimestampUtc);
        Assert.Contains(result, r => r.TimestampUtc == readings[777].TimestampUtc);
    }
}
