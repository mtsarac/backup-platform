using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace BackupPlatform.IntegrationTests;

public sealed class VirtualAuthenticator
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private uint _counter;

    public VirtualAuthenticator(byte[]? credentialId = null)
    {
        CredentialId = credentialId ?? Guid.NewGuid().ToByteArray();
    }

    public byte[] CredentialId { get; }
    public uint Counter => _counter;

    public AuthenticatorAttestationRawResponse CreateAttestation(
        string challengeBase64,
        string rpId = "localhost",
        string origin = "http://localhost")
    {
        var parameters = _key.ExportParameters(false);
        var rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));

        var coseWriter = new CborWriter();
        coseWriter.WriteStartMap(5);
        coseWriter.WriteInt32(1); // kty
        coseWriter.WriteInt32(2); // EC2
        coseWriter.WriteInt32(3); // alg
        coseWriter.WriteInt32(-7); // ES256
        coseWriter.WriteInt32(-1); // crv
        coseWriter.WriteInt32(1); // P-256
        coseWriter.WriteInt32(-2); // x
        coseWriter.WriteByteString(parameters.Q.X!);
        coseWriter.WriteInt32(-3); // y
        coseWriter.WriteByteString(parameters.Q.Y!);
        coseWriter.WriteEndMap();
        var coseKeyBytes = coseWriter.Encode();

        var authData = new List<byte>();
        authData.AddRange(rpIdHash);
        authData.Add(0x45); // UP (0x01) | UV (0x04) | AT (0x40)
        authData.AddRange(new byte[4]); // Counter 0
        authData.AddRange(Guid.NewGuid().ToByteArray()); // AAGUID 16 bytes
        authData.Add((byte)(CredentialId.Length >> 8));
        authData.Add((byte)(CredentialId.Length & 0xFF));
        authData.AddRange(CredentialId);
        authData.AddRange(coseKeyBytes);

        var attObjWriter = new CborWriter();
        attObjWriter.WriteStartMap(3);
        attObjWriter.WriteTextString("fmt");
        attObjWriter.WriteTextString("none");
        attObjWriter.WriteTextString("attStmt");
        attObjWriter.WriteStartMap(0);
        attObjWriter.WriteEndMap();
        attObjWriter.WriteTextString("authData");
        attObjWriter.WriteByteString(authData.ToArray());
        attObjWriter.WriteEndMap();
        var attestationObject = attObjWriter.Encode();

        var clientDataJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            type = "webauthn.create",
            challenge = challengeBase64,
            origin,
            crossOrigin = false
        }));

        return new AuthenticatorAttestationRawResponse
        {
            Id = Base64Url.EncodeToString(CredentialId),
            RawId = CredentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = attestationObject,
                ClientDataJson = clientDataJson
            }
        };
    }

    public AuthenticatorAssertionRawResponse CreateAssertion(
        string challengeBase64,
        string rpId = "localhost",
        string origin = "http://localhost",
        bool userVerified = true,
        uint? counterOverride = null)
    {
        var currentCounter = counterOverride ?? ++_counter;
        var rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));

        var authData = new List<byte>();
        authData.AddRange(rpIdHash);
        byte flags = 0x01; // User Present
        if (userVerified) flags |= 0x04; // User Verified
        authData.Add(flags);
        authData.AddRange(new byte[]
        {
            (byte)(currentCounter >> 24),
            (byte)(currentCounter >> 16),
            (byte)(currentCounter >> 8),
            (byte)currentCounter
        });

        var clientDataJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            type = "webauthn.get",
            challenge = challengeBase64,
            origin,
            crossOrigin = false
        }));

        var dataToSign = new List<byte>();
        dataToSign.AddRange(authData);
        dataToSign.AddRange(SHA256.HashData(clientDataJson));

        var signature = _key.SignData(dataToSign.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new AuthenticatorAssertionRawResponse
        {
            Id = Base64Url.EncodeToString(CredentialId),
            RawId = CredentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = authData.ToArray(),
                ClientDataJson = clientDataJson,
                Signature = signature
            }
        };
    }
}
