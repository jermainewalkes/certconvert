using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Core;
using CertConvert.Services;

namespace CertConvert.ViewModels;

/// <summary>The mini-CA: sign a loaded CSR with your own CA, issuing an end-entity
/// certificate. The CSR's requested extensions are never trusted — the operator
/// confirms the subject (shown here) and chooses the SANs and usage.</summary>
public partial class SignViewModel : ViewModelBase
{
    // --- CA ---
    [ObservableProperty] private string _caPassword = "";
    [ObservableProperty] private string _caIdentity = "";
    [ObservableProperty] private string _caWarning = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSign))] private bool _hasCa;
    [ObservableProperty] private bool _allowNonCa;

    // --- CSR ---
    [ObservableProperty] private string _csrSubject = "";
    [ObservableProperty] private string _csrRequested = "";
    [ObservableProperty] private string _csrSignatureNote = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSign))] private bool _csrSignatureOk;
    [ObservableProperty] private bool _csrSignatureBad;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSign))] private bool _hasCsr;

    // --- Issuance ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServer))]
    private IssuedRole _role = IssuedRole.TlsServer;
    [ObservableProperty] private string _validityDays = "365";
    [ObservableProperty] private string _dnsNames = "";
    [ObservableProperty] private string _ipAddresses = "";
    [ObservableProperty] private string _emailNames = "";
    [ObservableProperty] private string _uris = "";
    [ObservableProperty] private bool _includeChain;

    [ObservableProperty] private string _status = "";

    /// <summary>Server certs use DNS/IP SANs; client certs use email/URI.</summary>
    public bool IsServer => Role == IssuedRole.TlsServer;
    /// <summary>Signing needs a CA, a loaded request, and a request whose own
    /// proof-of-possession signature verified — an unverified request is never signable
    /// from the UI (CaSigner re-checks it regardless).</summary>
    public bool CanSign => HasCa && HasCsr && CsrSignatureOk;

    private X509Certificate2? _caCert;
    private PrivateKeyEntry? _caKeyEntry;
    private List<X509Certificate2> _caChain = new();
    private byte[]? _csrDer;

    // --- load the CA (cert + key as two files, or a single PFX) ---
    [RelayCommand]
    private async Task LoadCa()
    {
        var files = await Dialogs.OpenFilesAsync("Open CA Certificate and Key (or a PFX)", true);
        if (files.Count == 0) return;

        var certs = new List<X509Certificate2>();
        PrivateKeyEntry? key = null;
        bool committed = false;
        try
        {
            string? pw = CaPassword.Length > 0 ? CaPassword : null;
            foreach (var f in files)
            {
                using var content = ContentLoader.LoadFile(f, pw);
                foreach (var c in content.Certificates)
                    certs.Add(X509CertificateLoader.LoadCertificate(c.RawData));   // owned copy
                if (key is null && content.PrivateKeys.Count > 0)
                {
                    key = content.PrivateKeys[0];
                    content.PrivateKeys.RemoveAt(0);   // take ownership so Dispose won't free it
                }
            }
            if (certs.Count == 0)
                throw new CertConvertException("No certificate found in the selected file(s).");
            if (key is null)
                throw new CertConvertException("No CA private key found — add the key file, or use a PFX.");

            // A CA bundle may hold more than one certificate (issuing CA + root); pick the
            // one the key actually belongs to rather than assuming it is listed first.
            int idx = certs.FindIndex(c => KeyTools.Matches(c, key.Key));
            if (idx < 0)
                throw new CertConvertException("The private key does not match any of the loaded certificates.");

            var caCert = certs[idx];
            var info = Inspector.Inspect(caCert);          // do the fallible work before committing
            var identity = $"{info.DisplayName} — {info.KeyAlgorithm}";
            bool isCa = info.IsCertificateAuthority
                && (info.KeyUsageFlags == X509KeyUsageFlags.None
                    || info.KeyUsageFlags.HasFlag(X509KeyUsageFlags.KeyCertSign));

            // Chain export order is CA-first, then the rest of the supplied certificates.
            var chain = new List<X509Certificate2>(certs.Count) { caCert };
            for (int i = 0; i < certs.Count; i++)
                if (i != idx) chain.Add(certs[i]);

            // ---- commit; nothing below this point may throw ----
            DisposeCa();
            _caCert = caCert;
            _caKeyEntry = key;
            _caChain = chain;
            committed = true;

            CaIdentity = identity;
            CaWarning = isCa ? "" :
                "This certificate is not marked as a CA — certificates it issues won't chain-validate. " +
                "Tick “Allow non-CA signer” to sign with it anyway.";
            HasCa = true;
            Status = $"Loaded CA: {info.DisplayName}.";
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
        finally
        {
            // If we never committed, nothing was stored — dispose the owned certificate
            // copies and the extracted key so no certificate or key material leaks.
            if (!committed)
            {
                foreach (var c in certs) c.Dispose();
                key?.Dispose();
            }
        }
    }

    // --- load the CSR to be signed ---
    [RelayCommand]
    private async Task LoadCsr()
    {
        var files = await Dialogs.OpenFilesAsync("Open Certificate Request", false);
        if (files.Count != 1) return;
        try
        {
            using var content = ContentLoader.LoadFile(files[0]);
            if (content.CertificateRequestPem is not { } pem)
                throw new CertConvertException("No certificate request found in the file.");
            var blocks = PemUtil.ParseAll(pem);
            if (blocks.Count == 0)
                throw new CertConvertException("No certificate request found in the file.");

            _csrDer = blocks[0].Der;
            var info = CsrInspector.Inspect(_csrDer);
            CsrSubject = info.Subject;
            var parts = new List<string>();
            if (info.SubjectAlternativeNames.Count > 0)
                parts.Add("SANs " + string.Join(", ", info.SubjectAlternativeNames));
            if (info.KeyUsages.Count > 0) parts.Add("usage " + string.Join(", ", info.KeyUsages));
            CsrRequested = parts.Count > 0 ? "Requests: " + string.Join("; ", parts) : "No extensions requested.";

            CsrSignatureBad = info.SignatureState == CsrSignatureState.Invalid;
            CsrSignatureOk = info.SignatureState == CsrSignatureState.Valid;
            CsrSignatureNote = CsrSignatureBad
                ? "The request's signature does not verify — it will not be signed."
                : "";

            // Pre-fill the SANs from the request for the operator to confirm/edit.
            DnsNames = Join(info.SubjectAlternativeNames, "DNS:");
            IpAddresses = Join(info.SubjectAlternativeNames, "IP:");
            HasCsr = true;
            Status = $"Loaded request for {info.DisplayName}.";
        }
        catch (Exception e)
        {
            _csrDer = null;
            HasCsr = false;
            Status = $"Error: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task SignAndSave()
    {
        if (_caCert is null || _caKeyEntry is null || _csrDer is null)
        {
            Status = "Load a CA and a certificate request first.";
            return;
        }
        try
        {
            var opts = BuildIssuanceOptions();
            using var issued = CaSigner.Sign(_caCert, _caKeyEntry.Key, _csrDer, opts);

            var certs = new List<X509Certificate2> { issued };
            if (IncludeChain) certs.AddRange(_caChain);
            byte[] bytes = Converter.Export(certs, CertOutputFormat.Pem);

            var outPath = await Dialogs.SaveFileAsync("Save Issued Certificate", "issued.pem");
            if (outPath is null)
            {
                Status = "Save cancelled — nothing was written.";
                return;
            }
            File.WriteAllBytes(outPath, bytes);
            var info = Inspector.Inspect(issued);
            Status = $"Issued {info.DisplayName}, valid until {info.NotAfter:yyyy-MM-dd} UTC — wrote {Path.GetFileName(outPath)}.";
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
    }

    /// <summary>Assemble the issuance options, passing only the SANs that belong to the
    /// selected role. The hidden fields for the other role are never emitted, so a value
    /// typed under one role (or pre-filled from the CSR) can't silently end up in a
    /// certificate issued under the other — a server cert never carries email/URI SANs,
    /// and a client cert never carries the DNS/IP SANs prefilled from the request.</summary>
    internal IssuanceOptions BuildIssuanceOptions()
    {
        if (!int.TryParse(ValidityDays, out int days))
            throw new CertConvertException("Validity days must be a number.");

        bool server = Role == IssuedRole.TlsServer;
        return new IssuanceOptions
        {
            ValidityDays = days,
            Role = Role,
            DnsNames = server ? Split(DnsNames) : [],
            IpAddresses = server ? Split(IpAddresses) : [],
            EmailNames = server ? [] : Split(EmailNames),
            Uris = server ? [] : Split(Uris),
            AllowNonCaIssuer = AllowNonCa,
        };
    }

    private void DisposeCa()
    {
        foreach (var c in _caChain) c.Dispose();
        _caChain = new();
        _caKeyEntry?.Dispose();
        _caKeyEntry = null;
        _caCert = null;
    }

    private static string Join(IEnumerable<string> sans, string prefix) =>
        string.Join(", ", sans.Where(s => s.StartsWith(prefix, StringComparison.Ordinal))
                              .Select(s => s[prefix.Length..]));

    private static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
