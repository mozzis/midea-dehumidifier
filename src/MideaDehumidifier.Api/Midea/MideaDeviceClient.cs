using Microsoft.Extensions.Options;
using MideaDehumidifier.Api.Storage;

namespace MideaDehumidifier.Api.Midea;

/// <summary>
/// Reads the dehumidifier's status over the LAN. Resolves the appliance ID (UDP discovery) and the V3
/// token/key (config, then the SQLite cache, then the Midea cloud) as needed.
/// </summary>
public sealed class MideaDeviceClient(
    IOptions<MideaOptions> options,
    ReadingStore store,
    MideaCloudClient cloud,
    TimeProvider time,
    ILogger<MideaDeviceClient> log)
{
    private const string TokenSetting = "token";
    private const string KeySetting = "key";
    private const string DeviceIdSetting = "device_id";

    private byte _messageId;

    public async Task<DehumidifierStatus> QueryAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Host)) throw new InvalidOperationException("Midea:Host is not configured.");

        var deviceId = await ResolveDeviceIdAsync(ct);

        if (!string.IsNullOrWhiteSpace(o.Token) && !string.IsNullOrWhiteSpace(o.Key))
            return await QueryWithAsync(deviceId, Convert.FromHexString(o.Token), Convert.FromHexString(o.Key), ct);

        if (store.GetSetting(TokenSetting) is { } token && store.GetSetting(KeySetting) is { } key)
        {
            try
            {
                return await QueryWithAsync(deviceId, Convert.FromHexString(token), Convert.FromHexString(key), ct);
            }
            catch (MideaAuthenticationException e)
            {
                log.LogWarning("Cached token/key was rejected ({Message}); fetching a new one from the cloud.", e.Message);
                store.DeleteSetting(TokenSetting);
                store.DeleteSetting(KeySetting);
            }
        }

        // The cloud derives the device's udpid from its ID; which byte order it expects varies by device, so try both.
        foreach (var littleEndian in new[] { true, false })
        {
            var udpid = MideaCrypto.Udpid(deviceId, littleEndian);
            log.LogInformation("Requesting token/key from the {Cloud} cloud for udpid {Udpid}.", o.Cloud, udpid);
            var (cloudToken, cloudKey) = await cloud.GetTokenAsync(udpid, deviceId, ct);
            try
            {
                var status = await QueryWithAsync(deviceId, Convert.FromHexString(cloudToken), Convert.FromHexString(cloudKey), ct);
                store.SetSetting(TokenSetting, cloudToken);
                store.SetSetting(KeySetting, cloudKey);
                log.LogInformation("Token/key accepted by the device and cached in the database.");
                return status;
            }
            catch (MideaAuthenticationException e)
            {
                log.LogWarning("Device rejected the token/key for udpid {Udpid}: {Message}", udpid, e.Message);
            }
        }

        throw new MideaAuthenticationException("The device rejected every token/key the cloud supplied. " +
            "Check Midea:Cloud (NetHomePlus or SmartHome) matches the app the device is registered in.");
    }

    private async Task<long> ResolveDeviceIdAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (o.DeviceId != 0) return o.DeviceId;
        if (store.GetLong(DeviceIdSetting) is var cached and not 0) return cached;

        var info = await MideaDiscovery.DiscoverAsync(o.Host, ct);
        log.LogInformation("Discovered {Name} (serial {Serial}, ID {Id}, protocol V{Version}).",
            info.Name, info.Serial, info.DeviceId, info.ProtocolVersion);
        if (info.ProtocolVersion != 3)
            throw new NotSupportedException($"Only V3 devices are supported; this device speaks V{info.ProtocolVersion}.");

        store.SetSetting(DeviceIdSetting, info.DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return info.DeviceId;
    }

    private async Task<DehumidifierStatus> QueryWithAsync(long deviceId, byte[] token, byte[] key, CancellationToken ct)
    {
        var o = options.Value;
        await using var session = await MideaLanSession.ConnectAsync(o.Host, o.Port, ct);
        await session.AuthenticateAsync(token, key, ct);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await session.SendFrameAsync(deviceId, DehumidifierProtocol.BuildStatusQuery(++_messageId), time, ct);

            for (var frames = 0; frames < 10; frames++)
            {
                byte[] frame;
                try
                {
                    frame = await session.ReadFrameAsync(TimeSpan.FromSeconds(5), ct);
                }
                catch (TimeoutException)
                {
                    break; // resend the query
                }

                if (DehumidifierProtocol.TryParseStatus(frame) is { } status) return status;
                log.LogDebug("Ignoring non-status frame: {Frame}", Convert.ToHexStringLower(frame));
            }
        }

        throw new TimeoutException("The device did not answer the status query.");
    }
}
