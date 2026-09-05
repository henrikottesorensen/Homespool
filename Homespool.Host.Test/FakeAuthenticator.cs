// The credential-building half is adapted from dotnet/aspnetcore at v10.0.11
// (src/Identity/test/Identity.Test/Passkeys/CredentialHelpers.cs and CredentialKeyPair.cs).
// Copyright (c) .NET Foundation, MIT licence.

using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Homespool.Host.Test;

/// <summary>
/// A WebAuthn authenticator in a class: one ES256 key pair, one credential id, and the two ceremonies
/// a browser would run on its behalf. It takes the options JSON the server produced and answers with
/// the credential JSON the server expects back, which is what lets the whole passkey path be driven
/// in-process with no browser and no hardware.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is honest by default and lies on request.</b> Every knob a test wants to turn - the origin
/// it claims, whether the person was verified, which challenge it answers - is a property, and the
/// defaults produce a credential the engine accepts. A test that wants a refusal sets one property
/// and says which step it expects to fail.
/// </para>
/// <para>
/// <b>Only ES256</b>, the algorithm every platform authenticator offers first. The engine's own test
/// suite covers the other algorithms; what this project's tests need is a credential that verifies,
/// not coverage of the key-decoding table.
/// </para>
/// </remarks>
internal sealed class FakeAuthenticator : IDisposable
{
    private const int CoseAlgorithmEs256 = -7;

    private static readonly byte[] Aaguid = new byte[16];

    // An empty CBOR map: the "none" attestation format carries no statement.
    private static readonly byte[] EmptyAttestationStatement = [0xA0];

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The credential id: what the server files the public key under.</summary>
    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    /// <summary>The origin the client data claims. The engine compares it with the request's <c>Origin</c> header.</summary>
    public string Origin { get; set; } = "https://homespool.test";

    /// <summary>
    /// The relying-party id to hash into the authenticator data. Null takes it from the options JSON,
    /// which is what a real authenticator does.
    /// </summary>
    public string? RelyingPartyId { get; set; }

    /// <summary>Whether the person was verified - the UV flag. Off, the engine refuses under a "required" policy.</summary>
    public bool UserVerified { get; set; } = true;

    /// <summary>Whether the credential syncs - the BE and BS flags together, as a synced platform credential reports them.</summary>
    public bool BackedUp { get; set; }

    /// <summary>The signature counter to report. Zero is what synced authenticators report for ever.</summary>
    public uint SignCount { get; set; }

    /// <summary>
    /// The challenge to answer instead of the one in the options, base64url. Null answers the real
    /// one; anything else is a stale or forged ceremony.
    /// </summary>
    public string? ChallengeOverride { get; set; }

    /// <summary>
    /// Answers a registration ceremony: the <c>PublicKeyCredential</c> JSON a browser would return
    /// from <c>navigator.credentials.create</c> given <paramref name="creationOptionsJson"/>.
    /// </summary>
    public string Attest(string creationOptionsJson)
    {
        using JsonDocument options = JsonDocument.Parse(creationOptionsJson);

        string challenge = ChallengeOverride ?? options.RootElement.GetProperty("challenge").GetString()!;
        string rpId = RelyingPartyId ?? options.RootElement.GetProperty("rp").GetProperty("id").GetString()!;

        byte[] attestedCredentialData = MakeAttestedCredentialData();
        byte[] authenticatorData = MakeAuthenticatorData(rpId, attestedCredentialData);
        byte[] attestationObject = MakeAttestationObject(authenticatorData);
        byte[] clientDataJson = MakeClientDataJson("webauthn.create", challenge);

        return $$"""
            {
              "id": "{{Base64Url.EncodeToString(CredentialId)}}",
              "rawId": "{{Base64Url.EncodeToString(CredentialId)}}",
              "response": {
                "attestationObject": "{{Base64Url.EncodeToString(attestationObject)}}",
                "clientDataJSON": "{{Base64Url.EncodeToString(clientDataJson)}}",
                "transports": ["internal"]
              },
              "type": "public-key",
              "clientExtensionResults": {},
              "authenticatorAttachment": "platform"
            }
            """;
    }

    /// <summary>
    /// Answers a sign-in ceremony: the <c>PublicKeyCredential</c> JSON a browser would return from
    /// <c>navigator.credentials.get</c> given <paramref name="requestOptionsJson"/>, signed by this
    /// authenticator's key and naming <paramref name="userHandle"/> as the account.
    /// </summary>
    /// <param name="requestOptionsJson">The options the server issued with its challenge.</param>
    /// <param name="userHandle">
    /// The user handle to return - the account id the server put in the credential at registration.
    /// Null omits it, which a server that did not name an account in the challenge must refuse.
    /// </param>
    public string Assert(string requestOptionsJson, string? userHandle)
    {
        using JsonDocument options = JsonDocument.Parse(requestOptionsJson);

        string challenge = ChallengeOverride ?? options.RootElement.GetProperty("challenge").GetString()!;
        string rpId = RelyingPartyId ?? options.RootElement.GetProperty("rpId").GetString()!;

        byte[] authenticatorData = MakeAuthenticatorData(rpId, attestedCredentialData: null);
        byte[] clientDataJson = MakeClientDataJson("webauthn.get", challenge);
        byte[] clientDataHash = SHA256.HashData(clientDataJson);
        byte[] signed = [.. authenticatorData, .. clientDataHash];
        byte[] signature = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        string userHandleJson = userHandle is null
            ? "null"
            : $"\"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(userHandle))}\"";

        return $$"""
            {
              "id": "{{Base64Url.EncodeToString(CredentialId)}}",
              "rawId": "{{Base64Url.EncodeToString(CredentialId)}}",
              "response": {
                "authenticatorData": "{{Base64Url.EncodeToString(authenticatorData)}}",
                "clientDataJSON": "{{Base64Url.EncodeToString(clientDataJson)}}",
                "signature": "{{Base64Url.EncodeToString(signature)}}",
                "userHandle": {{userHandleJson}}
              },
              "type": "public-key",
              "clientExtensionResults": {},
              "authenticatorAttachment": "platform"
            }
            """;
    }

    public void Dispose()
    {
        _key.Dispose();
    }

    private byte[] MakeClientDataJson(string type, string challenge)
    {
        return Encoding.UTF8.GetBytes($$"""{"type":"{{type}}","challenge":"{{challenge}}","origin":"{{Origin}}","crossOrigin":false}""");
    }

    private byte[] MakeAuthenticatorData(string rpId, byte[]? attestedCredentialData)
    {
        const byte UserPresent = 1 << 0;
        const byte UserVerifiedFlag = 1 << 2;
        const byte BackupEligible = 1 << 3;
        const byte BackedUpFlag = 1 << 4;
        const byte HasAttestedCredentialData = 1 << 6;

        byte flags = UserPresent;

        if (UserVerified)
        {
            flags |= UserVerifiedFlag;
        }

        if (BackedUp)
        {
            flags |= BackupEligible | BackedUpFlag;
        }

        if (attestedCredentialData is not null)
        {
            flags |= HasAttestedCredentialData;
        }

        byte[] rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));
        byte[] result = new byte[32 + 1 + 4 + (attestedCredentialData?.Length ?? 0)];

        rpIdHash.CopyTo(result, 0);
        result[32] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(33, 4), SignCount);
        attestedCredentialData?.CopyTo(result, 37);

        return result;
    }

    private byte[] MakeAttestedCredentialData()
    {
        byte[] publicKey = EncodeCosePublicKey();
        byte[] result = new byte[16 + 2 + CredentialId.Length + publicKey.Length];

        Aaguid.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(16, 2), (ushort)CredentialId.Length);
        CredentialId.CopyTo(result, 18);
        publicKey.CopyTo(result, 18 + CredentialId.Length);

        return result;
    }

    private static byte[] MakeAttestationObject(byte[] authenticatorData)
    {
        CborWriter writer = new(CborConformanceMode.Ctap2Canonical);

        writer.WriteStartMap(3);
        writer.WriteTextString("fmt");
        writer.WriteTextString("none");
        writer.WriteTextString("attStmt");
        writer.WriteEncodedValue(EmptyAttestationStatement);
        writer.WriteTextString("authData");
        writer.WriteByteString(authenticatorData);
        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>A COSE_Key for an EC2 P-256 public key: kty, alg, crv, x, y, in canonical order.</summary>
    private byte[] EncodeCosePublicKey()
    {
        const int KeyTypeEc2 = 2;
        const int CurveP256 = 1;

        ECParameters parameters = _key.ExportParameters(includePrivateParameters: false);

        CborWriter writer = new(CborConformanceMode.Ctap2Canonical);

        writer.WriteStartMap(5);
        writer.WriteInt32(1);
        writer.WriteInt32(KeyTypeEc2);
        writer.WriteInt32(3);
        writer.WriteInt32(CoseAlgorithmEs256);
        writer.WriteInt32(-1);
        writer.WriteInt32(CurveP256);
        writer.WriteInt32(-2);
        writer.WriteByteString(parameters.Q.X!);
        writer.WriteInt32(-3);
        writer.WriteByteString(parameters.Q.Y!);
        writer.WriteEndMap();

        return writer.Encode();
    }
}
