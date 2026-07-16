using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CertConvert.Core;
using CertConvert.ViewModels;
using CertConvert.Views;

namespace CertConvert.Gui.Tests;

/// <summary>
/// Renders the real window on the headless platform so the whole XAML/binding/
/// view-locator graph is exercised without a display — this is what proves the
/// GUI actually comes up, beyond "the build compiled".
/// </summary>
public class MainWindowTests
{
    [AvaloniaFact]
    public void Window_Renders_WithAllSixNavigationItems()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        window.Show();

        var nav = window.GetVisualDescendants()
            .OfType<ListBox>()
            .Single(l => l.Classes.Contains("nav"));
        var names = nav.Items
            .OfType<ListBoxItem>()
            .Select(Avalonia.Automation.AutomationProperties.GetName)
            .ToArray();

        Assert.Equal(
            new[] { "Inspect", "Convert", "Chain", "Keys", "Generate", "About" },
            names);
    }

    [AvaloniaFact]
    public void Navigation_SwitchesPages()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.Same(vm.Inspect, vm.CurrentPage);
        vm.SelectedIndex = 5;
        Assert.Same(vm.About, vm.CurrentPage);
        vm.SelectedIndex = 2;
        Assert.Same(vm.Chain, vm.CurrentPage);
    }

    [AvaloniaFact]
    public void InspectView_ShowsCertificateDroppedInViaViewModel()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        // Write a throwaway self-signed cert and load it as the drop handler would.
        using var key = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
        using var cert = Generator.CreateSelfSigned(key.Key, new CertSpec
        {
            CommonName = "headless.test.local",
            ValidityDays = 30,
        });
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pem");
        File.WriteAllText(path, cert.ExportCertificatePem());
        try
        {
            vm.Inspect.LoadPath(path);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Single(vm.Inspect.Certificates);
        Assert.Equal("headless.test.local", vm.Inspect.Certificates[0].Info.DisplayName);
        Assert.Contains("1 certificate", vm.Inspect.Detected);

        // Clear resets the page completely.
        vm.Inspect.ClearCommand.Execute(null);
        Assert.Empty(vm.Inspect.Certificates);
        Assert.Equal("", vm.Inspect.FileName);
        Assert.Equal("", vm.Inspect.Detected);
    }

    [AvaloniaFact]
    public void AboutView_ExposesKoFiCommand()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.About.OpenKoFiCommand);
        Assert.Contains("never connects to the network", vm.About.SecurityStatement);
    }

    [AvaloniaFact]
    public void AboutView_ExposesUpdateControls()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.About.CheckForUpdatesCommand);
        Assert.NotNull(vm.About.DownloadAndInstallCommand);
        Assert.False(vm.About.UpdateAvailable);
        Assert.False(vm.UpdateAvailable);
    }

    [AvaloniaFact]
    public void GoToAbout_NavigatesToAboutPage()
    {
        var vm = new MainWindowViewModel();
        vm.SelectedIndex = 0;
        vm.GoToAboutCommand.Execute(null);
        Assert.Same(vm.About, vm.CurrentPage);
    }

    // The test project compiles against the normal (non-store) variant, so these
    // assertions pin down the full GitHub-build surface: the store variant is
    // verified separately by the gate build and the store-flag CLI smoke run.
    [AvaloniaFact]
    public void NormalBuild_ExposesUpdaterAndKoFiSurface()
    {
        Assert.False(CertConvert.AppInfo.IsStoreBuild);

        var vm = new MainWindowViewModel();
        // Sidebar Ko-fi footer and the whole updater/support surface stay visible.
        Assert.True(vm.ShowKoFi);
        Assert.True(vm.About.ShowUpdates);
        Assert.True(vm.About.ShowSupport);
        Assert.NotNull(vm.About.OpenKoFiCommand);
        Assert.NotNull(vm.About.CheckForUpdatesCommand);
        // The GitHub security statement keeps its opt-in update-check wording.
        Assert.Contains("checking GitHub for a newer version", vm.About.SecurityStatement);
        Assert.DoesNotContain("Updates are delivered by the store", vm.About.SecurityStatement);
    }
}
