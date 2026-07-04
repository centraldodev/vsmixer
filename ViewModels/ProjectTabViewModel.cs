using CommunityToolkit.Mvvm.ComponentModel;
using VSMixer.Models;

namespace VSMixer.ViewModels;

public partial class ProjectTabViewModel : ViewModelBase
{
    public ProjectTabViewModel(string name, string? projectPath, VsmixerProjectDocument document)
    {
        Name = name;
        ProjectPath = projectPath;
        Document = document;
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private MidiMapping? _selectMidiMapping;

    public string? ProjectPath { get; set; }
    public VsmixerProjectDocument Document { get; set; }
    public string DisplayName => IsActive ? $"* {Name}" : Name;
    public string MidiMappingMenuLabel => SelectMidiMapping is null ? "Mapear MIDI..." : $"Remapear MIDI ({SelectMidiMapping.DisplayName})";
    public bool HasMidiMapping => SelectMidiMapping is not null;

    partial void OnNameChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnIsActiveChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnSelectMidiMappingChanged(MidiMapping? value)
    {
        _ = value;
        OnPropertyChanged(nameof(MidiMappingMenuLabel));
        OnPropertyChanged(nameof(HasMidiMapping));
    }
}
