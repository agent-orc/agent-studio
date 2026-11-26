using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Security;

public sealed partial class AccessSecurityStore
{
    // This certifies possession of the device key, not manufacturer attestation.
    // The binding kind is reported by the client platform provider and confirmed by the Owner.
    private static bool FailedCertification(OperatorSignIn signIn)
        => signIn.CertificationAttempted && signIn.BindingKind == "memory";

    public static string DeviceCertificationStatement(string sessionThumbprint, string signInId, long issuedAt)
        => $"studio-op-device-certification-v1\n{sessionThumbprint}\n{signInId}\n{issuedAt.ToString(CultureInfo.InvariantCulture)}";

    private static OperatorSignIn CertifyDevice(OperatorSignIn signIn, OperatorDeviceCertification certification, DateTime now)
    {
        var result = signIn with { CertificationAttempted = true };
        if (certification.BindingKind is not ("tpm" or "secure-enclave")) return result;
        var seconds = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
        if (certification.IssuedAt < seconds - 60 || certification.IssuedAt > seconds + 60) return result;
        try
        {
            var key = ValidateDevicePublicKey(certification.PublicKey);
            if (certification.Signature is null || certification.Signature.Length != 86) return result;
            var signature = Convert.FromBase64String(certification.Signature.Replace('-', '+').Replace('_', '/') + "==");
            using var verifier = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = DecodeCoordinate(key.X), Y = DecodeCoordinate(key.Y) },
            });
            var statement = Encoding.UTF8.GetBytes(DeviceCertificationStatement(signIn.KeyThumbprint, signIn.Id, certification.IssuedAt));
            return verifier.VerifyData(statement, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? result with { BindingKind = certification.BindingKind, DeviceKeyThumbprint = key.Thumbprint(), DevicePublicKey = key }
                : result;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or SecurityOperationException or ArgumentException)
        {
            return result;
        }
    }
}
