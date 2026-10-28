using System.Net.Sockets;
using System.Text;

namespace MideaDehumidifier.Api.Midea;

public sealed record MideaDeviceInfo(long DeviceId, string Serial, string Name, int ProtocolVersion);

/// <summary>Asks a single device for its ID and protocol version with a unicast UDP discovery packet.</summary>
public static class MideaDiscovery
{
    private const int DiscoveryPort = 6445;

    private static readonly byte[] DiscoveryMessage = Convert.FromHexString(
        "5a5a011148009200" + new string('0', 64) +
        "7f75bd6b3e4f8b762e849c6e578d6590036e9d4342a50f1f569eb8ec918e92e5");

    public static async Task<MideaDeviceInfo> DiscoverAsync(string host, CancellationToken ct)
    {
        using var udp = new UdpClient();
        for (var attempt = 1; ; attempt++)
        {
            await udp.SendAsync(DiscoveryMessage, host, DiscoveryPort, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                return Parse(result.Buffer);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt == 3) throw new TimeoutException($"No discovery response from {host}:{DiscoveryPort}.");
            }
        }
    }

    public static MideaDeviceInfo Parse(byte[] data)
    {
        int version;
        ReadOnlySpan<byte> inner;
        if (data.Length > 2 && data[0] == 0x83 && data[1] == 0x70)
        {
            version = 3;
            inner = data.AsSpan(8, data.Length - 8 - 16);
        }
        else if (data.Length > 2 && data[0] == 0x5A && data[1] == 0x5A)
        {
            version = 2;
            inner = data;
        }
        else
        {
            throw new MideaProtocolException("Unrecognised discovery response.");
        }

        if (inner.Length < 40 + 16 + 16) throw new MideaProtocolException("Discovery response is too short.");

        var deviceId = 0L;
        for (var i = 5; i >= 0; i--) deviceId = deviceId << 8 | inner[20 + i];

        var decrypted = MideaCrypto.DecryptEcb(inner[40..^16]);
        var serial = Encoding.ASCII.GetString(decrypted, 8, 32);
        var name = Encoding.ASCII.GetString(decrypted, 41, decrypted[40]);
        return new MideaDeviceInfo(deviceId, serial, name, version);
    }
}
