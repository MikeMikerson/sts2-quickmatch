using System;
using System.Security.Cryptography;

namespace QuickMatch.Transport;

internal static class ChatClock
{
    /// <summary>Milliseconds from a steady clock.</summary>
    public static long Now() => Environment.TickCount64;

    /// <summary>
    /// A fresh session number for a new connection, never 0. It is part of every relay nonce
    /// (<see cref="RelayCrypto"/>), so it comes from the system's secure generator like the keys.
    /// </summary>
    public static uint NewSession() => (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
}
