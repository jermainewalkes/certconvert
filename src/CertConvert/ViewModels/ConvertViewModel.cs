using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Core;
using CertConvert.Services;

namespace CertConvert.ViewModels;

public sealed record FormatOption(string Label, CertOutputFormat Value)
{
    public override string ToString() => Label;
}

public partial class ConvertViewModel : ViewModelBase
{
    public ObservableCollection<string> InputFiles { get; } = new();

    public IReadOnlyList<FormatOption> Formats { get; } =
    [
        new("PEM (.pem, .crt)", CertOutputFormat.Pem),
        new("DER (.cer, .der)", CertOutputFormat.Der),
        new("PKCS #7 (.p7b)", CertOutputFormat.Pkcs7Der),
        new("PKCS #7, PEM Armoured", CertOutputFormat.Pkcs7Pem),
        new("PKCS #12 (.pfx, .p12)", CertOutputFormat.Pkcs12),
    ];

    [ObservableProperty] private FormatOption _selectedFormat;
    [ObservableProperty] private string _selectedInput = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _keyFile = "";
    [ObservableProperty] private string _keyPassword = "";
    [ObservableProperty] private string _outPassword = "";
    [ObservableProperty] private bool _legacyCiphers;
    [ObservableProperty] private bool _isPfx;
    [ObservableProperty] private string _status = "";

    public ConvertViewModel()
    {
        _selectedFormat = Formats[0];
    }

    partial void OnSelectedFormatChanged(FormatOption value) =>
        IsPfx = value.Value == CertOutputFormat.Pkcs12;

    [RelayCommand]
    private async Task AddFiles()
    {
        foreach (var f in await Dialogs.OpenFilesAsync("Add Input Files"))
            if (!InputFiles.Contains(f))
                InputFiles.Add(f);
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedInput.Length > 0)
            InputFiles.Remove(SelectedInput);
    }

    public void AddDroppedFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!InputFiles.Contains(p))
                InputFiles.Add(p);
    }

    [RelayCommand]
    private void ClearFiles() => InputFiles.Clear();

    [RelayCommand]
    private async Task PickKeyFile()
    {
        var files = await Dialogs.OpenFilesAsync("Choose Private Key File", false);
        if (files.Count == 1)
            KeyFile = files[0];
    }

    [RelayCommand]
    private async Task ConvertAndSave()
    {
        if (InputFiles.Count == 0)
        {
            Status = "Add at least one input file first.";
            return;
        }

        var certs = new List<X509Certificate2>();
        var keys = new List<PrivateKeyEntry>();
        try
        {
            foreach (var path in InputFiles)
            {
                using var content = ContentLoader.LoadFile(
                    path, Password.Length > 0 ? Password : null);
                certs.AddRange(content.Certificates);
                keys.AddRange(content.PrivateKeys);
                content.Certificates.Clear();  // ownership moved to local lists
                content.PrivateKeys.Clear();
            }
            if (KeyFile.Length > 0)
            {
                using var keyContent = ContentLoader.LoadFile(
                    KeyFile, KeyPassword.Length > 0 ? KeyPassword : null);
                keys.AddRange(keyContent.PrivateKeys);
                keyContent.PrivateKeys.Clear();
            }

            var format = SelectedFormat.Value;
            System.Security.Cryptography.AsymmetricAlgorithm? pfxKey = null;
            if (format == CertOutputFormat.Pkcs12 && keys.Count > 0)
            {
                pfxKey = keys.FirstOrDefault(
                        k => certs.Any(c => KeyTools.Matches(c, k.Key)))?.Key
                    ?? throw new CertConvertException(
                        "The private key does not match any certificate being exported.");
            }

            byte[] bytes = Converter.Export(certs, format, new ExportOptions
            {
                Password = OutPassword,
                PrivateKey = pfxKey,
                Encryption = LegacyCiphers ? Pkcs12Encryption.Legacy : Pkcs12Encryption.Modern,
            });

            string suggested = Path.GetFileNameWithoutExtension(InputFiles[0]) + format switch
            {
                CertOutputFormat.Pem => ".pem",
                CertOutputFormat.Der => ".cer",
                CertOutputFormat.Pkcs7Der => ".p7b",
                CertOutputFormat.Pkcs7Pem => ".p7b",
                CertOutputFormat.Pkcs12 => ".pfx",
                _ => ".out",
            };
            var outPath = await Dialogs.SaveFileAsync("Save Converted File", suggested);
            if (outPath is null)
            {
                Status = "Save cancelled.";
                return;
            }
            File.WriteAllBytes(outPath, bytes);
            Status = $"Wrote {Path.GetFileName(outPath)} ({bytes.Length:N0} bytes)" +
                     (format == CertOutputFormat.Pkcs12 && pfxKey is null
                         ? " — note: no private key was included." : ".");
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
        finally
        {
            foreach (var c in certs) c.Dispose();
            foreach (var k in keys) k.Dispose();
        }
    }
}
