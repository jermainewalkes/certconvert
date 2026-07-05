using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Core;
using CertConvert.Services;

namespace CertConvert.ViewModels;

public sealed record KeyFormatOption(string Label, KeyOutputFormat Value)
{
    public override string ToString() => Label;
}

public partial class KeysViewModel : ViewModelBase
{
    public IReadOnlyList<KeyFormatOption> Formats { get; } =
    [
        new("PKCS #8 PEM (Unencrypted)", KeyOutputFormat.Pkcs8Pem),
        new("PKCS #8 PEM (Encrypted, AES-256)", KeyOutputFormat.Pkcs8EncryptedPem),
        new("PKCS #1 PEM (RSA Only)", KeyOutputFormat.Pkcs1Pem),
        new("SEC 1 PEM (EC Only)", KeyOutputFormat.Sec1Pem),
        new("PKCS #8 DER (Binary)", KeyOutputFormat.Pkcs8Der),
    ];

    [ObservableProperty] private KeyFormatOption _selectedFormat;
    [ObservableProperty] private string _keyFile = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _keyDescription = "";
    [ObservableProperty] private string _outPassword = "";
    [ObservableProperty] private bool _needsOutPassword;
    [ObservableProperty] private string _certFile = "";
    [ObservableProperty] private string _matchResult = "";
    [ObservableProperty] private string _status = "Load a private key to convert it or check it against a certificate.";

    private PrivateKeyEntry? _loaded;

    public KeysViewModel()
    {
        _selectedFormat = Formats[0];
    }

    partial void OnSelectedFormatChanged(KeyFormatOption value) =>
        NeedsOutPassword = value.Value == KeyOutputFormat.Pkcs8EncryptedPem;

    [RelayCommand]
    private async Task OpenKey()
    {
        var files = await Dialogs.OpenFilesAsync("Open Private Key", false);
        if (files.Count == 1)
        {
            KeyFile = files[0];
            LoadKey();
        }
    }

    [RelayCommand]
    private void LoadKey()
    {
        if (KeyFile.Length == 0)
        {
            Status = "Choose a key file first.";
            return;
        }
        try
        {
            var content = ContentLoader.LoadFile(
                KeyFile, Password.Length > 0 ? Password : null);
            if (content.PrivateKeys.Count == 0)
            {
                content.Dispose();
                throw new CertConvertException("No private key found in the file.");
            }
            _loaded?.Dispose();
            _loaded = content.PrivateKeys[0];
            content.PrivateKeys.Clear();
            content.Dispose();
            KeyDescription = _loaded.Description;
            Status = $"Loaded {Path.GetFileName(KeyFile)} — {KeyDescription}.";
        }
        catch (Exception e)
        {
            KeyDescription = "";
            Status = $"Error: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveConverted()
    {
        if (_loaded is null)
        {
            Status = "Load a key first.";
            return;
        }
        try
        {
            var format = SelectedFormat.Value;
            byte[] bytes = format == KeyOutputFormat.Pkcs8Der
                ? KeyTools.ExportDer(_loaded.Key)
                : Encoding.ASCII.GetBytes(KeyTools.ExportPem(
                    _loaded.Key, format,
                    NeedsOutPassword ? OutPassword : null));

            string suggested = Path.GetFileNameWithoutExtension(KeyFile) +
                (format == KeyOutputFormat.Pkcs8Der ? ".der" : ".key");
            var outPath = await Dialogs.SaveFileAsync("Save Converted Key", suggested);
            if (outPath is null)
            {
                Status = "Save cancelled.";
                return;
            }
            File.WriteAllBytes(outPath, bytes);
            Status = $"Wrote {Path.GetFileName(outPath)}.";
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task PickCert()
    {
        var files = await Dialogs.OpenFilesAsync("Choose Certificate", false);
        if (files.Count == 1)
            CertFile = files[0];
    }

    [RelayCommand]
    private void CheckMatch()
    {
        MatchResult = "";
        if (_loaded is null)
        {
            Status = "Load a key first.";
            return;
        }
        if (CertFile.Length == 0)
        {
            Status = "Choose a certificate to check against.";
            return;
        }
        try
        {
            using var certContent = ContentLoader.LoadFile(CertFile);
            if (certContent.Certificates.Count == 0)
                throw new CertConvertException("No certificate found in the file.");
            bool match = KeyTools.Matches(certContent.Certificates[0], _loaded.Key);
            MatchResult = match
                ? "MATCH — the key belongs to this certificate."
                : "NO MATCH — the key does not belong to this certificate.";
            Status = MatchResult;
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
    }
}
