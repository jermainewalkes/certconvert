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

public sealed record ChainRow(string StatusMark, string Name, string Issues)
{
    public bool HasIssues => Issues.Length > 0;
}

public partial class ChainViewModel : ViewModelBase
{
    public ObservableCollection<string> InputFiles { get; } = new();
    public ObservableCollection<ChainRow> Results { get; } = new();

    public IReadOnlyList<FormatOption> ExportFormats { get; } =
    [
        new("PEM Bundle (.pem)", CertOutputFormat.Pem),
        new("PKCS #7 (.p7b)", CertOutputFormat.Pkcs7Der),
        new("PKCS #12 (.pfx) With Key", CertOutputFormat.Pkcs12),
    ];

    [ObservableProperty] private FormatOption _selectedExportFormat;
    [ObservableProperty] private string _selectedInput = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _keyFile = "";
    [ObservableProperty] private string _keyPassword = "";
    [ObservableProperty] private string _outPassword = "";
    [ObservableProperty] private bool _trustSystemRoots;
    [ObservableProperty] private bool _isPfx;
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private string _status =
        "Add the device, intermediate and root certificates in any order.";

    public ChainViewModel()
    {
        _selectedExportFormat = ExportFormats[0];
    }

    partial void OnSelectedExportFormatChanged(FormatOption value) =>
        IsPfx = value.Value == CertOutputFormat.Pkcs12;

    [RelayCommand]
    private async Task AddFiles()
    {
        foreach (var f in await Dialogs.OpenFilesAsync("Add Certificates"))
            if (!InputFiles.Contains(f))
                InputFiles.Add(f);
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedInput.Length > 0)
            InputFiles.Remove(SelectedInput);
    }

    [RelayCommand]
    private void ClearFiles()
    {
        InputFiles.Clear();
        Results.Clear();
        Verdict = "";
    }

    [RelayCommand]
    private async Task PickKeyFile()
    {
        var files = await Dialogs.OpenFilesAsync("Choose Private Key File", false);
        if (files.Count == 1)
            KeyFile = files[0];
    }

    [RelayCommand]
    private void ValidateChain()
    {
        Results.Clear();
        Verdict = "";
        var certs = new List<X509Certificate2>();
        try
        {
            certs = LoadCerts();
            var result = ChainTools.Validate(certs, TrustSystemRoots);
            foreach (var element in result.Elements)
                Results.Add(new ChainRow(
                    element.IsOk ? "OK" : "FAIL",
                    element.Certificate.DisplayName,
                    string.Join("; ", element.Issues)));
            string notes = result.Notes.Count > 0
                ? " " + string.Join(" ", result.Notes)
                : "";
            Verdict = (result.IsValid ? "Chain is valid." : "Chain is NOT valid.") + notes;
            Status = Verdict;
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
        finally
        {
            foreach (var c in certs) c.Dispose();
        }
    }

    [RelayCommand]
    private async Task ExportChain()
    {
        var certs = new List<X509Certificate2>();
        var keys = new List<PrivateKeyEntry>();
        try
        {
            certs = LoadCerts();
            var ordered = ChainTools.Order(certs);
            var format = SelectedExportFormat.Value;

            System.Security.Cryptography.AsymmetricAlgorithm? pfxKey = null;
            if (format == CertOutputFormat.Pkcs12)
            {
                if (KeyFile.Length > 0)
                {
                    using var keyContent = ContentLoader.LoadFile(
                        KeyFile, KeyPassword.Length > 0 ? KeyPassword : null);
                    keys.AddRange(keyContent.PrivateKeys);
                    keyContent.PrivateKeys.Clear();
                }
                pfxKey = keys.FirstOrDefault(
                    k => ordered.Any(c => KeyTools.Matches(c, k.Key)))?.Key;
                if (keys.Count > 0 && pfxKey is null)
                    throw new CertConvertException(
                        "The private key does not match any certificate in the chain.");
            }

            byte[] bytes = Converter.Export(ordered, format, new ExportOptions
            {
                Password = OutPassword,
                PrivateKey = pfxKey,
            });

            string suggested = "chain" + format switch
            {
                CertOutputFormat.Pem => ".pem",
                CertOutputFormat.Pkcs7Der => ".p7b",
                CertOutputFormat.Pkcs12 => ".pfx",
                _ => ".out",
            };
            var outPath = await Dialogs.SaveFileAsync("Export Chain", suggested);
            if (outPath is null)
            {
                Status = "Export cancelled.";
                return;
            }
            File.WriteAllBytes(outPath, bytes);
            Status = $"Wrote {Path.GetFileName(outPath)} — order: " +
                     string.Join(" → ", ordered.Select(c => Inspector.Inspect(c).DisplayName)) + ".";
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

    private List<X509Certificate2> LoadCerts()
    {
        if (InputFiles.Count == 0)
            throw new CertConvertException("Add at least one certificate file first.");
        var certs = new List<X509Certificate2>();
        foreach (var path in InputFiles)
        {
            using var content = ContentLoader.LoadFile(
                path, Password.Length > 0 ? Password : null);
            certs.AddRange(content.Certificates);
            content.Certificates.Clear();
        }
        if (certs.Count == 0)
            throw new CertConvertException("No certificates found in the input files.");
        return certs;
    }
}
