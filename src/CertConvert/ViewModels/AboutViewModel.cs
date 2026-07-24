using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Services;

namespace CertConvert.ViewModels;

public partial class AboutViewModel : ViewModelBase
{
    public const string KoFiUrl = "https://ko-fi.com/jwalkes";

    private readonly AppSettings _settings;
    private readonly UpdateService _updates;

    /// <summary>Raised when a check discovers a newer release (drives the sidebar hint).</summary>
    public event Action? UpdateFound;

    public AboutViewModel(AppSettings settings, UpdateService updates)
    {
        _settings = settings;
        _updates = updates;
        _checkOnLaunch = settings.CheckForUpdatesOnLaunch;
    }

    public string Version { get; } = "Version " + UpdateService.CurrentVersion;

    /// <summary>Store builds hide the self-updater and the Ko-fi support card.</summary>
    public bool ShowUpdates => !AppInfo.IsStoreBuild;
    public bool ShowSupport => !AppInfo.IsStoreBuild;

    public string SecurityStatement { get; } = AppInfo.IsStoreBuild
        ? "CertConvert runs entirely on this machine and never connects to the network " +
          "on its own. Updates are delivered by the store. There is no telemetry. All " +
          "cryptography uses the operating system's .NET platform libraries — no " +
          "third-party crypto code is involved. Private keys loaded from PKCS #12 files " +
          "are handled in memory only and are not imported into the system key store."
        : "CertConvert runs entirely on this machine and never connects to the network " +
          "on its own. The only network action it can take is checking GitHub for a newer " +
          "version — which is off by default, and otherwise happens only when you choose " +
          "Check For Updates. There is no telemetry. All cryptography uses the operating " +
          "system's .NET platform libraries — no third-party crypto code is involved. " +
          "Private keys loaded from PKCS #12 files are handled in memory only and are not " +
          "imported into the system key store.";

    public string CliHint { get; } =
        "The same executable works from the command line: run it with --help to see " +
        "the inspect, convert, chain, key, gen and update commands.";

    public string AccessibilityStatement { get; } =
        "Every control is reachable by keyboard, labelled for screen readers and " +
        "status updates are announced as they happen. If anything is hard to use " +
        "with assistive technology, please raise an issue — accessibility problems " +
        "are treated as bugs.";

    // ---------- updates ----------

    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _releaseUrl = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty] private bool _canRestart;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _checkOnLaunch;

    private UpdateCheckResult? _lastCheck;
    private string? _restartPath;
    private CancellationTokenSource? _downloadCts;

    partial void OnCheckOnLaunchChanged(bool value)
    {
        _settings.CheckForUpdatesOnLaunch = value;
        _settings.Save();
    }

    [RelayCommand]
    public async Task CheckForUpdates()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        UpdateStatus = "Checking GitHub for updates…";
        try
        {
            var result = await _updates.CheckAsync();
            _lastCheck = result;
            ReleaseUrl = result.ReleaseUrl ?? "";
            switch (result.Status)
            {
                case Services.UpdateStatus.UpToDate:
                    UpdateAvailable = false;
                    CanInstall = false;
                    UpdateStatus = $"You're up to date — {result.CurrentVersion} is the latest version.";
                    break;
                case Services.UpdateStatus.UpdateAvailable:
                    UpdateAvailable = true;
                    CanInstall = result.AssetUrl is not null;
                    UpdateStatus =
                        $"Version {result.LatestVersion} is available (you have {result.CurrentVersion})." +
                        (result.Message is null ? "" : $" {result.Message}");
                    UpdateFound?.Invoke();
                    break;
                default:
                    UpdateAvailable = false;
                    CanInstall = false;
                    UpdateStatus = $"Update check failed: {result.Message}";
                    break;
            }
        }
        catch (Exception e)
        {
            // Belt-and-braces: CheckAsync catches its expected exceptions, but this
            // also runs fire-and-forget at launch — never let an unexpected type
            // become an unobserved Task fault.
            UpdateAvailable = false;
            CanInstall = false;
            UpdateStatus = $"Update check failed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstall()
    {
        if (_lastCheck?.AssetUrl is not { } url || _lastCheck.AssetName is not { } name)
            return;
        IsBusy = true;
        IsDownloading = true;
        CanCancel = true;
        DownloadProgress = 0;
        CanInstall = false;
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;
        try
        {
            UpdateStatus = $"Downloading {name}…";
            string zip = await _updates.DownloadAsync(
                url, name, new Progress<double>(p => DownloadProgress = p), ct);
            IsDownloading = false;
            CanCancel = false;

            UpdateStatus = "Verifying the download…";
            var verified = await _updates.VerifyChecksumAsync(zip, _lastCheck.ChecksumsUrl, name, ct);
            if (verified == ChecksumResult.Failed)
            {
                UpdateStatus = "Verification failed — the download does not match the " +
                               "published checksum. Update aborted.";
                CanInstall = true;
                return;
            }
            UpdateStatus = verified == ChecksumResult.NoChecksumFile
                ? "Applying update (this release publishes no checksum — integrity rests on TLS)…"
                : "Checksum verified. Applying update…";

            var result = await _updates.ApplyAsync(zip);
            UpdateStatus = result.Message;
            if (result.Ok && result.RestartPath is not null)
            {
                _restartPath = result.RestartPath;
                CanRestart = true;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = "Download cancelled.";
            CanInstall = true;
        }
        catch (Exception e)
        {
            UpdateStatus = $"Update failed: {e.Message}";
            CanInstall = true; // allow retrying
        }
        finally
        {
            IsBusy = false;
            IsDownloading = false;
            CanCancel = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private void RestartNow()
    {
        if (_restartPath is not null)
            _updates.RestartNow(_restartPath);
    }

    [RelayCommand]
    private Task ViewReleaseNotes() =>
        ReleaseUrl.Length > 0 ? Dialogs.OpenUrlAsync(ReleaseUrl) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenKoFi() => Dialogs.OpenUrlAsync(KoFiUrl);
}
