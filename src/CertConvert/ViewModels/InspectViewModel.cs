using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Core;
using CertConvert.Services;

namespace CertConvert.ViewModels;

public partial class InspectViewModel : ViewModelBase
{
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _detected = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _keysSummary = "";
    // The drop zone shows until something is loaded. Certificate-request-only and
    // key-only files load no certificates, so this can't key off Certificates.Count.
    [ObservableProperty] private bool _isEmpty = true;

    public ObservableCollection<CertItemViewModel> Certificates { get; } = new();
    public ObservableCollection<CsrItemViewModel> Csrs { get; } = new();

    private string? _currentPath;

    [RelayCommand]
    private async Task OpenFile()
    {
        var files = await Dialogs.OpenFilesAsync("Open Certificate, Key Or Bundle", false);
        if (files.Count == 1)
            LoadPath(files[0]);
    }

    [RelayCommand]
    private void Reload()
    {
        if (_currentPath is not null)
            LoadPath(_currentPath);
    }

    [RelayCommand]
    private void Clear()
    {
        _currentPath = null;
        FileName = "";
        Password = "";
        Detected = "";
        KeysSummary = "";
        IsEmpty = true;
        Status = "";
        Certificates.Clear();
        Csrs.Clear();
    }

    public void LoadPath(string path)
    {
        _currentPath = path;
        FileName = System.IO.Path.GetFileName(path);
        Certificates.Clear();
        Csrs.Clear();
        Detected = "";
        KeysSummary = "";
        try
        {
            using var content = ContentLoader.LoadFile(
                path, Password.Length > 0 ? Password : null);
            Detected = content.SourceDescription;
            foreach (var cert in content.Certificates)
                Certificates.Add(new CertItemViewModel(
                    Inspector.Inspect(cert), cert.ExportCertificatePem() + "\n"));
            foreach (var csrPem in content.CertificateRequests)
                Csrs.Add(new CsrItemViewModel(CsrInspector.InspectPem(csrPem), csrPem));
            if (content.PrivateKeys.Count > 0)
                KeysSummary = "Private keys: " +
                    string.Join(", ", content.PrivateKeys.Select(k => k.Description));
            IsEmpty = Certificates.Count == 0 && Csrs.Count == 0 && KeysSummary.Length == 0;
            Status = $"Loaded {FileName} — {Detected}.";
        }
        catch (PasswordRequiredException e)
        {
            Status = $"{e.Message} Enter it above, then choose Unlock.";
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
    }
}
