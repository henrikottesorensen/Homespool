using System;
using System.Security.Cryptography;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Produces the random starting point for a connection's command-id counter. A fresh connection
/// restarts ids, and the printer can answer a command from a previous connection after
/// reconnecting - a counter always starting at 1 would make that stale ack collide with the new
/// connection's first command. A random start keeps ids unique within the only window that matters
/// (one in-flight command). The id is a collision-avoidance nonce the printer echoes back in
/// plaintext, the same role firmware's own <c>rand_u()</c> file_id plays.
/// </summary>
public static class CommandIdSeed
{
    public static uint Next()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);

        return BitConverter.ToUInt32(bytes);
    }
}
