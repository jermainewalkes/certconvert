using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CertConvert.Core;
using CertConvert.ViewModels;
using CertConvert.Views;

namespace CertConvert.Gui.Tests;

/// <summary>
/// Renders the Sign view (the mini-CA) through the real window and view-locator so
/// the XAML/binding graph is exercised headlessly, and guards the safety-relevant
/// default state: signing is blocked until a CA and a good CSR are both loaded.
/// </summary>
public class SignViewTests
{
    [AvaloniaFact]
    public void Sign_Is_Reachable_And_Renders()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.SelectedIndex = 5;   // Inspect, Convert, Chain, Keys, Generate, [Sign], About
        Assert.IsType<SignViewModel>(vm.CurrentPage);

        var view = window.GetVisualDescendants().OfType<SignView>().Single();
        Assert.Same(vm.Sign, view.DataContext);
    }

    [AvaloniaFact]
    public void Signing_Is_Blocked_Until_Ca_And_Good_Csr_Loaded()
    {
        var vm = new SignViewModel();
        Assert.False(vm.CanSign);           // nothing loaded

        vm.HasCa = true;
        Assert.False(vm.CanSign);           // CA alone is not enough

        vm.HasCsr = true;
        Assert.False(vm.CanSign);           // request loaded, but its signature isn't verified yet

        vm.CsrSignatureOk = true;
        Assert.True(vm.CanSign);            // CA + a request whose proof-of-possession verified

        vm.CsrSignatureOk = false;
        Assert.False(vm.CanSign);           // an unverified request re-blocks signing
    }

    [AvaloniaFact]
    public void Role_Toggle_Switches_Which_Sans_Apply()
    {
        var vm = new SignViewModel();
        Assert.True(vm.IsServer);           // defaults to a TLS server cert

        vm.Role = IssuedRole.TlsClient;
        Assert.False(vm.IsServer);          // client certs use email/URI SANs instead
    }

    [Fact]
    public void Issuance_Sans_Are_Scoped_To_The_Selected_Role()
    {
        // Every SAN field is populated (DNS/IP get pre-filled from a CSR; the operator
        // may also type email/URI). The issued cert must carry only the role's SANs.
        var vm = new SignViewModel
        {
            DnsNames = "device.example.com",
            IpAddresses = "10.0.0.5",
            EmailNames = "user@example.com",
            Uris = "spiffe://cluster/ns/sa",
        };

        vm.Role = IssuedRole.TlsServer;
        var server = vm.BuildIssuanceOptions();
        Assert.Contains("device.example.com", server.DnsNames);
        Assert.Contains("10.0.0.5", server.IpAddresses);
        Assert.Empty(server.EmailNames);            // a server cert never carries email/URI SANs
        Assert.Empty(server.Uris);

        vm.Role = IssuedRole.TlsClient;
        var client = vm.BuildIssuanceOptions();
        Assert.Empty(client.DnsNames);              // hidden DNS/IP fields don't leak into a client cert
        Assert.Empty(client.IpAddresses);
        Assert.Contains("user@example.com", client.EmailNames);
        Assert.Contains("spiffe://cluster/ns/sa", client.Uris);
    }
}
