using System;
using System.Security.Cryptography;

using Microsoft.AspNetCore.WebUtilities;

namespace Homespool.Host.Notifications.WebPush;

/// <summary>
/// Checks the two keys a browser subscribes with before they are stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>RFC 8291 requires the public key be checked to lie on the curve</b>, since a point off it can be
/// used to learn the private key it is combined with. Both platforms this runs on refuse such a point
/// when it is imported - Apple's crypto and OpenSSL alike - and the import is what this asks for, so
/// the answer does not depend on the library that later does the encryption.
/// </para>
/// <para>
/// <b>Checked at the door rather than at delivery</b>, so a malformed subscription is a refused form
/// rather than a row that fails on every notification.
/// </para>
/// </remarks>
public static class WebPushSubscriptionKeys
{
    /// <summary>An uncompressed P-256 point: <c>0x04</c>, then 32 bytes each of X and Y.</summary>
    public const int PublicKeyLength = 65;

    /// <summary>The authentication secret's length, fixed by RFC 8291.</summary>
    public const int AuthLength = 16;

    /// <summary>
    /// Whether <paramref name="p256dh"/> is a base64url P-256 public key on the curve and
    /// <paramref name="auth"/> a base64url 16-byte secret.
    /// </summary>
    public static bool AreValid(string? p256dh, string? auth)
    {
        return IsValidPublicKey(p256dh) && IsValidAuth(auth);
    }

    private static bool IsValidAuth(string? auth)
    {
        return TryDecode(auth, out byte[] bytes) && bytes.Length == AuthLength;
    }

    private static bool IsValidPublicKey(string? p256dh)
    {
        if (!TryDecode(p256dh, out byte[] point) || point.Length != PublicKeyLength || point[0] != 0x04)
        {
            return false;
        }

        try
        {
            using ECDiffieHellman key = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[1..33], Y = point[33..] },
            });

            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryDecode(string? text, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrWhiteSpace(text) || text.Length > Model.Entities.WebPushDestination.P256dhMaxLength)
        {
            return false;
        }

        try
        {
            bytes = WebEncoders.Base64UrlDecode(text);

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
