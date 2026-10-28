using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MideaDehumidifier.Api.Midea;

/// <summary>V3 transport ("8370"): token/key handshake followed by AES-CBC + SHA-256 protected packets.</summary>
public static class MideaV3
{
    public const byte HandshakeRequest = 0x0;
    public const byte HandshakeResponse = 0x1;
    public const byte EncryptedResponse = 0x3;
    public const byte EncryptedRequest = 0x6;
    public const byte ErrorPacket = 0xF;

    private const int HeaderLength = 6;

    private static byte[] Header(int length, byte typeAndPad) =>
        [0x83, 0x70, (byte)(length >> 8), (byte)length, 0x20, typeAndPad];

    public static byte[] EncodeHandshake(ushort packetId, ReadOnlySpan<byte> token)
    {
        var packet = new byte[HeaderLength + 2 + token.Length];
        Header(token.Length, HandshakeRequest).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(HeaderLength), packetId);
        token.CopyTo(packet.AsSpan(HeaderLength + 2));
        return packet;
    }

    public static byte[] EncodeEncrypted(byte[] localKey, ushort packetId, ReadOnlySpan<byte> data, byte type = EncryptedRequest)
    {
        // The 2 byte packet ID counts towards the 16 byte alignment but not the size field.
        var remainder = (data.Length + 2) % 16;
        var pad = remainder == 0 ? 0 : 16 - remainder;
        var header = Header(data.Length + pad + 32, (byte)(pad << 4 | type));

        var payload = new byte[2 + data.Length + pad];
        BinaryPrimitives.WriteUInt16BigEndian(payload, packetId);
        data.CopyTo(payload.AsSpan(2));
        RandomNumberGenerator.Fill(payload.AsSpan(2 + data.Length));

        var hash = SHA256.HashData([.. header, .. payload]);
        return [.. header, .. MideaCrypto.EncryptCbc(localKey, payload), .. hash];
    }

    /// <summary>Total byte length of the packet whose first 6 bytes are <paramref name="header"/>.</summary>
    public static int PacketLength(ReadOnlySpan<byte> header)
    {
        if (header[0] != 0x83 || header[1] != 0x70)
            throw new MideaProtocolException($"Invalid start of packet: {Convert.ToHexString(header[..2])}.");
        return BinaryPrimitives.ReadUInt16BigEndian(header[2..]) + 8;
    }

    /// <summary>
    /// Decodes a received packet. For a handshake response returns the raw 64 byte handshake data;
    /// for an encrypted response returns the inner V2 packet.
    /// </summary>
    public static byte[] Decode(byte[]? localKey, byte[] packet)
    {
        if (packet.Length < HeaderLength + 2) throw new MideaProtocolException("V3 packet is too short.");
        PacketLength(packet);
        if (packet[4] != 0x20) throw new MideaProtocolException($"Invalid magic byte: 0x{packet[4]:X2}.");

        switch (packet[5] & 0xF)
        {
            case HandshakeResponse:
                return packet[(HeaderLength + 2)..];

            case EncryptedResponse:
                if (localKey is null) throw new MideaProtocolException("Encrypted packet received before authentication.");
                if (packet.Length < HeaderLength + 16 + 32) throw new MideaProtocolException("Encrypted packet is too short.");
                var header = packet[..HeaderLength];
                var decrypted = MideaCrypto.DecryptCbc(localKey, packet.AsSpan(HeaderLength, packet.Length - HeaderLength - 32));
                if (!SHA256.HashData([.. header, .. decrypted]).AsSpan().SequenceEqual(packet.AsSpan(packet.Length - 32)))
                    throw new MideaProtocolException("V3 packet SHA-256 digest does not match.");
                var pad = packet[5] >> 4;
                return decrypted[2..(decrypted.Length - pad)];

            case ErrorPacket:
                throw new MideaProtocolException("Device sent an error packet.");

            default:
                throw new MideaProtocolException($"Unexpected V3 packet type {packet[5] & 0xF}.");
        }
    }

    public static byte[] DeriveLocalKey(byte[] key, ReadOnlySpan<byte> handshakeData)
    {
        if (handshakeData.Length != 64) throw new MideaAuthenticationException("Invalid data length for key handshake.");

        byte[] decrypted;
        try { decrypted = MideaCrypto.DecryptCbc(key, handshakeData[..32]); }
        catch (CryptographicException e) { throw new MideaAuthenticationException("Invalid key.", e); }

        if (!SHA256.HashData(decrypted).AsSpan().SequenceEqual(handshakeData[32..]))
            throw new MideaAuthenticationException("Key handshake digest does not match; the token/key is not accepted by the device.");

        var localKey = new byte[32];
        for (var i = 0; i < 32; i++) localKey[i] = (byte)(decrypted[i] ^ key[i]);
        return localKey;
    }
}

/// <summary>One authenticated TCP session with a V3 device.</summary>
public sealed class MideaLanSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private byte[]? _localKey;
    private ushort _packetId;

    private MideaLanSession(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<MideaLanSession> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, timeout.Token);
            return new MideaLanSession(client);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Timed out connecting to {host}:{port}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task AuthenticateAsync(byte[] token, byte[] key, CancellationToken ct)
    {
        try
        {
            await WriteAsync(MideaV3.EncodeHandshake(NextPacketId(), token), ct);
            var response = MideaV3.Decode(null, await ReadPacketAsync(TimeSpan.FromSeconds(5), ct));
            _localKey = MideaV3.DeriveLocalKey(key, response);
        }
        catch (MideaProtocolException e) when (e is not MideaAuthenticationException)
        {
            // A device that dislikes the token typically just drops the connection or answers with an error packet.
            throw new MideaAuthenticationException($"Handshake failed: {e.Message}", e);
        }

        // The device needs a moment after the handshake before it accepts commands.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }

    /// <summary>Sends a command frame (the answer is read with <see cref="ReadFrameAsync"/>).</summary>
    public async Task SendFrameAsync(long deviceId, byte[] frame, TimeProvider time, CancellationToken ct)
    {
        if (_localKey is null) throw new MideaProtocolException("Session is not authenticated.");
        var v2 = MideaPacket.Encode(deviceId, frame, time.GetUtcNow());
        await WriteAsync(MideaV3.EncodeEncrypted(_localKey, NextPacketId(), v2), ct);
    }

    public async Task<byte[]> ReadFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        var v2 = MideaV3.Decode(_localKey, await ReadPacketAsync(timeout, ct));
        return MideaPacket.Decode(v2);
    }

    private ushort NextPacketId()
    {
        var id = _packetId;
        _packetId = (ushort)((_packetId + 1) & 0xFFF);
        return id;
    }

    private async Task WriteAsync(byte[] packet, CancellationToken ct) => await _stream.WriteAsync(packet, ct);

    private async Task<byte[]> ReadPacketAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var header = new byte[6];
            await _stream.ReadExactlyAsync(header, cts.Token);
            var packet = new byte[MideaV3.PacketLength(header)];
            header.CopyTo(packet, 0);
            await _stream.ReadExactlyAsync(packet.AsMemory(6), cts.Token);
            return packet;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("No response from the device.");
        }
        catch (EndOfStreamException)
        {
            throw new MideaProtocolException("The device closed the connection.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
