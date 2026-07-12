using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CertConvert.Services;

namespace CertConvert.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public InspectViewModel Inspect { get; } = new();
    public ConvertViewModel Convert { get; } = new();
    public ChainViewModel Chain { get; } = new();
    public KeysViewModel Keys { get; } = new();
    public GenerateViewModel Generate { get; } = new();
    public AboutViewModel About { get; }

    private readonly ViewModelBase[] _pages;

    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private bool _updateAvailable;

    public MainWindowViewModel()
    {
        var settings = AppSettings.Load();
        About = new AboutViewModel(settings, new UpdateService());
        About.UpdateFound += () => UpdateAvailable = true;

        _pages = [Inspect, Convert, Chain, Keys, Generate, About];
        _currentPage = Inspect;

        // Opt-in only; the env var lets tests construct this VM without network access.
        if (settings.CheckForUpdatesOnLaunch &&
            Environment.GetEnvironmentVariable("CERTCONVERT_DISABLE_UPDATE_CHECK") is null)
        {
            _ = About.CheckForUpdates();
        }
    }

    partial void OnSelectedIndexChanged(int value) =>
        CurrentPage = _pages[Math.Clamp(value, 0, _pages.Length - 1)];

    [RelayCommand]
    private void GoToAbout() => SelectedIndex = _pages.Length - 1;
}
