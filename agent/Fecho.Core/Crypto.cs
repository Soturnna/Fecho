using System.Security.Cryptography;

namespace Fecho.Core;

public static class Crypto
{
    /// <summary>
    /// Confere se a chave pública veio no formato X9.63 que o iOS exporta
    /// (0x04 + X + Y, 65 bytes) e se é um ponto válido da curva P-256.
    /// </summary>
    public static bool IsValidP256PublicKey(byte[]? publicKey)
    {
        if (publicKey is null || publicKey.Length != 65 || publicKey[0] != 0x04) return false;
        try
        {
            using var ecdsa = ImportP256(publicKey);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Verifica uma assinatura ECDSA P-256/SHA-256 em formato DER.</summary>
    public static bool VerifyP256(byte[] publicKey, byte[] data, byte[] derSignature)
    {
        if (publicKey.Length != 65 || publicKey[0] != 0x04) return false;
        try
        {
            using var ecdsa = ImportP256(publicKey);
            return ecdsa.VerifyData(data, derSignature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static byte[] HmacSha256(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    /// <summary>Compara sem vazar, pelo tempo de resposta, onde os bytes diferem.</summary>
    public static bool FixedTimeEquals(byte[] a, byte[] b) =>
        CryptographicOperations.FixedTimeEquals(a, b);

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromBase64Url(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 512) return null;
        var s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: return null;
        }
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    public static byte[]? FromBase64(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || text.Length > (maxBytes / 3 + 1) * 4) return null;
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }

    private static ECDsa ImportP256(byte[] x963) => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = x963[1..33], Y = x963[33..65] },
    });
}
