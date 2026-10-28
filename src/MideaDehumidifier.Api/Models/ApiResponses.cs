namespace MideaDehumidifier.Api.Models;

/// <summary>A reading as served to the front end.</summary>
public sealed record ReadingDto
{
    /// <summary>When the reading was taken (UTC).</summary>
    public required DateTimeOffset TimestampUtc { get; init; }
    /// <summary>Measured room humidity, percent.</summary>
    public required int Humidity { get; init; }
    /// <summary>Device humidity setpoint, percent.</summary>
    public required double TargetHumidity { get; init; }
    /// <summary>Humidity relative to the setpoint: <c>low</c> (&gt;5 below, red), <c>normal</c> (within ±5, green) or <c>high</c> (&gt;5 above, yellow).</summary>
    public required HumidityBand Band { get; init; }
    /// <summary>True when the unit is running (plotted solid); false when off (plotted dashed).</summary>
    public required bool PowerOn { get; init; }
    public required double TemperatureC { get; init; }
    public required int Mode { get; init; }
    public required int FanSpeed { get; init; }
    public required int TankLevel { get; init; }
    public required bool TankFull { get; init; }
    public required bool Defrosting { get; init; }
    public required int ErrorCode { get; init; }

    public static ReadingDto From(Reading r) => new()
    {
        TimestampUtc = r.TimestampUtc,
        Humidity = r.Humidity,
        TargetHumidity = r.TargetHumidity,
        Band = r.Band,
        PowerOn = r.PowerOn,
        TemperatureC = r.TemperatureC,
        Mode = r.Mode,
        FanSpeed = r.FanSpeed,
        TankLevel = r.TankLevel,
        TankFull = r.TankFull,
        Defrosting = r.Defrosting,
        ErrorCode = r.ErrorCode,
    };
}

public sealed record ReadingsResponse
{
    public required DateTimeOffset FromUtc { get; init; }
    public required DateTimeOffset ToUtc { get; init; }
    /// <summary>Number of stored readings in the range before any downsampling.</summary>
    public required int TotalCount { get; init; }
    public required IReadOnlyList<ReadingDto> Readings { get; init; }
}

public sealed record HealthResponse
{
    public required string Status { get; init; }
    public DateTimeOffset? LastReadingUtc { get; init; }
    public double? AgeSeconds { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastErrorAtUtc { get; init; }
}
