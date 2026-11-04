using System.Security.Cryptography;

namespace RouteGo.Identity.Api.Domain;

/// <summary>
/// What makes a stored secret usable as a signing key: an RSA 2048-bit key whose private half is
/// present. Introducing a key and the readiness check apply the same test, so a key that passed
/// introduction can never be the reason signing fails later.
/// </summary>
public static class SigningKeyMaterial
{
    /// <summary>The platform's signing key size.</summary>
    public const int KeySizeBits = 2048;

    /// <summary>Reads a stored private key and derives its public half.</summary>
    /// <param name="privateKeyPem">The secret as stored, or null when there is none.</param>
    /// <returns>The public half, or the reason the key cannot sign.</returns>
    public static SigningKeyReading Read(string? privateKeyPem)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            return new SigningKeyReading(null, "the secret store holds no private key under this key ID");
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(privateKeyPem);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is CryptographicException)
        {
            return new SigningKeyReading(null, "the stored secret is not an RSA private key in PEM form");
        }

        if (rsa.KeySize != KeySizeBits)
        {
            return new SigningKeyReading(null, $"the key is {rsa.KeySize}-bit; signing keys are RSA {KeySizeBits}-bit");
        }

        // A PEM holding only a public key imports cleanly too, and would fail on the first signature.
        if (!HasPrivateHalf(rsa))
        {
            return new SigningKeyReading(null, "the stored secret holds only a public key; the private half is needed to sign");
        }

        return new SigningKeyReading(rsa.ExportSubjectPublicKeyInfoPem(), null);
    }

    private static bool HasPrivateHalf(RSA rsa)
    {
        try
        {
            rsa.ExportParameters(true);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
