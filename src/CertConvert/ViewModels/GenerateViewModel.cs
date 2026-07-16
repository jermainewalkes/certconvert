using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Core;
using CertConvert.Services;

namespace CertConvert.ViewModels;

public sealed record AlgorithmOption(string Label, KeyAlgorithmChoice Value)
{
    public override string ToString() => Label;
}

/// <summary>What the Generate page produces. Chosen first, so the form shows
/// only the fields that output uses (a CSR has no validity — the CA sets it).</summary>
public enum CertOutput { SelfSigned, Csr }

public partial class GenerateViewModel : ViewModelBase
{
    public IReadOnlyList<AlgorithmOption> Algorithms { get; } =
    [
        new("RSA 2048-bit", KeyAlgorithmChoice.Rsa2048),
        new("RSA 3072-bit", KeyAlgorithmChoice.Rsa3072),
        new("RSA 4096-bit", KeyAlgorithmChoice.Rsa4096),
        new("ECDSA P-256", KeyAlgorithmChoice.EcP256),
        new("ECDSA P-384", KeyAlgorithmChoice.EcP384),
        new("ECDSA P-521", KeyAlgorithmChoice.EcP521),
    ];

    [ObservableProperty] private AlgorithmOption _selectedAlgorithm;
    [ObservableProperty] private string _keyDescription = "";
    [ObservableProperty] private string _keyPassword = "";
    [ObservableProperty] private bool _hasKey;

    [ObservableProperty] private string _commonName = "";
    [ObservableProperty] private string _organization = "";
    [ObservableProperty] private string _organizationalUnit = "";
    [ObservableProperty] private string _country = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _locality = "";
    [ObservableProperty] private string _dnsNames = "";
    [ObservableProperty] private string _ipAddresses = "";
    [ObservableProperty] private string _validityDays = "365";
    [ObservableProperty] private bool _isCa;
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowValidity))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionLabel))]
    private CertOutput _output = CertOutput.SelfSigned;

    /// <summary>Validity applies to self-signed certificates only.</summary>
    public bool ShowValidity => Output == CertOutput.SelfSigned;

    public string PrimaryActionLabel => Output == CertOutput.SelfSigned
        ? "Generate And Save Certificate…"
        : "Generate And Save CSR…";

    private PrivateKeyEntry? _key;

    public GenerateViewModel()
    {
        _selectedAlgorithm = Algorithms[0];
    }

    [RelayCommand]
    private async Task GenerateKeyAndSave()
    {
        try
        {
            Status = $"Generating {SelectedAlgorithm.Label} key…";
            var choice = SelectedAlgorithm.Value;
            var entry = await Task.Run(() => Generator.CreateKey(choice));

            string pem = KeyPassword.Length > 0
                ? KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs8EncryptedPem, KeyPassword)
                : KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs8Pem);
            var outPath = await Dialogs.SaveFileAsync("Save Private Key", "private.key");
            if (outPath is null)
            {
                entry.Dispose();
                Status = "Key generation cancelled — nothing was saved.";
                return;
            }
            File.WriteAllBytes(outPath, Encoding.ASCII.GetBytes(pem));
            SetKey(entry);
            Status = $"Generated {entry.Description} key and wrote {Path.GetFileName(outPath)}." +
                     (KeyPassword.Length > 0 ? " The key file is encrypted." : "");
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadExistingKey()
    {
        var files = await Dialogs.OpenFilesAsync("Open Private Key", false);
        if (files.Count != 1) return;
        try
        {
            var content = ContentLoader.LoadFile(
                files[0], KeyPassword.Length > 0 ? KeyPassword : null);
            if (content.PrivateKeys.Count == 0)
            {
                content.Dispose();
                throw new CertConvertException("No private key found in the file.");
            }
            var entry = content.PrivateKeys[0];
            content.PrivateKeys.Clear();
            content.Dispose();
            SetKey(entry);
            Status = $"Loaded {entry.Description} key from {Path.GetFileName(files[0])}.";
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
    }

    private void SetKey(PrivateKeyEntry entry)
    {
        _key?.Dispose();
        _key = entry;
        KeyDescription = entry.Description;
        HasKey = true;
    }

    [RelayCommand]
    private async Task GenerateAndSave()
    {
        if (_key is null)
        {
            Status = "Generate or load a key first.";
            return;
        }
        if (Output == CertOutput.Csr)
            await SaveCsr();
        else
            await SaveSelfSigned();
    }

    private async Task SaveCsr()
    {
        try
        {
            string csr = Generator.CreateCsrPem(_key!.Key, ReadSpec());
            var outPath = await Dialogs.SaveFileAsync("Save Certificate Request", "request.csr");
            if (outPath is null)
            {
                Status = "Save cancelled.";
                return;
            }
            File.WriteAllBytes(outPath, Encoding.ASCII.GetBytes(csr));
            Status = $"Wrote {Path.GetFileName(outPath)}.";
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
    }

    private async Task SaveSelfSigned()
    {
        try
        {
            var spec = ReadSpec();
            using var cert = Generator.CreateSelfSigned(_key!.Key, spec);
            var outPath = await Dialogs.SaveFileAsync(
                "Save Self-Signed Certificate", "certificate.pem");
            if (outPath is null)
            {
                Status = "Save cancelled.";
                return;
            }
            File.WriteAllBytes(outPath,
                Encoding.ASCII.GetBytes(cert.ExportCertificatePem() + "\n"));
            Status = $"Wrote {Path.GetFileName(outPath)} — " +
                     $"{Inspector.Inspect(cert).DisplayName}, valid {spec.ValidityDays} days.";
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
    }

    private CertSpec ReadSpec()
    {
        if (!int.TryParse(ValidityDays, out int days))
            throw new CertConvertException("Validity days must be a number.");
        return new CertSpec
        {
            CommonName = CommonName,
            Organization = Organization,
            OrganizationalUnit = OrganizationalUnit,
            Country = Country,
            State = State,
            Locality = Locality,
            DnsNames = SplitList(DnsNames),
            IpAddresses = SplitList(IpAddresses),
            ValidityDays = days,
            IsCertificateAuthority = IsCa,
        };
    }

    private static string[] SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
