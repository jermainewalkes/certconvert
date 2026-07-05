using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Core;
using CertConvert.Services;

namespace CertConvert.ViewModels;

/// <summary>One inspected certificate shown as a card.</summary>
public partial class CertItemViewModel(CertificateInfo info, string pem) : ViewModelBase
{
    public CertificateInfo Info { get; } = info;
    public string Pem { get; } = pem;

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
