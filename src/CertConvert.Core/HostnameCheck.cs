using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

/// <summary>Whether, and how, a certificate covers a given host name.</summary>
public enum HostnameCoverage
{
    /// <summary>The host name is listed in the subject alternative names.</summary>
    Covered,
    /// <summary>Covered, but via a wildcard entry rather than an exact name.</summary>
    CoveredByWildcard,
    /// <summary>Present only in the common name — which modern browsers ignore.</summary>
    OnlyInCommonName,
    /// <summary>Not covered at all.</summary>
    NotCovered,
}

/// <summary>The answer to "does this certificate cover this host name?"</summary>
public sealed record HostnameResult(HostnameCoverage Coverage, string Explanation)
{
    public bool IsCovered => Coverage is HostnameCoverage.Covered or HostnameCoverage.CoveredByWildcard;
}

/// <summary>
/// Answers whether a certificate is valid for a host name, using the BCL's own
/// <see cref="X509Certificate2.MatchesHostname"/> (never a hand-rolled matcher).
/// It is called three ways and the differences are used to <em>explain</em> the
/// answer — in particular the case that trips people up: a name present only in the
/// common name, which browsers have ignored since 2017.
/// </summary>
public static class HostnameCheck
{
    /// <summary>
    /// RFC 6125 wildcard rules, stated plainly. Note deliberately NOT claimed: the
    /// BCL does not enforce public-suffix rules, so we do not tell users a wildcard
    /// "can never be a public suffix" — that would be a false security claim.
    /// </summary>
    public const string WildcardNote =
        "A wildcard covers exactly one label: *.example.com matches a.example.com " +
        "but not b.a.example.com, and not example.com itself. Wildcards apply to DNS " +
        "names only, never to an IP address.";

    public static HostnameResult Check(X509Certificate2 cert, string hostname)
    {
        hostname = hostname.Trim();
        if (hostname.Length == 0)
            return new(HostnameCoverage.NotCovered, "Enter a host name to check.");

        // MatchesHostname can throw on a malformed host name; a bad host simply
        // isn't covered, and must never crash the caller (a GUI command).
        bool viaSan = SafeMatch(cert, hostname, wildcards: true, commonName: false);
        bool exact = SafeMatch(cert, hostname, wildcards: false, commonName: false);

        if (viaSan && !exact)
            return new(HostnameCoverage.CoveredByWildcard,
                $"Yes — a wildcard in the certificate covers {hostname}. {WildcardNote}");
        if (viaSan)
            return new(HostnameCoverage.Covered,
                $"Yes — {hostname} is listed in the certificate's subject alternative names.");

        // MatchesHostname(commonName: true) only consults the CN when the certificate
        // has NO SAN at all, so it can't answer "is the host in the CN?" for a cert
        // that does have SANs. Compare the CN directly so the CN-only trap is always
        // explained rather than reported as a flat "no".
        bool viaCommonName = SafeMatch(cert, hostname, wildcards: true, commonName: true)
            || string.Equals(SafeCommonName(cert), hostname, StringComparison.OrdinalIgnoreCase);
        if (viaCommonName)
            return new(HostnameCoverage.OnlyInCommonName,
                $"No — {hostname} appears only in the common name, not the subject alternative " +
                "names. Browsers have ignored the common name since 2017, so it will still be " +
                "rejected. Add it to the SAN.");
        return new(HostnameCoverage.NotCovered,
            $"No — {hostname} is not covered by this certificate. Check the subject alternative names above.");
    }

    private static bool SafeMatch(X509Certificate2 cert, string hostname, bool wildcards, bool commonName)
    {
        try
        {
            return cert.MatchesHostname(hostname, wildcards, commonName);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static string SafeCommonName(X509Certificate2 cert)
    {
        try
        {
            return Inspector.CommonNameOf(cert.SubjectName.Name);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }
}
