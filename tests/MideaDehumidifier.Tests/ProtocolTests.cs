using System.Security.Cryptography;
using MideaDehumidifier.Api.Midea;

namespace MideaDehumidifier.Tests;

public class ProtocolTests
{
    [Fact]
    public void V2_packet_round_trips()
    {
        var frame = DehumidifierProtocol.BuildStatusQuery(7);
        var packet = MideaPacket.Encode(0x8900001DAD59, frame, DateTimeOffset.UtcNow);

        Assert.Equal(frame, MideaPacket.Decode(packet));
    }

    [Fact]
    public void V2_packet_with_bad_signature_is_rejected()
    {
        var packet = MideaPacket.Encode(1, DehumidifierProtocol.BuildStatusQuery(1), DateTimeOffset.UtcNow);
        packet[^1] ^= 0xFF;

        Assert.Throws<MideaProtocolException>(() => MideaPacket.Decode(packet));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)] // 14 + 2 byte packet id is already 16 byte aligned: no padding
    [InlineData(33)]
    public void V3_encrypted_packet_round_trips(int length)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var data = RandomNumberGenerator.GetBytes(length);

        // The device answers with the same layout, using packet type 3.
        var packet = MideaV3.EncodeEncrypted(key, 5, data, MideaV3.EncryptedResponse);

        Assert.Equal(packet.Length, MideaV3.PacketLength(packet.AsSpan(0, 6)));
        Assert.Equal(data, MideaV3.Decode(key, packet));
    }

    [Fact]
    public void V3_handshake_derives_local_key()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var secret = RandomNumberGenerator.GetBytes(32);
        byte[] handshake = [.. MideaCrypto.EncryptCbc(key, secret), .. SHA256.HashData(secret)];

        var localKey = MideaV3.DeriveLocalKey(key, handshake);

        Assert.Equal(secret.Zip(key, (s, k) => (byte)(s ^ k)), localKey);
    }

    [Fact]
    public void V3_handshake_with_wrong_key_is_an_authentication_error()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        byte[] handshake = [.. MideaCrypto.EncryptCbc(RandomNumberGenerator.GetBytes(32), secret), .. SHA256.HashData(secret)];

        Assert.Throws<MideaAuthenticationException>(() => MideaV3.DeriveLocalKey(RandomNumberGenerator.GetBytes(32), handshake));
    }

    [Fact]
    public void Status_query_has_valid_checksum_and_crc()
    {
        var frame = DehumidifierProtocol.BuildStatusQuery(9);

        Assert.Equal(33, frame.Length);
        Assert.Equal(0xAA, frame[0]);
        Assert.Equal(frame.Length - 1, frame[1]);
        Assert.Equal(0xA1, frame[2]);
        Assert.Equal(9, frame[30]);
        Assert.Equal(DehumidifierProtocol.Crc8(frame.AsSpan(10, 21)), frame[31]);
        Assert.Equal(0, (frame[1..].Sum(b => b)) & 0xFF);
    }

    private static byte[] StatusFrame(Action<byte[]> customise)
    {
        // 10 byte header + 22 byte payload + checksum
        var frame = new byte[33];
        frame[0] = 0xAA; frame[1] = 0x20; frame[2] = 0xA1; frame[9] = 0x03;
        frame[10] = 0xC8;
        customise(frame);
        frame[^1] = DehumidifierProtocol.Checksum(frame.AsSpan(1, frame.Length - 2));
        return frame;
    }

    [Fact]
    public void Parses_status_response()
    {
        var frame = StatusFrame(f =>
        {
            f[11] = 0x01;          // running
            f[12] = 0x03;          // mode
            f[13] = 0x28;          // fan speed 40
            f[17] = 55;            // setpoint 55%
            f[20] = 0x82;          // defrosting + tank level 2
            f[26] = 47;            // current humidity
            f[27] = 50 + 2 * 21;   // 21.0 C
            f[28] = 0x05;          // +0.5
        });

        var status = DehumidifierProtocol.TryParseStatus(frame)!;

        Assert.True(status.PowerOn);
        Assert.Equal(3, status.Mode);
        Assert.Equal(40, status.FanSpeed);
        Assert.Equal(55, status.TargetHumidity);
        Assert.Equal(47, status.CurrentHumidity);
        Assert.Equal(21.5, status.TemperatureC);
        Assert.Equal(2, status.TankLevel);
        Assert.False(status.TankFull);
        Assert.True(status.Defrosting);
    }

    [Fact]
    public void Off_unit_is_reported_as_power_off()
    {
        var status = DehumidifierProtocol.TryParseStatus(StatusFrame(f => { f[11] = 0x00; f[26] = 60; f[27] = 90; }))!;

        Assert.False(status.PowerOn);
        Assert.Equal(60, status.CurrentHumidity);
    }

    [Fact]
    public void Ignores_non_status_and_corrupt_frames()
    {
        Assert.Null(DehumidifierProtocol.TryParseStatus(StatusFrame(f => f[10] = 0xB5)));

        var corrupt = StatusFrame(_ => { });
        corrupt[^1] ^= 0x55;
        Assert.Null(DehumidifierProtocol.TryParseStatus(corrupt));
    }

    [Fact]
    public void Discovery_response_is_parsed()
    {
        const long deviceId = 0x8900001DAD59;
        var payload = new byte[64];
        "SERIAL0123456789ABCDEF0123456789"u8.CopyTo(payload.AsSpan(8));
        var name = "midea_a1_1234"u8;
        payload[40] = (byte)name.Length;
        name.CopyTo(payload.AsSpan(41));

        var v2 = MideaPacket.Encode(deviceId, payload, DateTimeOffset.UtcNow);
        byte[] v3 = [0x83, 0x70, 0, 0, 0x20, 0x0F, 0, 0, .. v2, .. new byte[16]];

        var info = MideaDiscovery.Parse(v3);

        Assert.Equal(deviceId, info.DeviceId);
        Assert.Equal(3, info.ProtocolVersion);
        Assert.Equal("midea_a1_1234", info.Name);
        Assert.Equal("SERIAL0123456789ABCDEF0123456789", info.Serial);
    }

    [Fact]
    public void Udpid_is_16_bytes_of_hex_and_depends_on_byte_order()
    {
        var little = MideaCrypto.Udpid(0x8900001DAD59, littleEndian: true);
        var big = MideaCrypto.Udpid(0x8900001DAD59, littleEndian: false);

        Assert.Equal(32, little.Length);
        Assert.NotEqual(little, big);
        Assert.Equal(little, MideaCrypto.Udpid([0x59, 0xAD, 0x1D, 0x00, 0x00, 0x89]));
    }
}
