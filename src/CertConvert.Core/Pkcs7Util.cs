using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

/// <summary>
/// Reads and writes certs-only PKCS #7 bundles (.p7b/.p7c).
/// Writing builds the degenerate SignedData structure directly with AsnWriter so the
/// output is identical on every platform; reading uses the managed SignedCms decoder.
/// </summary>
public static class Pkcs7Util
{
    private const string SignedDataOid = "1.2.840.113549.1.7.2";
    private const string DataOid = "1.2.840.113549.1.7.1";

    public static List<X509Certificate2> ReadCertificates(byte[] der)
    {
        var cms = new SignedCms();
        try
        {
            cms.Decode(der);
        }
        catch (CryptographicException e)
        {
            throw new UnrecognisedContentException($"Not a valid PKCS #7 structure: {e.Message}");
        }
        if (cms.Certificates.Count == 0)
            throw new UnrecognisedContentException("The PKCS #7 file contains no certificates.");
        return [.. cms.Certificates];
    }

    /// <summary>Encodes a certs-only (degenerate) SignedData containing the given certificates.</summary>
    public static byte[] Write(IReadOnlyList<X509Certificate2> certs)
    {
        if (certs.Count == 0)
            throw new CertConvertException("No certificates to export.");

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) // ContentInfo
        {
            writer.WriteObjectIdentifier(SignedDataOid);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true))) // [0] EXPLICIT
            using (writer.PushSequence()) // SignedData
            {
                writer.WriteInteger(1);            // version
                using (writer.PushSetOf()) { }     // digestAlgorithms: empty
                using (writer.PushSequence())      // encapContentInfo: id-data, no content
                    writer.WriteObjectIdentifier(DataOid);
                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, true))) // certificates [0]
                {
                    foreach (var cert in certs)
                        writer.WriteEncodedValue(cert.RawData);
                }
                using (writer.PushSetOf()) { }     // signerInfos: empty
            }
        }
        return writer.Encode();
    }
}
