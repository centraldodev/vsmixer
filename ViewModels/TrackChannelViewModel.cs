using CommunityToolkit.Mvvm.ComponentModel;
using VSMixer.Models;

namespace VSMixer.ViewModels;

public partial class TrackChannelViewModel : ViewModelBase
{
    public TrackChannelViewModel(int number, string name, double level, string? filePath = null)
    {
        Number = number;
        Name = name;
        Level = level;
        FilePath = filePath;
    }

    public int Number { get; }
    public string? FilePath { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private double _volume = 1;

    [ObservableProperty]
    private MasterBus _masterBus = MasterBus.B;

    [ObservableProperty]
    private double _level;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isSolo;

    public string NumberLabel => Number.ToString("00");
    public string FooterTitle => Name;
    public string FileName => string.IsNullOrWhiteSpace(FilePath)
        ? Name
        : Path.GetFileName(FilePath);
    public string VolumeDb => Volume <= 0.001 ? "−∞ dB" : $"{20 * Math.Log10(Volume):0.0} dB";
    public bool IsClipping => Level >= 0.98;

    public bool IsMasterA
    {
        get => MasterBus == MasterBus.A;
        set
        {
            if (value)
            {
                MasterBus = MasterBus.A;
            }
        }
    }

    public bool IsMasterB
    {
        get => MasterBus == MasterBus.B;
        set
        {
            if (value)
            {
                MasterBus = MasterBus.B;
            }
        }
    }

    partial void OnMasterBusChanged(MasterBus value)
    {
        OnPropertyChanged(nameof(IsMasterA));
        OnPropertyChanged(nameof(IsMasterB));
    }

    partial void OnVolumeChanged(double value)
    {
        _ = value;
        OnPropertyChanged(nameof(VolumeDb));
    }

    partial void OnLevelChanged(double value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsClipping));
    }
}
