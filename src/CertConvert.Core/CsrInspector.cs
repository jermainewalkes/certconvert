using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

/// <summary>
/// Decodes PKCS #10 certificate signing requests for display — the
/// <c>openssl req -text</c> equivalent.
/// </summary>
/// <remarks>
/// <para>
/// A request is decoded twice. First with
/// <see cref="CertificateRequestLoadOptions.SkipSignatureValidation"/> plus
/// <see cref="CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions"/> to
/// read the requested fields — if <em>that</em> throws, the input simply is not a
/// well-formed request. Then a plain validating load reveals whether the
/// self-signature (proof of possession) actually verifies. This ordering matters:
/// a tampered signature, malformed DER and truncated input otherwise all surface as
/// the same <see cref="CryptographicException"/>.
/// </para>
/// <para>
/// <see cref="CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions"/> is
/// safe <em>here</em>: the "unsafe" concerns <em>trusting</em> requested extensions
/// when issuing a certificate, not reading them for display. Everything this class
/// surfaces is what the requester asked for, never asserted fact.
/// </para>
/// </remarks>
public static class CsrInspector
{
    /// <summary>A PKCS #10 request is a few kilobytes; anything past this is hostile
    /// or nonsense and is refused before it is decoded or retained. The content
    /// loader already caps file input, but requests also arrive as raw bytes.</summary>
    private const int MaxRequestBytes = 1 * 1024 * 1024;

    /// <summary>Decodes the first certificate-request block in some PEM text.</summary>
    public static CsrInfo InspectPem(string pem)
    {
        foreach (var block in PemUtil.ParseAll(pem))
            if (block.Label is "CERTIFICATE REQUEST" or "NEW CERTIFICATE REQUEST")
                return Inspect(block.Der);
        throw new UnrecognisedContentException("No certificate request found in the input.");
    }

    /// <summary>Decodes a DER-encoded certificate request.</summary>
    public static CsrInfo Inspect(byte[] der)
    {
        if (der.Length > MaxRequestBytes)
            throw new UnrecognisedContentException(
                $"This certificate request is {der.Length / 1024} KB — far larger than any real " +
                "PKCS #10 request, so it is refused.");

        CertificateRequest req;
        try
        {
            req = CertificateRequest.LoadSigningRequest(
                der, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.SkipSignatureValidation
                    | CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        }
        catch (Exception e) when (
            e is CryptographicException or AsnContentException or ArgumentException)
        {
            throw new UnrecognisedContentException(
                $"Not a valid PKCS #10 certificate request: {e.Message}", e);
        }

        var ext = Inspector.ParseExtensions(req.CertificateExtensions);
        var (keyDisplay, keyAlgo, keyBits) = Inspector.DescribeKey(req.PublicKey);
        string sigOid = ReadSignatureAlgorithmOid(der);

        return new CsrInfo
        {
            Subject = req.SubjectName.Name,
            KeyAlgorithm = keyDisplay,
            PublicKeyAlgorithm = keyAlgo,
            PublicKeyBits = keyBits,
            SignatureAlgorithm = sigOid.Length == 0 ? "unknown" : (new Oid(sigOid).FriendlyName ?? sigOid),
            SignatureAlgorithmOid = sigOid,
            SignatureState = VerifySignature(der),
            Sha256Fingerprint = Inspector.FormatHex(SHA256.HashData(der)),
            PublicKeySpki = req.PublicKey.ExportSubjectPublicKeyInfo(),
            HasBasicConstraints = ext.HasBasicConstraints,
            RequestsCa = ext.IsCa,
            PathLengthConstraint = ext.PathLen,
            SubjectAlternativeNames = ext.Sans,
            KeyUsages = ext.KeyUsages,
            EnhancedKeyUsages = ext.Ekus,
            KeyUsageFlags = ext.KeyUsageFlags,
            EnhancedKeyUsageOids = ext.EkuOids,
            UnreadableExtensions = ext.Unreadable,
        };
    }

    /// <summary>
    /// Detects a DER-encoded certificate request without throwing, re-encoding it to
    /// PEM on success. Used by the content loader as a last-resort format probe.
    /// </summary>
    public static bool TryLoadDer(byte[] der, out string? pem)
    {
        pem = null;
        if (der.Length is 0 or > MaxRequestBytes)  // same size policy as Inspect
            return false;
        try
        {
            _ = CertificateRequest.LoadSigningRequest(
                der, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.SkipSignatureValidation
                    | CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            pem = PemUtil.Encode("CERTIFICATE REQUEST", der);
            return true;
        }
        catch (Exception e) when (
            e is CryptographicException or AsnContentException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Renders one certificate request as readable multi-line text (CLI output).</summary>
    public static string ToText(CsrInfo info)
    {
        var w = new System.Text.StringBuilder();
        w.AppendLine($"Subject:        {info.Subject}");
        w.AppendLine($"Public key:     {info.KeyAlgorithm}");
        w.AppendLine($"Signature:      {info.SignatureAlgorithm}");
        string sig = info.SignatureState switch
        {
            CsrSignatureState.Valid => "verified",
            CsrSignatureState.Invalid => "** DOES NOT VERIFY **",
            _ => "not checked",
        };
        w.AppendLine($"Self-signature: {sig}");
        if (info.SubjectAlternativeNames.Count > 0)
            w.AppendLine($"Requests SANs:  {string.Join(", ", info.SubjectAlternativeNames)}");
        if (info.KeyUsages.Count > 0)
            w.AppendLine($"Requests usage: {string.Join(", ", info.KeyUsages)}");
        if (info.EnhancedKeyUsages.Count > 0)
            w.AppendLine($"Requests EKU:   {string.Join(", ", info.EnhancedKeyUsages)}");
        if (info.HasBasicConstraints)
        {
            string ca = info.RequestsCa ? "CA" : "end entity";
            if (info.PathLengthConstraint is { } n) ca += $", path length {n}";
            w.AppendLine($"Requests CA:    {ca}");
        }
        if (info.UnreadableExtensions.Count > 0)
            w.AppendLine($"Unreadable:     {string.Join(", ", info.UnreadableExtensions)} (could not parse)");
        w.AppendLine($"SHA-256:        {info.Sha256Fingerprint}");
        return w.ToString();
    }

    /// <summary>
    /// Determines whether the request's self-signature verifies. We reach this only
    /// after a successful skip-validation load, so the DER is structurally sound; a
    /// <see cref="CryptographicException"/> from the validating load therefore means
    /// the signature genuinely does not verify, not that the input is malformed.
    /// </summary>
    private static CsrSignatureState VerifySignature(byte[] der)
    {
        try
        {
            _ = CertificateRequest.LoadSigningRequest(der, HashAlgorithmName.SHA256);
            return CsrSignatureState.Valid;
        }
        catch (CryptographicException)
        {
            return CsrSignatureState.Invalid;
        }
    }

    /// <summary>
    /// Reads the real signature AlgorithmIdentifier OID from the request.
    /// <see cref="CertificateRequest"/> exposes no signature-algorithm property — its
    /// <c>HashAlgorithm</c> is the value the caller passed to the loader, not what
    /// signed the request — so read it directly.
    /// <c>CertificationRequest ::= SEQUENCE { info SEQUENCE, signatureAlgorithm
    /// AlgorithmIdentifier, signature BIT STRING }</c>.
    /// </summary>
    private static string ReadSignatureAlgorithmOid(byte[] der)
    {
        try
        {
            var outer = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
            outer.ReadEncodedValue();          // certificationRequestInfo (skip)
            var algId = outer.ReadSequence();  // signatureAlgorithm
            return algId.ReadObjectIdentifier();
        }
        catch (Exception e) when (e is AsnContentException or InvalidOperationException)
        {
            // Truncated or unexpectedly-shaped DER — the algorithm is simply unknown.
            return "";
        }
    }
}
