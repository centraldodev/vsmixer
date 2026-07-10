using CommunityToolkit.Mvvm.ComponentModel;

namespace VSMixer.ViewModels;

public partial class SessionRegionViewModel : ViewModelBase
{
    public SessionRegionViewModel(string name, int startMeasure, int endMeasure, string color)
    {
        Name = name;
        StartMeasure = startMeasure;
        EndMeasure = endMeasure;
        Color = color;
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private int _startMeasure;

    [ObservableProperty]
    private int _endMeasure;

    [ObservableProperty]
    private string _color;

    [ObservableProperty]
    private bool _isQueued;

    [ObservableProperty]
    private bool _isLooping;

    [ObservableProperty]
    private bool _preCountEnabled;
}
