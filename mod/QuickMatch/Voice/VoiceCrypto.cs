using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace QuickMatch.Voice;

/// <summary>
/// Encryption of one player's voice packets carried by the relay, so the relay (and its host) can
/// neither listen nor change them. Every player makes its own random voice key per match, apart from
/// its lobby message key (QuickMatch.Transport.RelayCrypto), and announces it over Steam lobby chat;
/// the host passes each member's voice key on to the others, since voice goes from every member to
/// every other. A receiver opens a packet only with the key announced for the member the relay names
/// as its sender. The nonce is the packet's stream and first frame number: a speaker's frame numbers
/// only grow within a stream, and each voice session picks a new random stream.
///
///   relay payload = "QV" | version u8 | stream u32 | first frame u32 | encrypted frames part | tag (16 bytes)
///
/// The 11-byte header stays readable (it tells voice from lobby messages, which start "QC") and is
/// authenticated with the rest.
/// </summary>
internal sealed class VoiceCrypto : IDisposable
{
    public const int KeyBytes = 32;
    public const int HeaderSize = 11;
    public const int TagBytes = 16;
    public const byte FormatVersion = 2;
    /// <summary>Anything longer is not a voice packet and is dropped before decryption.</summary>
    public const int MaxPayloadBytes = HeaderSize + VoicePacket.MaxFramesPartBytes + TagBytes;

    private const byte Magic0 = (byte)'Q';
    private const byte Magic1 = (byte)'V';

    private static readonly byte[] NonceSuffix = "QMV2"u8.ToArray();

    private readonly AesGcm _cipher;

    public VoiceCrypto(byte[] key)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException($"a voice key has {KeyBytes} bytes", nameof(key));
        }
        _cipher = new AesGcm(key, TagBytes);
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary>True for a relay payload that claims to be voice; lobby messages start with "QC".</summary>
    public static bool IsVoice(ReadOnlySpan<byte> payload) => payload.Length >= 2 && payload[0] == Magic0 && payload[1] == Magic1;

    /// <summary>Encrypts a packet under this key.</summary>
    public byte[] Seal(VoicePacket packet)
    {
        byte[] frames = packet.WriteFrames();
        var payload = new byte[HeaderSize + frames.Length + TagBytes];
        payload[0] = Magic0;
        payload[1] = Magic1;
        payload[2] = FormatVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(3), packet.Stream);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(7), packet.FirstFrame);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(payload, nonce);
        _cipher.Encrypt(nonce, frames,
            payload.AsSpan(HeaderSize, frames.Length),
            payload.AsSpan(HeaderSize + frames.Length, TagBytes),
            payload.AsSpan(0, HeaderSize));
        return payload;
    }

    /// <summary>The packet sealed under this key, or null if the payload is malformed, too large, or was not.</summary>
    public VoicePacket? Open(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize + 2 + TagBytes || payload.Length > MaxPayloadBytes || !IsVoice(payload) || payload[2] != FormatVersion)
        {
            return null;
        }
        int framesLength = payload.Length - HeaderSize - TagBytes;
        var frames = new byte[framesLength];
        Span<byte> nonce = stackalloc byte[12];
        Nonce(payload, nonce);
        try
        {
            _cipher.Decrypt(nonce,
                payload.Slice(HeaderSize, framesLength),
                payload[(HeaderSize + framesLength)..],
                frames,
                payload[..HeaderSize]);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        uint stream = BinaryPrimitives.ReadUInt32LittleEndian(payload[3..]);
        uint firstFrame = BinaryPrimitives.ReadUInt32LittleEndian(payload[7..]);
        return VoicePacket.ReadFrames(stream, firstFrame, frames);
    }

    public void Dispose() => _cipher.Dispose();

    /// <summary>Stream and first frame from the header, then a fixed suffix.</summary>
    private static void Nonce(ReadOnlySpan<byte> header, Span<byte> nonce)
    {
        header.Slice(3, 8).CopyTo(nonce);
        NonceSuffix.CopyTo(nonce[8..]);
    }
}
