using Microsoft.Extensions.Options;
using MideaDehumidifier.Api.Midea;
using MideaDehumidifier.Api.Models;
using MideaDehumidifier.Api.Storage;

namespace MideaDehumidifier.Api.Services;

/// <summary>Latest known reading plus polling health. Read by the HTTP endpoints.</summary>
public sealed class DeviceState
{
    private volatile Reading? _latest;
    private volatile string? _lastError;
    private long _lastErrorTicks;

    public Reading? Latest => _latest;
    public string? LastError => _lastError;
    public DateTimeOffset? LastErrorAtUtc =>
        Interlocked.Read(ref _lastErrorTicks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    public void SetReading(Reading reading)
    {
        _latest = reading;
        _lastError = null;
    }

    public void SetError(string message, DateTimeOffset at)
    {
        _lastError = message;
        Interlocked.Exchange(ref _lastErrorTicks, at.UtcTicks);
    }
}

/// <summary>Polls the dehumidifier on a fixed interval and appends every reading to SQLite.</summary>
public sealed class DehumidifierPoller(
    MideaDeviceClient client,
    ReadingStore store,
    DeviceState state,
    IOptions<MideaOptions> options,
    TimeProvider time,
    ILogger<DehumidifierPoller> log) : BackgroundService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CredentialRetry = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        if (interval < MinInterval) interval = MinInterval;

        // Resume the chart from what is already stored so a restart shows history immediately.
        if (store.GetLatest() is { } previous) state.SetReading(previous);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = interval;
            try
            {
                var status = await client.QueryAsync(stoppingToken);
                var reading = ToReading(status, time.GetUtcNow());
                store.Insert(reading);
                state.SetReading(reading);
                log.LogDebug("Humidity {Humidity}% (setpoint {Target}%), power {Power}.",
                    reading.Humidity, reading.TargetHumidity, reading.PowerOn ? "on" : "off");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                state.SetError(ex.Message, time.GetUtcNow());
                log.LogWarning(ex, "Polling the dehumidifier failed.");
                // Don't hammer the Midea cloud with bad credentials.
                if (ex is MideaCloudException or MideaAuthenticationException) delay = CredentialRetry;
            }

            try
            {
                await Task.Delay(delay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public static Reading ToReading(DehumidifierStatus s, DateTimeOffset at) => new()
    {
        TimestampUtc = at,
        PowerOn = s.PowerOn,
        Humidity = s.CurrentHumidity,
        TargetHumidity = s.TargetHumidity,
        TemperatureC = s.TemperatureC,
        Mode = s.Mode,
        FanSpeed = s.FanSpeed,
        TankLevel = s.TankLevel,
        TankFull = s.TankFull,
        Defrosting = s.Defrosting,
        ErrorCode = s.ErrorCode,
        RawHex = s.RawHex,
    };
}
