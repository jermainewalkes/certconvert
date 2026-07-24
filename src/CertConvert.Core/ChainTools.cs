using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

/// <summary>Orders and validates certificate chains (root → CA → device).</summary>
public static class ChainTools
{
    /// <summary>
    /// Orders an unordered set of certificates leaf-first. Matching prefers
    /// AKI → SKI links and falls back to issuer/subject name comparison.
    /// Throws when the set holds anything other than one single chain.
    /// </summary>
    public static List<X509Certificate2> Order(IReadOnlyList<X509Certificate2> input)
    {
        // De-duplicate by thumbprint, keeping first occurrence.
        var certs = input
            .GroupBy(c => c.Thumbprint)
            .Select(g => g.First())
            .ToList();

        if (certs.Count == 0)
            throw new CertConvertException("No certificates supplied.");
        if (certs.Count == 1)
            return certs;

        // Leaves are certificates that issued nothing else in the set.
        var leaves = certs
            .Where(c => !certs.Any(other => !ReferenceEquals(other, c) && IsIssuedBy(other, c)))
            .ToList();

        if (leaves.Count == 0)
            throw new CertConvertException(
                "The certificates form a loop — no leaf certificate could be identified.");
        if (leaves.Count > 1)
            throw new CertConvertException(
                "More than one chain is present. Leaf candidates: " +
                string.Join("; ", leaves.Select(c => c.Subject)) +
                ". Build one chain at a time.");

        var ordered = new List<X509Certificate2> { leaves[0] };
        var remaining = certs.Where(c => !ReferenceEquals(c, leaves[0])).ToList();
        while (remaining.Count > 0)
        {
            var current = ordered[^1];
            if (IsSelfSigned(current)) break;
            var issuer = remaining.FirstOrDefault(c => IsIssuedBy(current, c));
            if (issuer is null) break;
            ordered.Add(issuer);
            remaining.Remove(issuer);
        }

        if (remaining.Count > 0)
            throw new CertConvertException(
                "These certificates do not belong to the chain: " +
                string.Join("; ", remaining.Select(c => c.Subject)));
        return ordered;
    }

    /// <summary>True when <paramref name="cert"/> was (nominally) issued by <paramref name="candidate"/>.</summary>
    public static bool IsIssuedBy(X509Certificate2 cert, X509Certificate2 candidate)
    {
        if (!cert.IssuerName.RawData.AsSpan().SequenceEqual(candidate.SubjectName.RawData))
            return false;

        // When both key identifiers are present they must agree.
        var akiRaw = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.35");
        var skiRaw = candidate.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.14");
        if (akiRaw is not null && skiRaw is not null)
        {
            var aki = new X509AuthorityKeyIdentifierExtension(akiRaw.RawData, akiRaw.Critical);
            var ski = new X509SubjectKeyIdentifierExtension();
            ski.CopyFrom(skiRaw);
            if (aki.KeyIdentifier is { } kid && ski.SubjectKeyIdentifier is { } skiHex)
                return Convert.ToHexString(kid.Span).Equals(skiHex, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    public static bool IsSelfSigned(X509Certificate2 cert) =>
        cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData);

    /// <summary>
    /// Validates a chain offline. Self-signed certificates in the set act as trust anchors;
    /// optionally the operating system's root store is trusted too.
    /// </summary>
    public static ChainValidationResult Validate(
        IReadOnlyList<X509Certificate2> certs, bool trustSystemRoots = false)
    {
        var ordered = Order(certs);
        var leaf = ordered[0];
        var notes = new List<string>();
        var roots = ordered.Where(IsSelfSigned).ToList();

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreWrongUsage;

        if (trustSystemRoots)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.System;
            notes.Add("Trusting the operating system root store.");
            foreach (var extra in ordered.Skip(1))
                chain.ChainPolicy.ExtraStore.Add(extra);
        }
        else if (roots.Count > 0)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            foreach (var root in roots)
                chain.ChainPolicy.CustomTrustStore.Add(root);
            foreach (var extra in ordered.Skip(1).Where(c => !IsSelfSigned(c)))
                chain.ChainPolicy.ExtraStore.Add(extra);
            notes.Add("Anchored to a self-signed root supplied in the input — not a " +
                      "system-trusted CA. Enable “Trust System Roots” to check against the " +
                      "operating system’s trusted CAs instead.");
        }
        else
        {
            // No root supplied: verify as far as possible without trusting anything.
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.VerificationFlags |=
                X509VerificationFlags.AllowUnknownCertificateAuthority;
            foreach (var extra in ordered.Skip(1))
                chain.ChainPolicy.ExtraStore.Add(extra);
            notes.Add("Root certificate not supplied — the chain is verified up to the last " +
                      "intermediate but its trust anchor cannot be confirmed.");
        }

        bool valid = chain.Build(leaf);

        var elements = new List<ChainElementResult>();
        foreach (X509ChainElement element in chain.ChainElements)
        {
            var issues = element.ChainElementStatus
                .Where(s => s.Status != X509ChainStatusFlags.NoError)
                .Where(s => roots.Count == 0
                    ? s.Status is not (X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain)
                    : true)
                .Select(s => $"{s.Status}: {s.StatusInformation.Trim()}")
                .ToList();
            elements.Add(new ChainElementResult
            {
                Certificate = Inspector.Inspect(element.Certificate),
                Issues = issues,
            });
        }

        // With AllowUnknownCertificateAuthority, Build returns true even though the root
        // is unconfirmed; reflect remaining element issues in the verdict instead.
        bool anyIssues = elements.Any(e => !e.IsOk);
        bool isValid = valid && !anyIssues;
        if (isValid)
            notes.Add("Certificate revocation and extended key usage were not checked " +
                      "(validation is fully offline).");
        return new ChainValidationResult
        {
            IsValid = isValid,
            Elements = elements,
            Notes = notes,
        };
    }
}
