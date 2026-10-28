using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MideaDehumidifier.Api.Midea;

/// <summary>Primitives of the Midea LAN protocol (V2 packets wrapped by V3 "8370" packets).</summary>
public static class MideaCrypto
{
    private static readonly byte[] SignKey = "xhdiwjnchekd4d512chdjx5d8e4c394D2D7S"u8.ToArray();
    private static readonly byte[] EncKey = MD5.HashData(SignKey);

    public static byte[] DecryptEcb(ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = EncKey;
        return aes.DecryptEcb(data, PaddingMode.PKCS7);
    }

    public static byte[] EncryptEcb(ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = EncKey;
        return aes.EncryptEcb(data, PaddingMode.PKCS7);
    }

    public static byte[] DecryptCbc(byte[] key, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data, new byte[16], PaddingMode.None);
    }

    public static byte[] EncryptCbc(byte[] key, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
    }

    public static byte[] Sign(ReadOnlySpan<byte> data)
    {
        var buffer = new byte[data.Length + SignKey.Length];
        data.CopyTo(buffer);
        SignKey.CopyTo(buffer, data.Length);
        return MD5.HashData(buffer);
    }

    /// <summary>The cloud identifies a device by this value: SHA-256 of the 6-byte ID, first half XOR second half.</summary>
    public static string Udpid(ReadOnlySpan<byte> deviceIdBytes)
    {
        var hash = SHA256.HashData(deviceIdBytes);
        var result = new byte[16];
        for (var i = 0; i < 16; i++) result[i] = (byte)(hash[i] ^ hash[i + 16]);
        return Convert.ToHexStringLower(result);
    }

    public static string Udpid(long deviceId, bool littleEndian)
    {
        Span<byte> bytes = stackalloc byte[8];
        if (littleEndian) BinaryPrimitives.WriteInt64LittleEndian(bytes, deviceId);
        else BinaryPrimitives.WriteInt64BigEndian(bytes, deviceId);
        return Udpid(littleEndian ? bytes[..6] : bytes[2..]);
    }
}

/// <summary>V2 packet ("5A5A"): 40 byte header, AES-ECB encrypted frame, 16 byte MD5 signature.</summary>
public static class MideaPacket
{
    public static byte[] Encode(long deviceId, ReadOnlySpan<byte> frame, DateTimeOffset now)
    {
        var encrypted = MideaCrypto.EncryptEcb(frame);
        var length = 40 + encrypted.Length + 16;

        var packet = new byte[length - 16];
        packet[0] = 0x5A; packet[1] = 0x5A;
        packet[2] = 0x01; packet[3] = 0x11;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), (ushort)length);
        packet[6] = 0x20; packet[7] = 0x00;
        // bytes 8-11: message ID (zero); 12-19: timestamp; 20-27: device ID; 28-39: unknown (zero)
        var utc = now.UtcDateTime;
        packet[12] = (byte)(utc.Millisecond / 10);
        packet[13] = (byte)utc.Second;
        packet[14] = (byte)utc.Minute;
        packet[15] = (byte)utc.Hour;
        packet[16] = (byte)utc.Day;
        packet[17] = (byte)utc.Month;
        packet[18] = (byte)(utc.Year % 100);
        packet[19] = (byte)(utc.Year / 100);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(20), deviceId);
        encrypted.CopyTo(packet, 40);

        return [.. packet, .. MideaCrypto.Sign(packet)];
    }

    public static byte[] Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 56) throw new MideaProtocolException($"Packet is too short ({packet.Length} bytes).");
        if (packet[0] != 0x5A || packet[1] != 0x5A) throw new MideaProtocolException("Unsupported packet: missing 5A5A header.");

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]);
        if (length < 56 || packet.Length < length) throw new MideaProtocolException("Packet is truncated.");
        packet = packet[..length];

        if (!MideaCrypto.Sign(packet[..^16]).AsSpan().SequenceEqual(packet[^16..]))
            throw new MideaProtocolException("Packet signature does not match.");

        return MideaCrypto.DecryptEcb(packet[40..^16]);
    }
}

public class MideaProtocolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The device rejected the token/key (or the handshake was malformed).</summary>
public sealed class MideaAuthenticationException(string message, Exception? inner = null) : MideaProtocolException(message, inner);
