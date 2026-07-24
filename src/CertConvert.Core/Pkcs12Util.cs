using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

/// <summary>
/// PKCS #12 (.pfx/.p12) read/write using the fully managed Pkcs12Info/Pkcs12Builder APIs,
/// so private keys are handled in memory and never imported into an OS key store.
/// </summary>
public static class Pkcs12Util
{
    public static (List<X509Certificate2> Certs, List<PrivateKeyEntry> Keys) Read(
        byte[] data, string? password)
    {
        Pkcs12Info info;
        try
        {
            info = Pkcs12Info.Decode(data, out _, skipCopy: true);
        }
        catch (CryptographicException e)
        {
            throw new UnrecognisedContentException($"Not a valid PKCS #12 file: {e.Message}");
        }

        string effective = password ?? "";
        if (info.IntegrityMode == Pkcs12IntegrityMode.Password && !info.VerifyMac(effective))
        {
            if (password is null)
                throw new PasswordRequiredException("This PKCS #12 file");
            throw new InvalidPasswordException("this PKCS #12 file");
        }

        var certs = new List<X509Certificate2>();
        var keys = new List<PrivateKeyEntry>();

        try
        {
            foreach (Pkcs12SafeContents safe in info.AuthenticatedSafe)
            {
                if (safe.ConfidentialityMode == Pkcs12ConfidentialityMode.Password)
                {
                    try
                    {
                        safe.Decrypt(effective);
                    }
                    catch (CryptographicException)
                    {
                        if (password is null)
                            throw new PasswordRequiredException("This PKCS #12 file");
                        throw new InvalidPasswordException("this PKCS #12 file");
                    }
                }

                foreach (Pkcs12SafeBag bag in safe.GetBags())
                {
                    switch (bag)
                    {
                        case Pkcs12CertBag certBag when certBag.IsX509Certificate:
                            certs.Add(certBag.GetCertificate());
                            break;

                        case Pkcs12ShroudedKeyBag shrouded:
                        {
                            Pkcs8PrivateKeyInfo pk8;
                            try
                            {
                                pk8 = Pkcs8PrivateKeyInfo.DecryptAndDecode(
                                    effective, shrouded.EncryptedPkcs8PrivateKey, out _);
                            }
                            catch (CryptographicException)
                            {
                                if (password is null)
                                    throw new PasswordRequiredException("This PKCS #12 file");
                                throw new InvalidPasswordException("this PKCS #12 file");
                            }
                            keys.Add(KeyTools.ImportPkcs8Der(pk8.Encode()));
                            break;
                        }

                        case Pkcs12KeyBag keyBag:
                            keys.Add(KeyTools.ImportPkcs8Der(keyBag.Pkcs8PrivateKey));
                            break;
                    }
                }
            }
        }
        catch
        {
            // A later bag/safe failed after earlier ones parsed — release the
            // native handles already accumulated before propagating. Shield each
            // Dispose so one throwing doesn't mask the real error or strand the rest.
            foreach (var c in certs) { try { c.Dispose(); } catch { /* free the rest */ } }
            foreach (var k in keys) { try { k.Dispose(); } catch { /* free the rest */ } }
            throw;
        }

        if (certs.Count == 0 && keys.Count == 0)
            throw new UnrecognisedContentException(
                "The PKCS #12 file contains no certificates or private keys.");
        return (certs, keys);
    }

    /// <summary>
    /// Builds a PFX. Layout matches OpenSSL: certificates in a password-encrypted SafeContents,
    /// the shrouded key in an unencrypted one, matched to its certificate via localKeyId.
    /// </summary>
    public static byte[] Write(
        IReadOnlyList<X509Certificate2> certs,
        AsymmetricAlgorithm? privateKey,
        string password,
        Pkcs12Encryption encryption)
    {
        if (certs.Count == 0 && privateKey is null)
            throw new CertConvertException("Nothing to export into the PKCS #12 file.");

        PbeParameters pbe = encryption == Pkcs12Encryption.Modern
            ? new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000)
            : new PbeParameters(PbeEncryptionAlgorithm.TripleDes3KeyPkcs12, HashAlgorithmName.SHA1, 2_048);
        HashAlgorithmName macHash = encryption == Pkcs12Encryption.Modern
            ? HashAlgorithmName.SHA256
            : HashAlgorithmName.SHA1;

        Pkcs9LocalKeyId? localKeyId = null;
        var certContents = new Pkcs12SafeContents();
        foreach (var cert in certs)
        {
            Pkcs12SafeBag bag = certContents.AddCertificate(cert);
            if (privateKey is not null && localKeyId is null && KeyTools.Matches(cert, privateKey))
            {
                localKeyId = new Pkcs9LocalKeyId(cert.GetCertHash());
                bag.Attributes.Add(localKeyId);
            }
        }
        if (privateKey is not null && certs.Count > 0 && localKeyId is null)
            throw new CertConvertException(
                "The private key does not match any certificate being exported.");

        var builder = new Pkcs12Builder();
        if (certs.Count > 0)
            builder.AddSafeContentsEncrypted(certContents, password, pbe);

        if (privateKey is not null)
        {
            var keyContents = new Pkcs12SafeContents();
            Pkcs12SafeBag keyBag = privateKey switch
            {
                RSA rsa => keyContents.AddShroudedKey(rsa, password, pbe),
                ECDsa ec => keyContents.AddShroudedKey(ec, password, pbe),
                _ => throw new CertConvertException(
                    $"Unsupported key algorithm: {privateKey.GetType().Name}."),
            };
            if (localKeyId is not null)
                keyBag.Attributes.Add(localKeyId);
            builder.AddSafeContentsUnencrypted(keyContents);
        }

        builder.SealWithMac(password, macHash, iterationCount: 2_048);
        return builder.Encode();
    }
}
