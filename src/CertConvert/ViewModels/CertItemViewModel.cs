using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Core;
using CertConvert.Services;

namespace CertConvert.ViewModels;

/// <summary>One inspected certificate shown as a card.</summary>
public partial class CertItemViewModel(CertificateInfo info, string pem) : ViewModelBase
{
    public CertificateInfo Info { get; } = info;
    public string Pem { get; } = pem;

    public IReadOnlyList<FindingViewModel> Findings { get; } =
        HealthCheck.Analyse(info).Select(f => new FindingViewModel(f)).ToList();
    public bool HasFindings => Findings.Count > 0;
    public bool IsHealthy => Findings.Count == 0;

    [ObservableProperty] private string _hostToCheck = "";
    [ObservableProperty] private string _hostResult = "";
    [ObservableProperty] private bool _hostChecked;
    [ObservableProperty] private bool _hostCovered;

    // Pills only appear after a check has run, so they can't key off HostCovered alone.
    public bool ShowCoveredPill => HostChecked && HostCovered;
    public bool ShowNotCoveredPill => HostChecked && !HostCovered;

    partial void OnHostCheckedChanged(bool value) => NotifyPills();
    partial void OnHostCoveredChanged(bool value) => NotifyPills();
    private void NotifyPills()
    {
        OnPropertyChanged(nameof(ShowCoveredPill));
        OnPropertyChanged(nameof(ShowNotCoveredPill));
    }

    [RelayCommand]
    private void CheckHostname()
    {
        var host = HostToCheck.Trim();
        if (host.Length == 0)
        {
            HostChecked = false;
            HostResult = "";
            return;
        }
        // The live certificate from the load was disposed with its LoadedContent, so
        // re-materialise it from the PEM this card holds — never retain the original.
        try
        {
            using var cert = X509Certificate2.CreateFromPem(Pem);
            var result = HostnameCheck.Check(cert, host);
            HostResult = result.Explanation;
            HostCovered = result.IsCovered;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            HostResult = $"Could not read this certificate: {e.Message}";
            HostCovered = false;
        }
        HostChecked = true;
    }

    public string Header =>
        $"{Info.DisplayName}{(Info.IsCertificateAuthority ? "  (CA)" : "")}" +
        (Info.IsExpired ? "  — EXPIRED" : Info.IsNotYetValid ? "  — NOT YET VALID" : "");

    public string Validity =>
        $"{Info.NotBefore:yyyy-MM-dd} to {Info.NotAfter:yyyy-MM-dd} UTC";

    public string SansText => string.Join(", ", Info.SubjectAlternativeNames);
    public string KeyUsageText => string.Join(", ", Info.KeyUsages);
    public string EkuText => string.Join(", ", Info.EnhancedKeyUsages);
    public bool HasSans => Info.SubjectAlternativeNames.Count > 0;
    public bool HasKeyUsage => Info.KeyUsages.Count > 0;
    public bool HasEku => Info.EnhancedKeyUsages.Count > 0;

    [RelayCommand]
    private Task CopyPem() => Dialogs.CopyTextAsync(Pem);

    [RelayCommand]
    private Task CopySha256() => Dialogs.CopyTextAsync(Info.Sha256Fingerprint);
}
