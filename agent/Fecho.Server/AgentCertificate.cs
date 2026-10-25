using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Fecho.Server;

public static class AgentCertificate
{
    /// <summary>
    /// Cria o certificado TLS próprio do agente (ECDSA P-256). Ele não precisa
    /// ser confiável para ninguém: o iPhone o aceita só pela impressão digital
    /// recebida no QR code.
    /// </summary>
    public static X509Certificate2 Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Fecho Agent", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("fecho.local");
        req.CertificateExtensions.Add(san.Build());
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }

    /// <summary>SHA-256 do certificado em DER, em hexadecimal minúsculo.</summary>
    public static string Fingerprint(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
}
