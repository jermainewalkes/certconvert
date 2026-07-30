using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Core;
using CertConvert.Services;

namespace CertConvert.ViewModels;

/// <summary>One inspected certificate signing request shown as a card. Every field
/// is what the request <em>asks for</em>, never asserted fact.</summary>
public partial class CsrItemViewModel(CsrInfo info, string pem) : ViewModelBase
{
    public CsrInfo Info { get; } = info;
    public string Pem { get; } = pem;

    public IReadOnlyList<FindingViewModel> Findings { get; } =
        HealthCheck.Analyse(info).Select(f => new FindingViewModel(f)).ToList();
    public bool HasFindings => Findings.Count > 0;
    public bool IsHealthy => Findings.Count == 0;

    public string Header => Info.DisplayName;

    public string SansText => string.Join(", ", Info.SubjectAlternativeNames);
    public string KeyUsageText => string.Join(", ", Info.KeyUsages);
    public string EkuText => string.Join(", ", Info.EnhancedKeyUsages);
    public bool HasSans => Info.SubjectAlternativeNames.Count > 0;
    public bool HasKeyUsage => Info.KeyUsages.Count > 0;
    public bool HasEku => Info.EnhancedKeyUsages.Count > 0;
    public bool RequestsCa => Info.HasBasicConstraints && Info.RequestsCa;

    public bool SignatureOk => Info.SignatureState == CsrSignatureState.Valid;
    public bool SignatureBad => Info.SignatureState == CsrSignatureState.Invalid;
    public string SignatureText => Info.SignatureState switch
    {
        CsrSignatureState.Valid => "Signature OK",
        CsrSignatureState.Invalid => "Bad signature",
        _ => "Not checked",
    };

    [RelayCommand]
    private Task CopyPem() => Dialogs.CopyTextAsync(Pem);

    [RelayCommand]
    private Task CopySha256() => Dialogs.CopyTextAsync(Info.Sha256Fingerprint);
}
