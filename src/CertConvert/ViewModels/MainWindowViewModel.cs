namespace CertConvert.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public InspectViewModel Inspect { get; } = new();
    public ConvertViewModel Convert { get; } = new();
    public ChainViewModel Chain { get; } = new();
    public KeysViewModel Keys { get; } = new();
    public GenerateViewModel Generate { get; } = new();
    public AboutViewModel About { get; } = new();
}
