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
    [ObservableProperty] private string _status = "Open a file or drag one anywhere onto this tab.";
    [ObservableProperty] private string _keysSummary = "";
    [ObservableProperty] private bool _hasCsr;

    public ObservableCollection<CertItemViewModel> Certificates { get; } = new();

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

    public void LoadPath(string path)
    {
        _currentPath = path;
        FileName = System.IO.Path.GetFileName(path);
        Certificates.Clear();
        Detected = "";
        KeysSummary = "";
        HasCsr = false;
        try
        {
            using var content = ContentLoader.LoadFile(
                path, Password.Length > 0 ? Password : null);
            Detected = content.SourceDescription;
            foreach (var cert in content.Certificates)
                Certificates.Add(new CertItemViewModel(
                    Inspector.Inspect(cert), cert.ExportCertificatePem() + "\n"));
            if (content.PrivateKeys.Count > 0)
                KeysSummary = "Private keys: " +
                    string.Join(", ", content.PrivateKeys.Select(k => k.Description));
            HasCsr = content.CertificateRequestPem is not null;
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
