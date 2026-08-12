using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DropZone.LocalSend;

/// <summary>
/// LocalSend peers do not use a CA. Trust is pinned to the SHA-256 of the certificate,
/// which is exactly what the protocol calls the device "fingerprint".
/// </summary>
public static class SelfSignedCertificate
{
    public static X509Certificate2 Create(string commonName = "dropzone")
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        // Kestrel needs a cert with an exportable private key attached.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    public static string FingerprintOf(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
}
