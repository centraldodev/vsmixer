using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

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
    private double _volume = 0.72;

    [ObservableProperty]
    private double _pan;

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
}
