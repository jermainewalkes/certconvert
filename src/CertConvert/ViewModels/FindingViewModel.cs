using CertConvert.Core;

namespace CertConvert.ViewModels;

/// <summary>One health-check finding, shown as a severity pill plus a plain-English
/// explanation. Three severity booleans drive which pill style shows, matching the
/// pill idiom used elsewhere without a value converter.</summary>
public sealed class FindingViewModel(Finding finding)
{
    public string Title => finding.Title;
    public string Explanation => finding.Explanation;

    public string SeverityLabel => finding.Severity switch
    {
        Severity.Critical => "Critical",
        Severity.Warning => "Warning",
        _ => "Note",
    };

    public bool IsCritical => finding.Severity == Severity.Critical;
    public bool IsWarning => finding.Severity == Severity.Warning;
    public bool IsNotice => finding.Severity == Severity.Notice;
}
