// ComputeTotp is transcribed from dotnet/aspnetcore at v10.0.12
// (src/Identity/Extensions.Core/src/Rfc6238AuthenticationService.cs), where it is internal. The
// modifier, which only the emailed-token providers pass, is left out.
//
// The MIT License (MIT)
//
// Copyright (c) .NET Foundation and Contributors
//
// All rights reserved.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;

namespace Homespool.Host.Accounts;

/// <summary>
/// The authenticator code for one time step: the framework's own computation, which it keeps internal.
/// </summary>
internal static class Rfc6238
{
    /// <summary>
    /// The six-digit code an authenticator app holding <paramref name="key"/> shows during
    /// <paramref name="timestepNumber"/>: HMAC-SHA1 over the step, dynamically truncated (RFC 4226).
    /// </summary>
    [SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms",
                     Justification = "RFC 6238 and every authenticator app use HMAC-SHA1. SHA-1's weakness is collisions, which HMAC does not rest on.")]
    public static int ComputeTotp(byte[] key, ulong timestepNumber)
    {
        // # of 0's = length of pin
        const int Mod = 1000000;

        // See https://tools.ietf.org/html/rfc4226
        Span<byte> timestepAsBytes = stackalloc byte[sizeof(long)];
        bool res = BitConverter.TryWriteBytes(timestepAsBytes, IPAddress.HostToNetworkOrder((long)timestepNumber));
        Debug.Assert(res, "A long fits in eight bytes.");

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        res = HMACSHA1.TryHashData(key, timestepAsBytes, hash, out int written);
        Debug.Assert(res, "The destination is sized for an HMAC-SHA1.");
        Debug.Assert(written == hash.Length, "An HMAC-SHA1 is always the same length.");

        // Generate DT string
        int offset = hash[hash.Length - 1] & 0xf;
        Debug.Assert(offset + 4 < hash.Length, "A four-bit offset leaves four bytes in a 20-byte hash.");
        int binaryCode = ((hash[offset] & 0x7f) << 24) |
                         ((hash[offset + 1] & 0xff) << 16) |
                         ((hash[offset + 2] & 0xff) << 8) |
                         (hash[offset + 3] & 0xff);

        return binaryCode % Mod;
    }
}
