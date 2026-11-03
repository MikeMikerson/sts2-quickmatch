using System;
using System.Security.Cryptography;
using System.Text;

namespace QuickMatch.Transport;

/// <summary>
/// Encryption of one player's lobby messages carried by the relay, so the relay (and its host) can
/// neither read nor change them, and no other player can pass messages off as that player's. Every
/// player makes its own random key per match and announces it over Steam lobby chat, which Steam
/// delivers with the real sender (see <see cref="RelayRoute"/>); a receiver opens a relay copy only
/// with the key announced by the member the relay names as its sender. The nonce is the lobby
/// message's session and sequence number: a resent lobby message is byte-identical and encrypts
/// identically, and no two different messages of one sender share a nonce. The lobby message header
/// stays readable and is authenticated with the rest.
///
///   relay payload = header (11 bytes, as in the lobby message) | encrypted frames | tag (16 bytes)
/// </summary>
internal sealed class RelayCrypto : IDisposable
{
    public const int KeyBytes = 32;
    public const int TagBytes = 16;

    private static readonly byte[] RoomLabel = Encoding.ASCII.GetBytes("QuickMatch relay room v2");
    private static readonly byte[] NonceSuffix = "QMR2"u8.ToArray();

    private readonly AesGcm _cipher;

    public RelayCrypto(byte[] key)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException($"a relay key has {KeyBytes} bytes", nameof(key));
        }
        _cipher = new AesGcm(key, TagBytes);
    }

    /// <summary>False where the runtime has no AES-GCM; such a player never offers or takes the relay.</summary>
    public static bool IsSupported => AesGcm.IsSupported;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary>The relay room of a match: derived from the host's room key, so only players it was given to can find the room.</summary>
    public static byte[] RoomToken(byte[] roomKey) => HMACSHA256.HashData(roomKey, RoomLabel);

    /// <summary>Encrypts a lobby message (header and frames) under this key.</summary>
    public byte[] Seal(byte[] message, int length)
    {
        if (length < ChatFraming.HeaderSize)
        {
            throw new ArgumentException("not a lobby message", nameof(message));
        }
        var sealedMessage = new byte[length + TagBytes];
        message.AsSpan(0, ChatFraming.HeaderSize).CopyTo(sealedMessage);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(message, nonce);
        _cipher.Encrypt(nonce,
            message.AsSpan(ChatFraming.HeaderSize, length - ChatFraming.HeaderSize),
            sealedMessage.AsSpan(ChatFraming.HeaderSize, length - ChatFraming.HeaderSize),
            sealedMessage.AsSpan(length, TagBytes),
            message.AsSpan(0, ChatFraming.HeaderSize));
        return sealedMessage;
    }

    /// <summary>The lobby message sealed under this key, or null if it was not.</summary>
    public byte[]? Open(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ChatFraming.HeaderSize + TagBytes || !ChatFraming.TryReadHeader(payload, out _, out _))
        {
            return null;
        }
        int length = payload.Length - TagBytes;
        var message = new byte[length];
        payload[..ChatFraming.HeaderSize].CopyTo(message);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(payload, nonce);
        try
        {
            _cipher.Decrypt(nonce,
                payload[ChatFraming.HeaderSize..length],
                payload[length..],
                message.AsSpan(ChatFraming.HeaderSize),
                payload[..ChatFraming.HeaderSize]);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        return message;
    }

    public void Dispose() => _cipher.Dispose();

    /// <summary>Session and sequence number from the lobby message header, then a fixed suffix.</summary>
    private static void Nonce(ReadOnlySpan<byte> header, Span<byte> nonce)
    {
        header.Slice(3, 8).CopyTo(nonce);
        NonceSuffix.CopyTo(nonce[8..]);
    }
}
