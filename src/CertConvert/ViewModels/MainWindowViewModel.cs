using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CertConvert.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public InspectViewModel Inspect { get; } = new();
    public ConvertViewModel Convert { get; } = new();
    public ChainViewModel Chain { get; } = new();
    public KeysViewModel Keys { get; } = new();
    public GenerateViewModel Generate { get; } = new();
    public AboutViewModel About { get; } = new();

    private readonly ViewModelBase[] _pages;

    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private ViewModelBase _currentPage;

    public MainWindowViewModel()
    {
        _pages = [Inspect, Convert, Chain, Keys, Generate, About];
        _currentPage = Inspect;
    }

    partial void OnSelectedIndexChanged(int value) =>
        CurrentPage = _pages[Math.Clamp(value, 0, _pages.Length - 1)];
}
