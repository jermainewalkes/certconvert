using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Services;

namespace CertConvert.ViewModels;

public partial class AboutViewModel : ViewModelBase
{
    public const string KoFiUrl = "https://ko-fi.com/jwalkes";

    public string Version { get; } = "Version " +
        (Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown");

    public string SecurityStatement { get; } =
        "CertConvert runs entirely on this machine. It never connects to the network, " +
        "never sends data anywhere and never stores your keys or certificates outside " +
        "the files you explicitly save. All cryptography uses the operating system's " +
        ".NET platform libraries — no third-party crypto code is involved. Private keys " +
        "loaded from PKCS #12 files are handled in memory only and are not imported " +
        "into the system key store.";

    public string CliHint { get; } =
        "The same executable works from the command line: run it with --help to see " +
        "the inspect, convert, chain, key and gen commands.";

    public string AccessibilityStatement { get; } =
        "Every control is reachable by keyboard, labelled for screen readers and " +
        "status updates are announced as they happen. If anything is hard to use " +
        "with assistive technology, please raise an issue — accessibility problems " +
        "are treated as bugs.";

    [RelayCommand]
    private Task OpenKoFi() => Dialogs.OpenUrlAsync(KoFiUrl);
}
