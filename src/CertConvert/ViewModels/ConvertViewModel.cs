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

public sealed record FormatOption(string Label, CertOutputFormat Value, string Extension)
{
    public override string ToString() => Label;
}

public partial class ConvertViewModel : ViewModelBase
{
    public ObservableCollection<string> InputFiles { get; } = new();

    public IReadOnlyList<FormatOption> Formats { get; } =
    [
        new("PEM Certificate (.pem)", CertOutputFormat.Pem, "pem"),
        new("PEM Certificate (.crt)", CertOutputFormat.Pem, "crt"),
        new("DER Certificate (.cer)", CertOutputFormat.Der, "cer"),
        new("DER Certificate (.der)", CertOutputFormat.Der, "der"),
        new("PKCS #7 Bundle (.p7b)", CertOutputFormat.Pkcs7Der, "p7b"),
        new("PKCS #7 Bundle, PEM (.pem)", CertOutputFormat.Pkcs7Pem, "pem"),
        new("PKCS #12 (.pfx)", CertOutputFormat.Pkcs12, "pfx"),
        new("PKCS #12 (.p12)", CertOutputFormat.Pkcs12, "p12"),
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
            Converter.RemoveDuplicates(certs);

            var format = SelectedFormat.Value;
            if (certs.Count == 0 && keys.Count > 0)
                throw new CertConvertException(format == CertOutputFormat.Pkcs12
                    ? "Only private keys were supplied. Add the certificate the key belongs " +
                      "to — a PFX pairs certificates with their key."
                    : "Only private keys were supplied — the Keys page converts key formats.");

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

            string suggested =
                $"{Path.GetFileNameWithoutExtension(InputFiles[0])}.{SelectedFormat.Extension}";
            var outPath = await Dialogs.SaveFileAsync("Save Converted File", suggested);
            if (outPath is null)
            {
                Status = "Save cancelled.";
                return;
            }
            File.WriteAllBytes(outPath, bytes);
            var notes = new List<string>();
            if (format == CertOutputFormat.Pkcs12)
            {
                if (pfxKey is null)
                    notes.Add("no private key was included");
                if (keys.Count > 1)
                    notes.Add($"{keys.Count - 1} unused key(s) were ignored — a PFX carries one key");
                if (OutPassword.Length == 0)
                    notes.Add("warning: the PFX password is empty");
            }
            Status = $"Wrote {Path.GetFileName(outPath)} ({bytes.Length:N0} bytes)" +
                     (notes.Count > 0 ? " — " + string.Join("; ", notes) + "." : ".");
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
