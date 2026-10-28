namespace MideaDehumidifier.Api.Models;

/// <summary>Where the measured humidity sits relative to the device setpoint.</summary>
public enum HumidityBand
{
    /// <summary>More than 5 percentage points below the setpoint (plotted red).</summary>
    Low,
    /// <summary>Within ±5 percentage points of the setpoint (plotted green).</summary>
    Normal,
    /// <summary>More than 5 percentage points above the setpoint (plotted yellow).</summary>
    High,
}

public static class HumidityBands
{
    public const double Tolerance = 5;

    public static HumidityBand Classify(double humidity, double target)
    {
        var delta = humidity - target;
        return delta < -Tolerance ? HumidityBand.Low : delta > Tolerance ? HumidityBand.High : HumidityBand.Normal;
    }
}

/// <summary>One stored poll of the dehumidifier.</summary>
public sealed record Reading
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required bool PowerOn { get; init; }
    public required int Humidity { get; init; }
    public required double TargetHumidity { get; init; }
    public required double TemperatureC { get; init; }
    public required int Mode { get; init; }
    public required int FanSpeed { get; init; }
    public required int TankLevel { get; init; }
    public required bool TankFull { get; init; }
    public required bool Defrosting { get; init; }
    public required int ErrorCode { get; init; }
    public required string RawHex { get; init; }

    public HumidityBand Band => HumidityBands.Classify(Humidity, TargetHumidity);
}

/// <summary>Keeps long ranges small enough to plot without hiding colour or dash changes.</summary>
public static class ReadingSampler
{
    public static IReadOnlyList<Reading> Downsample(IReadOnlyList<Reading> readings, int maxPoints)
    {
        if (maxPoints < 3 || readings.Count <= maxPoints) return readings;

        var stride = (int)Math.Ceiling((double)readings.Count / maxPoints);
        var keep = new bool[readings.Count];
        for (var i = 0; i < readings.Count; i++)
        {
            var edge = i == 0 || i == readings.Count - 1;
            var changed = i > 0 && (readings[i].Band != readings[i - 1].Band || readings[i].PowerOn != readings[i - 1].PowerOn);
            if (edge || i % stride == 0) keep[i] = true;
            if (changed) keep[i] = keep[i - 1] = true;
        }

        return readings.Where((_, i) => keep[i]).ToList();
    }
}
