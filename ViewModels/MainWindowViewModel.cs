using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Melanchall.DryWetMidi.Multimedia;
using VSMixer.Models;
using VSMixer.Services;

namespace VSMixer.ViewModels;

public enum MidiMapTarget
{
    Play,
    Rewind,
    MasterA,
    MasterB
}

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly string[] _sectionColors = ["#2f7dff", "#28b66f", "#db9d27", "#b45cff", "#ef5a77", "#18a8b8"];
    private readonly IReadOnlyDictionary<string, string> _padFiles = new Dictionary<string, string>
    {
        ["C"] = "C_1.wav",
        ["C#"] = "C_sharp_1.wav",
        ["D"] = "D_1.wav",
        ["D#"] = "D_sharp_1.wav",
        ["E"] = "E_1.wav",
        ["F"] = "F_1.wav",
        ["F#"] = "F_sharp_1.wav",
        ["G"] = "G_1.wav",
        ["G#"] = "G_sharp_1.wav",
        ["A"] = "A_1.wav",
        ["A#"] = "A_sharp_1.wav",
        ["B"] = "B_1.wav"
    };
    private readonly Dictionary<string, string> _guideVoiceFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAudioEngine _audioEngine;
    private readonly IMidiControlService _midiControlService;
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private SessionRegionViewModel? _editingSession;
    private SessionRegionViewModel? _queuedSession;
    private SessionRegionViewModel? _activePlaybackSession;
    private SessionRegionViewModel? _selectedSessionForMenu;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _metronomeTimer;
    private readonly Stopwatch _metronomeClock = new();
    private double _durationSeconds;
    private double _nextMetronomeBeatSeconds;
    private int _metronomeBeatIndex;
    private string? _lastGuideVoiceKey;
    private bool _isRestoringProjectTab;
    private MidiMapTarget? _pendingMidiMapTarget;
    private DateTime _lastPerformanceSampleTime = DateTime.UtcNow;
    private TimeSpan _lastPerformanceSampleCpuTime;
    private DateTime _nextPerformanceSampleTime = DateTime.MinValue;

    public MainWindowViewModel()
        : this(new BassAudioEngine(), new DryWetMidiControlService())
    {
    }

    public MainWindowViewModel(IAudioEngine audioEngine, IMidiControlService midiControlService)
    {
        _audioEngine = audioEngine;
        _midiControlService = midiControlService;
        _meterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(55)
        };
        _meterTimer.Tick += (_, _) => UpdateTransport();
        _meterTimer.Start();

        _metronomeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        _metronomeTimer.Tick += (_, _) => UpdateMetronome();
        _metronomeTimer.Start();

        WaveformPeaks.Clear();
        RefreshGuideVoiceFiles();
        _midiControlService.MessageReceived += OnMidiMessageReceived;

        var initialTab = new ProjectTabViewModel(ProjectName, CurrentProjectPath, CreateProjectDocument())
        {
            IsActive = true
        };
        ProjectTabs.Add(initialTab);
        _activeProjectTab = initialTab;
    }

    public ObservableCollection<ProjectTabViewModel> ProjectTabs { get; } = [];
    public ObservableCollection<TrackChannelViewModel> Tracks { get; } = [];
    public ObservableCollection<double> WaveformPeaks { get; } = [];
    public ObservableCollection<SessionRegionViewModel> SessionRegions { get; } = [];
    public ObservableCollection<DeviceOptionViewModel> AudioOutputDevices { get; } = [];
    public ObservableCollection<DeviceOptionViewModel> MidiControllerDevices { get; } = [];
    public ObservableCollection<string> MidiControlBanks { get; } = ["Transporte", "Tracks 1-8", "Tracks 9-16", "Tracks 17-24", "Master"];

    public ObservableCollection<string> TimeMarkers { get; } =
        ["0:00", "0:27", "0:55", "1:22", "1:50", "2:18", "2:45", "3:13", "3:41", "4:08", "4:36", "5:04"];

    public ObservableCollection<string> TimeSignatures { get; } = ["4/4", "3/4"];
    public ObservableCollection<string> GridOptions { get; } = ["1/2", "1/4", "1/8", "1/16"];
    public ObservableCollection<string> PadNotes { get; } = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    public ObservableCollection<string> SessionNameOptions { get; } = [];
    public ObservableCollection<SessionCopyOptionViewModel> SessionCopyOptions { get; } = [];

    [ObservableProperty]
    private string _projectName = "Projeto 1";

    [ObservableProperty]
    private ProjectTabViewModel? _activeProjectTab;

    [ObservableProperty]
    private string? _currentProjectPath;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string _currentTime = "0:00";

    [ObservableProperty]
    private string _barClock = "1.1.01";

    [ObservableProperty]
    private int _detectedBpm = 133;

    [ObservableProperty]
    private string _detectedBpmText = "133";

    [ObservableProperty]
    private int _playbackBpm = 133;

    [ObservableProperty]
    private string _playbackBpmText = "133";

    [ObservableProperty]
    private string _selectedTimeSignature = "4/4";

    [ObservableProperty]
    private string _selectedGrid = "1/4";

    [ObservableProperty]
    private int _tempoOffset;

    [ObservableProperty]
    private int _pitchOffset;

    [ObservableProperty]
    private bool _metronomeEnabled;

    [ObservableProperty]
    private double _metronomeVolume = 0.82;

    [ObservableProperty]
    private double _metronomePan = -1;

    [ObservableProperty]
    private bool _guideVoiceEnabled;

    [ObservableProperty]
    private double _guideVoiceVolume = 0.85;

    [ObservableProperty]
    private double _guideVoicePan = -1;

    [ObservableProperty]
    private bool _padContinuousEnabled;

    [ObservableProperty]
    private string _selectedPadNote = "C";

    [ObservableProperty]
    private double _masterAVolume = 0.96;

    [ObservableProperty]
    private double _masterBVolume = 0.96;

    [ObservableProperty]
    private double _playbackProgress;

    [ObservableProperty]
    private string _statusMessage = "Importe os arquivos multitrack para comecar.";

    [ObservableProperty]
    private string _cpuUsageText = "CPU 0%";

    [ObservableProperty]
    private string _memoryUsageText = "RAM 0 MB";

    [ObservableProperty]
    private bool _isAddSessionModalOpen;

    [ObservableProperty]
    private bool _isAudioSettingsModalOpen;

    [ObservableProperty]
    private bool _isSessionActionsModalOpen;

    [ObservableProperty]
    private bool _isMidiMappingModalOpen;

    [ObservableProperty]
    private bool _isMidiMappingEditMode;

    [ObservableProperty]
    private DeviceOptionViewModel? _selectedAudioOutputDevice;

    [ObservableProperty]
    private bool _isMidiControllerEnabled;

    [ObservableProperty]
    private DeviceOptionViewModel? _selectedMidiController;

    [ObservableProperty]
    private string _selectedMidiControlBank = "Transporte";

    [ObservableProperty]
    private MidiMapping? _playMidiMapping;

    [ObservableProperty]
    private MidiMapping? _rewindMidiMapping;

    [ObservableProperty]
    private MidiMapping? _masterAMidiMapping;

    [ObservableProperty]
    private MidiMapping? _masterBMidiMapping;

    [ObservableProperty]
    private string _newSessionName = "Sessao";

    [ObservableProperty]
    private int _newSessionStartMeasure = 145;

    [ObservableProperty]
    private int _newSessionEndMeasure = 176;

    [ObservableProperty]
    private SessionCopyOptionViewModel? _selectedSessionCopySource;

    public int TrackCount => Tracks.Count;
    public string PlayPauseText => IsPlaying ? "Pause" : "Play";
    public bool IsNotPlaying => !IsPlaying;
    public bool HasSavedProject => !string.IsNullOrWhiteSpace(CurrentProjectPath);
    public bool IsEditingSession => _editingSession is not null;
    public string SessionModalTitle => IsEditingSession ? "Editar sessao" : "Nova sessao";
    public string SessionModalSubmitText => IsEditingSession ? "Salvar" : "Adicionar";
    public string SessionActionsTitle => _selectedSessionForMenu is null ? "Sessao" : _selectedSessionForMenu.Name;
    public string SessionLoopActionText => _selectedSessionForMenu?.IsLooping == true ? "Remover loop" : "Colocar em loop";
    public bool IsNotMidiMappingEditMode => !IsMidiMappingEditMode;
    public string MidiMappingEditModeText => IsMidiMappingEditMode ? "Sair MIDI" : "MIDI";
    public string MidiMappingModalTitle => _pendingMidiMapTarget is null ? "Mapear MIDI" : $"Mapear {GetMidiMapTargetDisplayName(_pendingMidiMapTarget.Value)}";
    public string MidiMappingModalInstruction => _pendingMidiMapTarget is MidiMapTarget.MasterA or MidiMapTarget.MasterB
        ? "Mova um fader ou knob MIDI CC. O valor recebido sera convertido de 0-127 para 0-100%."
        : "Pressione um botao, pad ou tecla MIDI para usar como clique desta acao.";
    public string MidiMappingModalCurrent => _pendingMidiMapTarget is null
        ? "Nenhum controle selecionado."
        : GetMidiMapping(_pendingMidiMapTarget.Value) is { } mapping
            ? $"Atual: {mapping.DisplayName}"
            : "Atual: sem mapeamento.";
    public string PlayMidiMappingLabel => FormatMidiMappingLabel(PlayMidiMapping);
    public string RewindMidiMappingLabel => FormatMidiMappingLabel(RewindMidiMapping);
    public string MasterAMidiMappingLabel => FormatMidiMappingLabel(MasterAMidiMapping);
    public string MasterBMidiMappingLabel => FormatMidiMappingLabel(MasterBMidiMapping);
    private bool HasImportedAudio => _audioEngine.TrackCount > 0;

    partial void OnProjectNameChanged(string value)
    {
        if (_isRestoringProjectTab || ActiveProjectTab is null)
        {
            return;
        }

        ActiveProjectTab.Name = value;
    }

    partial void OnActiveProjectTabChanged(ProjectTabViewModel? oldValue, ProjectTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            SaveStateToProjectTab(oldValue);
            oldValue.IsActive = false;
        }

        if (newValue is null)
        {
            return;
        }

        newValue.IsActive = true;
        LoadProjectDocument(newValue.Document, newValue.ProjectPath, $"Aba ativa: {newValue.Name}.", updateActiveTab: false);
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseText));
        OnPropertyChanged(nameof(IsNotPlaying));
    }

    partial void OnCurrentProjectPathChanged(string? value)
    {
        if (!_isRestoringProjectTab && ActiveProjectTab is not null)
        {
            ActiveProjectTab.ProjectPath = value;
        }

        OnPropertyChanged(nameof(HasSavedProject));
    }

    partial void OnMasterAVolumeChanged(double value)
    {
        ApplyMixerStates();
    }

    partial void OnMasterBVolumeChanged(double value)
    {
        ApplyMixerStates();
    }

    partial void OnIsMidiMappingEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotMidiMappingEditMode));
        OnPropertyChanged(nameof(MidiMappingEditModeText));
        StatusMessage = value
            ? "Modo MIDI ativo. Clique em Play, Voltar, Master A ou Master B para mapear."
            : "Modo MIDI desativado.";
    }

    partial void OnPlayMidiMappingChanged(MidiMapping? value)
    {
        _ = value;
        OnPropertyChanged(nameof(PlayMidiMappingLabel));
    }

    partial void OnRewindMidiMappingChanged(MidiMapping? value)
    {
        _ = value;
        OnPropertyChanged(nameof(RewindMidiMappingLabel));
    }

    partial void OnMasterAMidiMappingChanged(MidiMapping? value)
    {
        _ = value;
        OnPropertyChanged(nameof(MasterAMidiMappingLabel));
    }

    partial void OnMasterBMidiMappingChanged(MidiMapping? value)
    {
        _ = value;
        OnPropertyChanged(nameof(MasterBMidiMappingLabel));
    }

    partial void OnSelectedTimeSignatureChanged(string value)
    {
        _ = value;
        UpdateClockFields(_audioEngine.CurrentPositionSeconds);
        if (IsPlaying && MetronomeEnabled)
        {
            StartMetronome(playFirstClick: true);
        }
    }

    partial void OnSelectedGridChanged(string value)
    {
        if (!GridOptions.Contains(value))
        {
            SelectedGrid = "1/4";
            return;
        }

        if (IsPlaying && MetronomeEnabled)
        {
            StartMetronome(playFirstClick: true);
        }
    }

    partial void OnDetectedBpmChanged(int value)
    {
        DetectedBpm = Math.Clamp(value, 1, 300);
        SyncBpmText(nameof(DetectedBpmText), DetectedBpmText, DetectedBpm);
        ApplyPlaybackTempo();
        UpdateClockFields(_audioEngine.CurrentPositionSeconds);
    }

    partial void OnPlaybackBpmChanged(int value)
    {
        PlaybackBpm = Math.Clamp(value, 1, 300);
        SyncBpmText(nameof(PlaybackBpmText), PlaybackBpmText, PlaybackBpm);
        ApplyPlaybackTempo();
        RescheduleMetronome();
    }

    partial void OnDetectedBpmTextChanged(string value)
    {
        if (TryParseBpm(value, out var bpm))
        {
            DetectedBpm = bpm;
        }
    }

    partial void OnPlaybackBpmTextChanged(string value)
    {
        if (TryParseBpm(value, out var bpm))
        {
            PlaybackBpm = bpm;
        }
    }

    partial void OnMetronomeEnabledChanged(bool value)
    {
        if (value && IsPlaying)
        {
            StartMetronome(playFirstClick: true);
            return;
        }

        StopMetronome();
    }

    partial void OnMetronomeVolumeChanged(double value)
    {
        _audioEngine.SetMetronomeState(value, MetronomePan);
    }

    partial void OnMetronomePanChanged(double value)
    {
        _audioEngine.SetMetronomeState(MetronomeVolume, value);
    }

    partial void OnGuideVoiceVolumeChanged(double value)
    {
        _audioEngine.SetGuideVoiceState(value, GuideVoicePan);
    }

    partial void OnGuideVoicePanChanged(double value)
    {
        _audioEngine.SetGuideVoiceState(GuideVoiceVolume, value);
    }

    partial void OnGuideVoiceEnabledChanged(bool value)
    {
        if (!value)
        {
            _audioEngine.StopGuideVoice();
        }

        StatusMessage = value ? "Voz guia ligada." : "Voz guia desligada.";
    }

    partial void OnPadContinuousEnabledChanged(bool value)
    {
        if (value)
        {
            ApplyPadState();
            return;
        }

        _audioEngine.StopPad();
        StatusMessage = "Pad desligado.";
    }

    partial void OnSelectedPadNoteChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !_padFiles.ContainsKey(value))
        {
            SelectedPadNote = "C";
            return;
        }

        if (PadContinuousEnabled)
        {
            ApplyPadState();
        }
    }

    partial void OnPitchOffsetChanged(int value)
    {
        PitchOffset = Math.Clamp(value, -12, 12);
        _audioEngine.SetPitch(PitchOffset, GetPitchExemptTrackIndices());
    }

    partial void OnSelectedAudioOutputDeviceChanged(DeviceOptionViewModel? value)
    {
        if (value is null || value.IsPlaceholder)
        {
            return;
        }

        StatusMessage = _audioEngine.SetOutputDevice(value.Id)
            ? $"Saida de audio selecionada: {value.Name}."
            : _audioEngine.LastError ?? "Nao foi possivel selecionar a saida de audio.";
    }

    partial void OnIsMidiControllerEnabledChanged(bool value)
    {
        _ = value;
        ApplyMidiControllerState();
    }

    partial void OnSelectedMidiControllerChanged(DeviceOptionViewModel? value)
    {
        _ = value;
        if (IsMidiControllerEnabled)
        {
            ApplyMidiControllerState();
        }
    }

    partial void OnSelectedMidiControlBankChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !MidiControlBanks.Contains(value))
        {
            SelectedMidiControlBank = "Transporte";
            return;
        }

        if (IsMidiControllerEnabled && SelectedMidiController is not null && !SelectedMidiController.IsPlaceholder)
        {
            StatusMessage = $"MIDI controlando banco: {SelectedMidiControlBank}.";
        }
    }

    partial void OnNewSessionStartMeasureChanged(int value)
    {
        _ = value;
        ApplyCopiedSessionLength();
    }

    partial void OnSelectedSessionCopySourceChanged(SessionCopyOptionViewModel? value)
    {
        _ = value;
        ApplyCopiedSessionLength();
    }

    public void ImportTracks(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        IsPlaying = false;
        _audioEngine.Pause();
        _audioEngine.StopGuideVoice();
        StopMetronome();
        _lastGuideVoiceKey = null;
        var orderedFilePaths = OrderTrackFilePaths(filePaths);
        var results = _audioEngine.LoadTracks(orderedFilePaths);
        var loaded = results.Where(result => result.IsLoaded).ToArray();
        var failed = results.Where(result => !result.IsLoaded).ToArray();

        ClearTracks();
        for (var i = 0; i < loaded.Length; i++)
        {
            var track = new TrackChannelViewModel(i + 1, loaded[i].Name, 0, loaded[i].FilePath)
            {
                MasterBus = DetermineDefaultMasterBus(loaded[i].Name)
            };
            SubscribeTrack(track);
            Tracks.Add(track);
        }

        OnPropertyChanged(nameof(TrackCount));
        ApplyMixerStates();
        UpdateWaveformPeaks();
        _durationSeconds = Math.Max(0, _audioEngine.DurationSeconds);
        CurrentTime = FormatTime(0);
        PlaybackProgress = 0;

        var detectedBpm = _audioEngine.DetectBpm();
        if (detectedBpm.HasValue)
        {
            DetectedBpm = detectedBpm.Value;
            PlaybackBpm = detectedBpm.Value;
        }

        UpdateClockFields(0);

        StatusMessage = failed.Length == 0
            ? $"{loaded.Length} tracks importadas. BPM detectado: {DetectedBpm}."
            : $"{loaded.Length} tracks importadas. {failed.Length} falharam: {string.Join(", ", failed.Select(item => item.ErrorMessage).Distinct())}";
        SaveActiveProjectTab();
    }

    public void SaveProject(string filePath)
    {
        ProjectName = Path.GetFileNameWithoutExtension(filePath);
        var document = CreateProjectDocument();
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
        CurrentProjectPath = filePath;
        StatusMessage = $"Projeto salvo: {ProjectName}.";
        SaveActiveProjectTab();
    }

    public void SaveCurrentProject()
    {
        if (string.IsNullOrWhiteSpace(CurrentProjectPath))
        {
            return;
        }

        SaveProject(CurrentProjectPath);
    }

    public void OpenProject(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var document = JsonSerializer.Deserialize<VsmixerProjectDocument>(json);
        if (document is null)
        {
            StatusMessage = "Nao foi possivel abrir o projeto.";
            return;
        }

        LoadProjectDocument(document, filePath, $"Projeto aberto: {document.ProjectName}.", updateActiveTab: true);
    }

    public void SeekToProgress(double progress)
    {
        if (!HasImportedAudio || _durationSeconds <= 0)
        {
            return;
        }

        var targetSeconds = Math.Clamp(progress, 0, 1) * _durationSeconds;
        _audioEngine.Seek(targetSeconds);
        _audioEngine.StopGuideVoice();
        _lastGuideVoiceKey = null;
        ClearQueuedSession();
        _activePlaybackSession = GetSessionAtPosition(targetSeconds);
        if (IsPlaying && MetronomeEnabled)
        {
            StartMetronome(playFirstClick: false);
        }

        UpdateClockFields(targetSeconds);
        UpdateGuideVoice(targetSeconds);
    }

    [RelayCommand]
    private void NewProjectTab()
    {
        SaveActiveProjectTab();
        var projectNumber = ProjectTabs.Count + 1;
        var document = CreateEmptyProjectDocument($"Projeto {projectNumber}");
        var tab = new ProjectTabViewModel(document.ProjectName, null, document);
        ProjectTabs.Add(tab);
        ActiveProjectTab = tab;
    }

    [RelayCommand]
    private void SelectProjectTab(ProjectTabViewModel tab)
    {
        if (tab == ActiveProjectTab)
        {
            return;
        }

        ActiveProjectTab = tab;
    }

    [RelayCommand]
    private void CloseProjectTab(ProjectTabViewModel tab)
    {
        if (ProjectTabs.Count <= 1)
        {
            return;
        }

        var index = ProjectTabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasActive = tab == ActiveProjectTab;
        ProjectTabs.Remove(tab);
        if (wasActive)
        {
            ActiveProjectTab = ProjectTabs[Math.Clamp(index, 0, ProjectTabs.Count - 1)];
        }
    }

    private void LoadProjectDocument(VsmixerProjectDocument document, string? projectPath, string statusMessage, bool updateActiveTab)
    {
        _isRestoringProjectTab = true;
        IsPlaying = false;
        _audioEngine.Pause();
        _audioEngine.StopGuideVoice();
        StopMetronome();
        _lastGuideVoiceKey = null;
        ProjectName = document.ProjectName;
        DetectedBpm = document.DetectedBpm;
        PlaybackBpm = document.PlaybackBpm;
        PitchOffset = document.PitchOffset;
        SelectedTimeSignature = document.SelectedTimeSignature;
        SelectedGrid = document.SelectedGrid;
        MasterAVolume = document.MasterAVolume;
        MasterBVolume = document.MasterBVolume;
        MetronomeEnabled = document.MetronomeEnabled;
        MetronomeVolume = document.MetronomeVolume;
        MetronomePan = document.MetronomePan;
        GuideVoiceEnabled = document.GuideVoiceEnabled;
        GuideVoiceVolume = document.GuideVoiceVolume;
        GuideVoicePan = document.GuideVoicePan;
        SelectedPadNote = PadNotes.Contains(document.SelectedPadNote) ? document.SelectedPadNote : "C";
        PadContinuousEnabled = document.PadContinuousEnabled;
        SelectedMidiControlBank = MidiControlBanks.Contains(document.MidiControlBank)
            ? document.MidiControlBank
            : "Transporte";
        PlayMidiMapping = document.PlayMidiMapping;
        RewindMidiMapping = document.RewindMidiMapping;
        MasterAMidiMapping = document.MasterAMidiMapping;
        MasterBMidiMapping = document.MasterBMidiMapping;
        RefreshDeviceLists();
        SelectedAudioOutputDevice = AudioOutputDevices.FirstOrDefault(device => device.Id == document.AudioOutputDeviceId)
            ?? AudioOutputDevices.FirstOrDefault();
        SelectedMidiController = MidiControllerDevices.FirstOrDefault(device => string.Equals(device.Name, document.MidiControllerName, StringComparison.OrdinalIgnoreCase))
            ?? MidiControllerDevices.FirstOrDefault(device => !device.IsPlaceholder);
        IsMidiControllerEnabled = document.MidiControllerEnabled;

        var paths = document.Tracks.Select(track => track.FilePath).Where(File.Exists).ToArray();
        var results = _audioEngine.LoadTracks(paths);
        var loaded = results.Where(result => result.IsLoaded).ToArray();

        ClearTracks();
        for (var i = 0; i < loaded.Length; i++)
        {
            var savedTrack = document.Tracks.FirstOrDefault(track => string.Equals(track.FilePath, loaded[i].FilePath, StringComparison.OrdinalIgnoreCase));
            var track = new TrackChannelViewModel(i + 1, savedTrack?.Name ?? loaded[i].Name, 0, loaded[i].FilePath)
            {
                Volume = savedTrack?.Volume ?? 1,
                MasterBus = savedTrack?.MasterBus ?? DetermineDefaultMasterBus(savedTrack?.Name ?? loaded[i].Name),
                IsMuted = savedTrack?.IsMuted ?? false,
                IsSolo = savedTrack?.IsSolo ?? false
            };
            SubscribeTrack(track);
            Tracks.Add(track);
        }

        SessionRegions.Clear();
        foreach (var session in document.Sessions)
        {
            SessionRegions.Add(new SessionRegionViewModel(session.Name, session.StartMeasure, session.EndMeasure, session.Color)
            {
                IsLooping = session.IsLooping
            });
        }

        CurrentProjectPath = projectPath;
        OnPropertyChanged(nameof(TrackCount));
        ApplyMixerStates();
        ApplyPlaybackTempo();
        _audioEngine.SetPitch(PitchOffset, GetPitchExemptTrackIndices());
        UpdateWaveformPeaks();
        _durationSeconds = Math.Max(0, _audioEngine.DurationSeconds);
        UpdateClockFields(0);
        StatusMessage = statusMessage;
        _isRestoringProjectTab = false;

        if (updateActiveTab && ActiveProjectTab is not null)
        {
            SaveStateToProjectTab(ActiveProjectTab);
        }
    }

    private void SaveActiveProjectTab()
    {
        if (ActiveProjectTab is not null)
        {
            SaveStateToProjectTab(ActiveProjectTab);
        }
    }

    private void SaveStateToProjectTab(ProjectTabViewModel tab)
    {
        tab.Name = ProjectName;
        tab.ProjectPath = CurrentProjectPath;
        tab.Document = CreateProjectDocument();
    }

    private static VsmixerProjectDocument CreateEmptyProjectDocument(string name)
    {
        return new VsmixerProjectDocument
        {
            ProjectName = name
        };
    }

    [RelayCommand]
    private void TogglePlayback()
    {
        if (!HasImportedAudio && !MetronomeEnabled)
        {
            StatusMessage = "Importe uma track ou habilite o metronomo antes de tocar.";
            return;
        }

        IsPlaying = !IsPlaying;
        if (IsPlaying)
        {
            if (HasImportedAudio)
            {
                _audioEngine.Play();
                _activePlaybackSession = GetSessionAtPosition(_audioEngine.CurrentPositionSeconds);
            }

            StartMetronome(playFirstClick: MetronomeEnabled);
            StatusMessage = HasImportedAudio ? "Tocando." : "Metronomo tocando.";
        }
        else
        {
            _audioEngine.Pause();
            _audioEngine.StopGuideVoice();
            StopMetronome();
            StatusMessage = "Pausado.";
        }

        UpdateClockFields(_audioEngine.CurrentPositionSeconds);
    }

    [RelayCommand]
    private void Rewind()
    {
        IsPlaying = false;
        _audioEngine.Pause();
        _audioEngine.SeekToStart();
        _audioEngine.StopGuideVoice();
        StopMetronome();
        _lastGuideVoiceKey = null;
        ClearQueuedSession();
        _activePlaybackSession = null;
        UpdateClockFields(0);
    }

    [RelayCommand]
    private void RemoveTrack(TrackChannelViewModel track)
    {
        var trackIndex = Tracks.IndexOf(track);
        if (trackIndex >= 0)
        {
            _audioEngine.RemoveTrack(trackIndex);
        }

        track.PropertyChanged -= OnTrackPropertyChanged;
        Tracks.Remove(track);
        OnPropertyChanged(nameof(TrackCount));
        ApplyMixerStates();
        UpdateWaveformPeaks();
    }

    [RelayCommand]
    private void OpenAddSessionModal()
    {
        _editingSession = null;
        OnPropertyChanged(nameof(IsEditingSession));
        OnPropertyChanged(nameof(SessionModalTitle));
        OnPropertyChanged(nameof(SessionModalSubmitText));

        RefreshGuideVoiceFiles();
        RefreshSessionCopyOptions();

        if (SessionNameOptions.Count == 0)
        {
            SessionNameOptions.Add($"Sessao {SessionRegions.Count + 1}");
        }

        NewSessionName = SessionNameOptions.First();
        NewSessionStartMeasure = SessionRegions.Count == 0 ? 1 : SessionRegions.Max(session => session.EndMeasure) + 1;
        NewSessionEndMeasure = NewSessionStartMeasure + 15;
        SelectedSessionCopySource = SessionCopyOptions.FirstOrDefault();
        IsAddSessionModalOpen = true;
    }

    [RelayCommand]
    private void OpenEditSessionModal(SessionRegionViewModel session)
    {
        IsSessionActionsModalOpen = false;
        _editingSession = session;
        OnPropertyChanged(nameof(IsEditingSession));
        OnPropertyChanged(nameof(SessionModalTitle));
        OnPropertyChanged(nameof(SessionModalSubmitText));

        RefreshGuideVoiceFiles();
        RefreshSessionCopyOptions();

        if (!SessionNameOptions.Contains(session.Name))
        {
            SessionNameOptions.Insert(0, session.Name);
        }

        NewSessionName = session.Name;
        NewSessionStartMeasure = session.StartMeasure;
        NewSessionEndMeasure = session.EndMeasure;
        SelectedSessionCopySource = SessionCopyOptions.FirstOrDefault();
        IsAddSessionModalOpen = true;
    }

    [RelayCommand]
    private void CloseAddSessionModal()
    {
        IsAddSessionModalOpen = false;
        _editingSession = null;
        OnPropertyChanged(nameof(IsEditingSession));
        OnPropertyChanged(nameof(SessionModalTitle));
        OnPropertyChanged(nameof(SessionModalSubmitText));
    }

    [RelayCommand]
    private void OpenSessionActionsModal(SessionRegionViewModel session)
    {
        _selectedSessionForMenu = session;
        OnPropertyChanged(nameof(SessionActionsTitle));
        OnPropertyChanged(nameof(SessionLoopActionText));
        IsSessionActionsModalOpen = true;
    }

    [RelayCommand]
    private void CloseSessionActionsModal()
    {
        IsSessionActionsModalOpen = false;
        _selectedSessionForMenu = null;
        OnPropertyChanged(nameof(SessionActionsTitle));
        OnPropertyChanged(nameof(SessionLoopActionText));
    }

    [RelayCommand]
    private void EditSelectedSession()
    {
        if (_selectedSessionForMenu is null)
        {
            return;
        }

        var session = _selectedSessionForMenu;
        CloseSessionActionsModal();
        OpenEditSessionModal(session);
    }

    [RelayCommand]
    private void ToggleSelectedSessionLoop()
    {
        if (_selectedSessionForMenu is null)
        {
            return;
        }

        foreach (var session in SessionRegions)
        {
            if (!ReferenceEquals(session, _selectedSessionForMenu))
            {
                session.IsLooping = false;
            }
        }

        _selectedSessionForMenu.IsLooping = !_selectedSessionForMenu.IsLooping;
        OnPropertyChanged(nameof(SessionLoopActionText));
        SaveActiveProjectTab();
    }

    [RelayCommand]
    private void DuplicateSelectedSession()
    {
        if (_selectedSessionForMenu is null)
        {
            return;
        }

        var source = _selectedSessionForMenu;
        var length = Math.Max(1, source.EndMeasure - source.StartMeasure + 1);
        var start = SessionRegions.Count == 0 ? 1 : SessionRegions.Max(session => session.EndMeasure) + 1;
        var end = start + length - 1;
        var color = _sectionColors[SessionRegions.Count % _sectionColors.Length];
        SessionRegions.Add(new SessionRegionViewModel(source.Name, start, end, color));
        StatusMessage = $"Sessao duplicada no fim: {source.Name}.";
        CloseSessionActionsModal();
        SaveActiveProjectTab();
    }

    [RelayCommand]
    private void DeleteSelectedSession()
    {
        if (_selectedSessionForMenu is null)
        {
            return;
        }

        var session = _selectedSessionForMenu;
        if (ReferenceEquals(_queuedSession, session))
        {
            ClearQueuedSession();
        }

        if (ReferenceEquals(_activePlaybackSession, session))
        {
            _activePlaybackSession = null;
        }

        SessionRegions.Remove(session);
        CloseSessionActionsModal();
        SaveActiveProjectTab();
    }

    [RelayCommand]
    private void QueueOrStartSession(SessionRegionViewModel session)
    {
        if (!HasImportedAudio)
        {
            StatusMessage = "Importe pelo menos uma track antes de iniciar uma sessao.";
            return;
        }

        if (ReferenceEquals(_queuedSession, session))
        {
            ClearQueuedSession();
            StatusMessage = $"Agendamento cancelado: {session.Name}.";
            return;
        }

        var currentSession = _activePlaybackSession ?? GetSessionAtPosition(_audioEngine.CurrentPositionSeconds);
        if (IsPlaying && currentSession is not null && !ReferenceEquals(currentSession, session))
        {
            SetQueuedSession(session);
            StatusMessage = $"Sessao agendada: {session.Name}.";
            return;
        }

        StartSessionNow(session);
    }

    [RelayCommand]
    private void OpenAudioSettingsModal()
    {
        RefreshDeviceLists();
        IsAudioSettingsModalOpen = true;
    }

    [RelayCommand]
    private void CloseAudioSettingsModal()
    {
        IsAudioSettingsModalOpen = false;
    }

    [RelayCommand]
    private void ToggleMidiMappingEditMode()
    {
        IsMidiMappingEditMode = !IsMidiMappingEditMode;
    }

    [RelayCommand]
    private void OpenMidiMappingModal(string target)
    {
        if (!TryParseMidiMapTarget(target, out var parsedTarget))
        {
            return;
        }

        _pendingMidiMapTarget = parsedTarget;
        OnPropertyChanged(nameof(MidiMappingModalTitle));
        OnPropertyChanged(nameof(MidiMappingModalInstruction));
        OnPropertyChanged(nameof(MidiMappingModalCurrent));
        IsMidiMappingModalOpen = true;
        RefreshDeviceLists();

        if (!IsMidiControllerEnabled)
        {
            IsMidiControllerEnabled = true;
        }
        else
        {
            ApplyMidiControllerState();
        }

        StatusMessage = $"Aguardando MIDI para {GetMidiMapTargetDisplayName(parsedTarget)}.";
    }

    [RelayCommand]
    private void CloseMidiMappingModal()
    {
        IsMidiMappingModalOpen = false;
        _pendingMidiMapTarget = null;
        OnPropertyChanged(nameof(MidiMappingModalTitle));
        OnPropertyChanged(nameof(MidiMappingModalInstruction));
        OnPropertyChanged(nameof(MidiMappingModalCurrent));
    }

    [RelayCommand]
    private void ClearMidiMapping()
    {
        if (_pendingMidiMapTarget is not { } target)
        {
            return;
        }

        SetMidiMapping(target, null);
        SaveActiveProjectTab();
        OnPropertyChanged(nameof(MidiMappingModalCurrent));
        StatusMessage = $"Mapeamento removido: {GetMidiMapTargetDisplayName(target)}.";
    }

    [RelayCommand]
    private void AddSession()
    {
        var name = string.IsNullOrWhiteSpace(NewSessionName) ? $"Sessao {SessionRegions.Count + 1}" : NewSessionName.Trim();
        var start = Math.Max(1, Math.Min(NewSessionStartMeasure, NewSessionEndMeasure));
        var end = Math.Max(start, Math.Max(NewSessionStartMeasure, NewSessionEndMeasure));

        if (_editingSession is not null)
        {
            _editingSession.Name = name;
            _editingSession.StartMeasure = start;
            _editingSession.EndMeasure = end;
            _editingSession = null;
            OnPropertyChanged(nameof(IsEditingSession));
            OnPropertyChanged(nameof(SessionModalTitle));
            OnPropertyChanged(nameof(SessionModalSubmitText));
        }
        else
        {
            var color = _sectionColors[SessionRegions.Count % _sectionColors.Length];
            SessionRegions.Add(new SessionRegionViewModel(name, start, end, color));
        }

        IsAddSessionModalOpen = false;
        SaveActiveProjectTab();
    }

    [RelayCommand]
    private void ChangeTempo(string amount)
    {
        if (!int.TryParse(amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var step))
        {
            return;
        }

        PlaybackBpm = Math.Clamp(PlaybackBpm + step, 1, 300);
    }

    [RelayCommand]
    private void ChangePitch(string amount)
    {
        if (!int.TryParse(amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var step))
        {
            return;
        }

        PitchOffset = Math.Clamp(PitchOffset + step, -12, 12);
        StatusMessage = PitchOffset == 0
            ? "Pitch original."
            : $"Pitch alterado: {PitchOffset:+#;-#;0} semitons.";
    }

    private void SubscribeTrack(TrackChannelViewModel track)
    {
        track.PropertyChanged += OnTrackPropertyChanged;
    }

    private void ClearTracks()
    {
        foreach (var track in Tracks)
        {
            track.PropertyChanged -= OnTrackPropertyChanged;
        }

        Tracks.Clear();
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackChannelViewModel.Volume)
            or nameof(TrackChannelViewModel.MasterBus)
            or nameof(TrackChannelViewModel.IsMuted)
            or nameof(TrackChannelViewModel.IsSolo))
        {
            ApplyMixerStates();
        }
    }

    private void ApplyMixerStates()
    {
        var anySolo = Tracks.Any(track => track.IsSolo);

        for (var i = 0; i < Tracks.Count; i++)
        {
            var track = Tracks[i];
            var effectiveMuted = track.IsMuted || (anySolo && !track.IsSolo);
            var busVolume = track.MasterBus == MasterBus.A ? MasterAVolume : MasterBVolume;
            var pan = track.MasterBus == MasterBus.A ? -1d : 1d;
            _audioEngine.SetTrackState(i, track.Volume * busVolume, pan, effectiveMuted);
        }
    }

    private void UpdateScheduledSessionPlayback(double positionSeconds)
    {
        if (!IsPlaying || SessionRegions.Count == 0)
        {
            return;
        }

        _activePlaybackSession ??= GetSessionAtPosition(positionSeconds);
        if (_activePlaybackSession is null)
        {
            return;
        }

        var nextSectionStartSeconds = GetMeasureStartSeconds(_activePlaybackSession.EndMeasure + 1);
        if (positionSeconds < nextSectionStartSeconds - 0.035)
        {
            return;
        }

        if (_queuedSession is not null)
        {
            var queued = _queuedSession;
            ClearQueuedSession();
            StartSessionNow(queued, keepQueuedSession: true);
            return;
        }

        if (_activePlaybackSession.IsLooping)
        {
            StartSessionNow(_activePlaybackSession, keepQueuedSession: true);
            return;
        }

        _activePlaybackSession = GetSessionAtPosition(positionSeconds);
    }

    private void StartSessionNow(SessionRegionViewModel session, bool keepQueuedSession = false)
    {
        var targetSeconds = GetMeasureStartSeconds(session.StartMeasure);
        _audioEngine.Seek(targetSeconds);
        _audioEngine.StopGuideVoice();
        _lastGuideVoiceKey = null;
        _activePlaybackSession = session;

        if (!keepQueuedSession)
        {
            ClearQueuedSession();
        }

        if (!IsPlaying)
        {
            IsPlaying = true;
            _audioEngine.Play();
        }

        if (MetronomeEnabled)
        {
            StartMetronome(playFirstClick: true);
        }

        UpdateClockFields(targetSeconds);
        StatusMessage = $"Tocando sessao: {session.Name}.";
    }

    private void SetQueuedSession(SessionRegionViewModel session)
    {
        ClearQueuedSession();
        _queuedSession = session;
        _queuedSession.IsQueued = true;
    }

    private void ClearQueuedSession()
    {
        if (_queuedSession is not null)
        {
            _queuedSession.IsQueued = false;
        }

        _queuedSession = null;
    }

    private void UpdateTransport()
    {
        var position = HasImportedAudio ? _audioEngine.CurrentPositionSeconds : 0;
        UpdateScheduledSessionPlayback(position);
        position = HasImportedAudio ? _audioEngine.CurrentPositionSeconds : 0;
        UpdateClockFields(position);
        UpdateTrackMeters();
        UpdateGuideVoice(position);
        UpdatePerformanceUsage();

        if (IsPlaying && _durationSeconds > 0 && position >= _durationSeconds - 0.05)
        {
            IsPlaying = false;
            _audioEngine.StopGuideVoice();
            StopMetronome();
            _lastGuideVoiceKey = null;
            ClearQueuedSession();
            _activePlaybackSession = null;
            StatusMessage = "Fim da reproducao.";
        }
    }

    private void UpdatePerformanceUsage()
    {
        var now = DateTime.UtcNow;
        if (now < _nextPerformanceSampleTime)
        {
            return;
        }

        _currentProcess.Refresh();

        var cpuTime = _currentProcess.TotalProcessorTime;
        var elapsedSeconds = Math.Max((now - _lastPerformanceSampleTime).TotalSeconds, 0.001);
        var cpuSeconds = Math.Max((cpuTime - _lastPerformanceSampleCpuTime).TotalSeconds, 0);
        var cpuPercent = cpuSeconds / elapsedSeconds / Math.Max(1, Environment.ProcessorCount) * 100d;
        var memoryMb = _currentProcess.WorkingSet64 / 1024d / 1024d;

        CpuUsageText = $"CPU {Math.Clamp(cpuPercent, 0, 100):0}%";
        MemoryUsageText = $"RAM {memoryMb:0} MB";

        _lastPerformanceSampleTime = now;
        _lastPerformanceSampleCpuTime = cpuTime;
        _nextPerformanceSampleTime = now.AddSeconds(1);
    }

    private void UpdateTrackMeters()
    {
        var anySolo = Tracks.Any(track => track.IsSolo);

        for (var i = 0; i < Tracks.Count; i++)
        {
            var track = Tracks[i];
            var effectiveMuted = track.IsMuted || (anySolo && !track.IsSolo);
            var target = IsPlaying && !effectiveMuted
                ? _audioEngine.GetTrackLevel(i) * track.Volume
                : 0;

            track.Level = target > track.Level
                ? target
                : track.Level * 0.72;
        }
    }

    private void UpdateWaveformPeaks()
    {
        WaveformPeaks.Clear();

        foreach (var peak in _audioEngine.GetSummedWaveform(360))
        {
            WaveformPeaks.Add(peak);
        }
    }

    private void ApplyPlaybackTempo()
    {
        var ratio = PlaybackBpm / (double)Math.Max(1, DetectedBpm);
        _audioEngine.SetTempo(ratio);
    }

    private void ApplyPadState()
    {
        if (!_padFiles.TryGetValue(SelectedPadNote, out var fileName))
        {
            StatusMessage = "Nota do pad invalida.";
            return;
        }

        var padPath = ResolveAssetPath("Assets", "Pads-Continuos", fileName);
        if (!_audioEngine.PlayPad(padPath))
        {
            StatusMessage = _audioEngine.LastError ?? $"Nao foi possivel tocar o pad {SelectedPadNote}.";
            return;
        }

        StatusMessage = $"Pad ligado: {SelectedPadNote}.";
    }

    private void UpdateGuideVoice(double positionSeconds)
    {
        if (!IsPlaying || !GuideVoiceEnabled || SessionRegions.Count == 0)
        {
            return;
        }

        var measure = GetMeasureAtPosition(positionSeconds);
        var session = SessionRegions
            .OrderBy(region => region.StartMeasure)
            .FirstOrDefault(region => measure >= region.StartMeasure && measure <= region.EndMeasure);
        if (session is null)
        {
            return;
        }

        var guideKey = $"{session.StartMeasure}:{NormalizeGuideKey(session.Name)}";
        if (string.Equals(_lastGuideVoiceKey, guideKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastGuideVoiceKey = guideKey;
        if (!TryGetGuideVoicePath(session.Name, out var guidePath))
        {
            return;
        }

        if (!_audioEngine.PlayGuideVoice(guidePath))
        {
            StatusMessage = _audioEngine.LastError ?? $"Nao foi possivel tocar a voz guia: {session.Name}.";
        }
    }

    private bool TryGetGuideVoicePath(string sessionName, out string guidePath)
    {
        if (_guideVoiceFiles.Count == 0)
        {
            RefreshGuideVoiceFiles();
        }

        return _guideVoiceFiles.TryGetValue(NormalizeGuideKey(sessionName), out guidePath!);
    }

    private void RefreshGuideVoiceFiles()
    {
        _guideVoiceFiles.Clear();
        SessionNameOptions.Clear();
        var guideDirectory = ResolveAssetDirectory("Assets", "Voz-Guia");
        if (!Directory.Exists(guideDirectory))
        {
            return;
        }

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in Directory.EnumerateFiles(guideDirectory, "*.mp3"))
        {
            var stem = Path.GetFileNameWithoutExtension(filePath);
            AddGuideVoiceKey(stem, filePath);
            AddGuideVoiceKey(stem.Replace('_', '-'), filePath);
            AddGuideVoiceKey(stem.Replace('-', '_'), filePath);
            names.Add(HumanizeGuideVoiceName(stem));
        }

        foreach (var name in names)
        {
            SessionNameOptions.Add(name);
        }
    }

    private void AddGuideVoiceKey(string key, string filePath)
    {
        var normalized = NormalizeGuideKey(key);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            _guideVoiceFiles[normalized] = filePath;
        }
    }

    private void RefreshSessionCopyOptions()
    {
        SessionCopyOptions.Clear();
        SessionCopyOptions.Add(new SessionCopyOptionViewModel("Nao copiar", 0, isNone: true));

        foreach (var session in SessionRegions.OrderBy(session => session.StartMeasure))
        {
            var length = Math.Max(1, session.EndMeasure - session.StartMeasure + 1);
            SessionCopyOptions.Add(new SessionCopyOptionViewModel(session.Name, length));
        }
    }

    private void ApplyCopiedSessionLength()
    {
        if (SelectedSessionCopySource is null || SelectedSessionCopySource.IsNone)
        {
            return;
        }

        var start = Math.Max(1, NewSessionStartMeasure);
        NewSessionEndMeasure = start + SelectedSessionCopySource.LengthMeasures - 1;
    }

    private static IReadOnlyList<string> OrderTrackFilePaths(IReadOnlyList<string> filePaths)
    {
        return filePaths
            .OrderBy(path => GetTrackPriorityRank(Path.GetFileNameWithoutExtension(path)))
            .ThenBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.InvariantCultureIgnoreCase)
            .ToArray();
    }

    private static int GetTrackPriorityRank(string trackName)
    {
        var name = trackName.ToLowerInvariant();
        if (name.Contains("click", StringComparison.Ordinal) || name.Contains("clique", StringComparison.Ordinal))
        {
            return 0;
        }

        if (name.Contains("guia", StringComparison.Ordinal) || name.Contains("guide", StringComparison.Ordinal))
        {
            return 1;
        }

        return 2;
    }

    private static MasterBus DetermineDefaultMasterBus(string trackName)
    {
        return GetTrackPriorityRank(trackName) < 2 ? MasterBus.A : MasterBus.B;
    }

    private HashSet<int> GetPitchExemptTrackIndices()
    {
        var exemptIndices = new HashSet<int>();
        for (var i = 0; i < Tracks.Count; i++)
        {
            if (IsPitchExemptTrack(Tracks[i].Name))
            {
                exemptIndices.Add(i);
            }
        }

        return exemptIndices;
    }

    private static bool IsPitchExemptTrack(string trackName)
    {
        if (GetTrackPriorityRank(trackName) < 2)
        {
            return true;
        }

        var name = trackName.ToLowerInvariant();
        return name.Contains("bateria", StringComparison.Ordinal)
            || name.Contains("drum", StringComparison.Ordinal)
            || name.Contains("drums", StringComparison.Ordinal)
            || name.Contains("drummer", StringComparison.Ordinal)
            || name.Contains("percussao", StringComparison.Ordinal)
            || name.Contains("percussão", StringComparison.Ordinal);
    }

    private static bool TryParseBpm(string value, out int bpm)
    {
        bpm = 0;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        bpm = Math.Clamp(parsed, 1, 300);
        return true;
    }

    private void SyncBpmText(string propertyName, string currentValue, int bpm)
    {
        var nextValue = bpm.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(currentValue, nextValue, StringComparison.Ordinal))
        {
            return;
        }

        if (propertyName == nameof(DetectedBpmText))
        {
            DetectedBpmText = nextValue;
            return;
        }

        PlaybackBpmText = nextValue;
    }

    private static string ResolveAssetPath(params string[] parts)
    {
        var outputParts = new string[parts.Length + 1];
        outputParts[0] = AppContext.BaseDirectory;
        parts.CopyTo(outputParts, 1);
        var outputPath = Path.Combine(outputParts);
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidateParts = new string[parts.Length + 1];
            candidateParts[0] = directory.FullName;
            parts.CopyTo(candidateParts, 1);
            var candidate = Path.Combine(candidateParts);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return outputPath;
    }

    private static string ResolveAssetDirectory(params string[] parts)
    {
        var outputParts = new string[parts.Length + 1];
        outputParts[0] = AppContext.BaseDirectory;
        parts.CopyTo(outputParts, 1);
        var outputPath = Path.Combine(outputParts);
        if (Directory.Exists(outputPath))
        {
            return outputPath;
        }

        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidateParts = new string[parts.Length + 1];
            candidateParts[0] = directory.FullName;
            parts.CopyTo(candidateParts, 1);
            var candidate = Path.Combine(candidateParts);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return outputPath;
    }

    private static string NormalizeGuideKey(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var previousWasSeparator = false;

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
                continue;
            }

            if ((char.IsWhiteSpace(character) || character is '-' or '_') && !previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string HumanizeGuideVoiceName(string value)
    {
        var normalized = value.Replace('_', ' ').Replace('-', ' ').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return value;
        }

        var words = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.Length == 1
                ? word.ToUpperInvariant()
                : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant());

        return string.Join(' ', words);
    }

    private void StartMetronome(bool playFirstClick)
    {
        _metronomeBeatIndex = 0;
        _metronomeClock.Restart();

        if (playFirstClick)
        {
            _audioEngine.PlayMetronomeClick(isAccent: true);
            _metronomeBeatIndex = 1;
            _nextMetronomeBeatSeconds = GetBeatIntervalSeconds();
            return;
        }

        _nextMetronomeBeatSeconds = 0;
    }

    private void StopMetronome()
    {
        _metronomeClock.Reset();
        _nextMetronomeBeatSeconds = 0;
        _metronomeBeatIndex = 0;
    }

    private void RescheduleMetronome()
    {
        if (!IsPlaying || !MetronomeEnabled || !_metronomeClock.IsRunning)
        {
            return;
        }

        _nextMetronomeBeatSeconds = _metronomeClock.Elapsed.TotalSeconds + GetBeatIntervalSeconds();
    }

    private void UpdateMetronome()
    {
        if (!IsPlaying || !MetronomeEnabled)
        {
            return;
        }

        if (!_metronomeClock.IsRunning)
        {
            StartMetronome(playFirstClick: true);
            return;
        }

        var interval = GetBeatIntervalSeconds();
        var elapsed = _metronomeClock.Elapsed.TotalSeconds;
        var guard = 0;
        while (elapsed + 0.015 >= _nextMetronomeBeatSeconds && guard < 3)
        {
            _audioEngine.PlayMetronomeClick(IsAccentBeat(_metronomeBeatIndex));
            _metronomeBeatIndex++;
            _nextMetronomeBeatSeconds += interval;
            guard++;
        }
    }

    private bool IsAccentBeat(int beatIndex)
    {
        var beatPosition = beatIndex * GetGridBeatLength();
        var measureRemainder = beatPosition % GetBeatsPerMeasure();
        return Math.Abs(measureRemainder) < 0.0001 || Math.Abs(measureRemainder - GetBeatsPerMeasure()) < 0.0001;
    }

    private int GetMeasureAtPosition(double positionSeconds)
    {
        var bpm = Math.Max(1, DetectedBpm);
        var totalBeats = Math.Max(0, positionSeconds) * bpm / 60d;
        return (int)Math.Floor(totalBeats / GetBeatsPerMeasure()) + 1;
    }

    private double GetMeasureStartSeconds(int measure)
    {
        var bpm = Math.Max(1, DetectedBpm);
        var beatsBeforeMeasure = Math.Max(0, measure - 1) * GetBeatsPerMeasure();
        return beatsBeforeMeasure * 60d / bpm;
    }

    private SessionRegionViewModel? GetSessionAtPosition(double positionSeconds)
    {
        var measure = GetMeasureAtPosition(positionSeconds);
        return SessionRegions
            .OrderBy(region => region.StartMeasure)
            .FirstOrDefault(region => measure >= region.StartMeasure && measure <= region.EndMeasure);
    }

    private double GetBeatIntervalSeconds()
    {
        return 60d / Math.Clamp(PlaybackBpm, 1, 300) * GetGridBeatLength();
    }

    private double GetGridBeatLength()
    {
        var denominator = SelectedGrid switch
        {
            "1/2" => 2,
            "1/8" => 8,
            "1/16" => 16,
            _ => 4
        };

        return 4d / denominator;
    }

    private int GetBeatsPerMeasure()
    {
        return SelectedTimeSignature == "3/4" ? 3 : 4;
    }

    private VsmixerProjectDocument CreateProjectDocument()
    {
        return new VsmixerProjectDocument
        {
            ProjectName = ProjectName,
            DetectedBpm = DetectedBpm,
            PlaybackBpm = PlaybackBpm,
            PitchOffset = PitchOffset,
            SelectedTimeSignature = SelectedTimeSignature,
            SelectedGrid = SelectedGrid,
            MasterAVolume = MasterAVolume,
            MasterBVolume = MasterBVolume,
            MetronomeEnabled = MetronomeEnabled,
            MetronomeVolume = MetronomeVolume,
            MetronomePan = MetronomePan,
            GuideVoiceEnabled = GuideVoiceEnabled,
            GuideVoiceVolume = GuideVoiceVolume,
            GuideVoicePan = GuideVoicePan,
            PadContinuousEnabled = PadContinuousEnabled,
            SelectedPadNote = SelectedPadNote,
            AudioOutputDeviceId = SelectedAudioOutputDevice?.Id ?? -1,
            AudioOutputDeviceName = SelectedAudioOutputDevice?.Name,
            MidiControllerEnabled = IsMidiControllerEnabled,
            MidiControllerName = SelectedMidiController?.IsPlaceholder == false ? SelectedMidiController.Name : null,
            MidiControlBank = SelectedMidiControlBank,
            PlayMidiMapping = PlayMidiMapping,
            RewindMidiMapping = RewindMidiMapping,
            MasterAMidiMapping = MasterAMidiMapping,
            MasterBMidiMapping = MasterBMidiMapping,
            Tracks = Tracks
                .Where(track => !string.IsNullOrWhiteSpace(track.FilePath))
                .Select(track => new ProjectTrackDocument
                {
                    Name = track.Name,
                    FilePath = track.FilePath!,
                    Volume = track.Volume,
                    MasterBus = track.MasterBus,
                    IsMuted = track.IsMuted,
                    IsSolo = track.IsSolo
                })
                .ToList(),
            Sessions = SessionRegions
                .Select(session => new ProjectSessionDocument
                {
                    Name = session.Name,
                    StartMeasure = session.StartMeasure,
                    EndMeasure = session.EndMeasure,
                    Color = session.Color,
                    IsLooping = session.IsLooping
                })
                .ToList()
        };
    }

    private void OnMidiMessageReceived(object? sender, MidiMessageReceivedEventArgs e)
    {
        _ = sender;

        Dispatcher.UIThread.Post(() => HandleMidiMessage(e));
    }

    private void HandleMidiMessage(MidiMessageReceivedEventArgs e)
    {
        if (_pendingMidiMapTarget is { } target)
        {
            CaptureMidiMapping(target, e);
            return;
        }

        if (MatchesMidiMapping(PlayMidiMapping, e) && IsClickMessage(e))
        {
            TogglePlayback();
            return;
        }

        if (MatchesMidiMapping(RewindMidiMapping, e) && IsClickMessage(e))
        {
            Rewind();
            return;
        }

        if (MatchesMidiMapping(MasterAMidiMapping, e) && e.Kind == MidiMessageKind.ControlChange)
        {
            MasterAVolume = MidiValueToVolume(e.Value);
            SaveActiveProjectTab();
            return;
        }

        if (MatchesMidiMapping(MasterBMidiMapping, e) && e.Kind == MidiMessageKind.ControlChange)
        {
            MasterBVolume = MidiValueToVolume(e.Value);
            SaveActiveProjectTab();
        }
    }

    private void CaptureMidiMapping(MidiMapTarget target, MidiMessageReceivedEventArgs e)
    {
        if ((target is MidiMapTarget.MasterA or MidiMapTarget.MasterB) && e.Kind != MidiMessageKind.ControlChange)
        {
            StatusMessage = "Master A/B precisa de um fader ou knob MIDI CC.";
            return;
        }

        if ((target is MidiMapTarget.Play or MidiMapTarget.Rewind) && !IsClickMessage(e))
        {
            return;
        }

        var mapping = new MidiMapping
        {
            Kind = e.Kind,
            Channel = e.Channel,
            Number = e.Number
        };

        SetMidiMapping(target, mapping);
        SaveActiveProjectTab();
        StatusMessage = $"{GetMidiMapTargetDisplayName(target)} mapeado para {mapping.DisplayName}.";
        CloseMidiMappingModal();
    }

    private MidiMapping? GetMidiMapping(MidiMapTarget target)
    {
        return target switch
        {
            MidiMapTarget.Play => PlayMidiMapping,
            MidiMapTarget.Rewind => RewindMidiMapping,
            MidiMapTarget.MasterA => MasterAMidiMapping,
            MidiMapTarget.MasterB => MasterBMidiMapping,
            _ => null
        };
    }

    private void SetMidiMapping(MidiMapTarget target, MidiMapping? mapping)
    {
        switch (target)
        {
            case MidiMapTarget.Play:
                PlayMidiMapping = mapping;
                break;
            case MidiMapTarget.Rewind:
                RewindMidiMapping = mapping;
                break;
            case MidiMapTarget.MasterA:
                MasterAMidiMapping = mapping;
                break;
            case MidiMapTarget.MasterB:
                MasterBMidiMapping = mapping;
                break;
        }
    }

    private static bool TryParseMidiMapTarget(string target, out MidiMapTarget parsedTarget)
    {
        return Enum.TryParse(target, ignoreCase: true, out parsedTarget);
    }

    private static bool MatchesMidiMapping(MidiMapping? mapping, MidiMessageReceivedEventArgs e)
    {
        return mapping is not null
            && mapping.Kind == e.Kind
            && mapping.Channel == e.Channel
            && mapping.Number == e.Number;
    }

    private static bool IsClickMessage(MidiMessageReceivedEventArgs e)
    {
        return e.Value > 0;
    }

    private static double MidiValueToVolume(int value)
    {
        return Math.Clamp(value, 0, 127) / 127d;
    }

    private static string FormatMidiMappingLabel(MidiMapping? mapping)
    {
        return mapping is null ? "Mapear MIDI" : mapping.DisplayName;
    }

    private static string GetMidiMapTargetDisplayName(MidiMapTarget target)
    {
        return target switch
        {
            MidiMapTarget.Play => "Play",
            MidiMapTarget.Rewind => "Voltar",
            MidiMapTarget.MasterA => "Volume Master A",
            MidiMapTarget.MasterB => "Volume Master B",
            _ => "MIDI"
        };
    }

    private void RefreshDeviceLists()
    {
        var currentAudioDeviceId = SelectedAudioOutputDevice?.Id ?? -1;
        var currentMidiDeviceName = SelectedMidiController?.Name;

        AudioOutputDevices.Clear();
        MidiControllerDevices.Clear();

        foreach (var device in _audioEngine.GetOutputDevices())
        {
            var detail = device.Id == -1
                ? "Saida"
                : device.IsDefault
                    ? "Saida padrao"
                    : "Saida";
            AudioOutputDevices.Add(new DeviceOptionViewModel(device.Name, detail, device.Id, !device.IsEnabled));
        }

        try
        {
            foreach (var device in InputDevice.GetAll())
            {
                using (device)
                {
                    MidiControllerDevices.Add(new DeviceOptionViewModel(device.Name, "Controlador MIDI"));
                }
            }
        }
        catch (Exception ex)
        {
            MidiControllerDevices.Add(new DeviceOptionViewModel($"Erro ao listar MIDI: {ex.Message}", string.Empty, 0, true));
        }

        if (AudioOutputDevices.Count == 0)
        {
            AudioOutputDevices.Add(new DeviceOptionViewModel("Nenhuma saida de audio encontrada", string.Empty, -999, true));
        }

        if (MidiControllerDevices.Count == 0)
        {
            MidiControllerDevices.Add(new DeviceOptionViewModel("Nenhum controlador MIDI encontrado", string.Empty, 0, true));
        }

        SelectedAudioOutputDevice = AudioOutputDevices.FirstOrDefault(device => device.Id == currentAudioDeviceId)
            ?? AudioOutputDevices.FirstOrDefault(device => !device.IsPlaceholder);
        SelectedMidiController = MidiControllerDevices.FirstOrDefault(device => string.Equals(device.Name, currentMidiDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? MidiControllerDevices.FirstOrDefault(device => !device.IsPlaceholder);
    }

    private void ApplyMidiControllerState()
    {
        if (!IsMidiControllerEnabled)
        {
            _midiControlService.StopListening();
            StatusMessage = "Controlador MIDI desabilitado.";
            return;
        }

        if (SelectedMidiController is null || SelectedMidiController.IsPlaceholder)
        {
            _midiControlService.StopListening();
            StatusMessage = "Nenhum controlador MIDI disponivel.";
            return;
        }

        try
        {
            _midiControlService.StartListening(SelectedMidiController.Name);
            StatusMessage = $"MIDI habilitado: {SelectedMidiController.Name} controlando {SelectedMidiControlBank}.";
        }
        catch (Exception ex)
        {
            _midiControlService.StopListening();
            StatusMessage = $"Nao foi possivel habilitar MIDI: {ex.Message}";
        }
    }

    private void UpdateClockFields(double positionSeconds)
    {
        CurrentTime = FormatTime(positionSeconds);
        PlaybackProgress = _durationSeconds <= 0 ? 0 : Math.Clamp(positionSeconds / _durationSeconds, 0, 1);
        BarClock = FormatBarClock(positionSeconds);
    }

    private string FormatBarClock(double positionSeconds)
    {
        var bpm = Math.Max(1, DetectedBpm);
        var beatsPerMeasure = GetBeatsPerMeasure();
        var totalBeats = positionSeconds * bpm / 60d;
        var wholeBeat = Math.Max(0, (int)Math.Floor(totalBeats));
        var measure = wholeBeat / beatsPerMeasure + 1;
        var beat = wholeBeat % beatsPerMeasure + 1;
        var tick = (int)Math.Floor((totalBeats - wholeBeat) * 100) + 1;
        return $"{measure}.{beat}.{tick:00}";
    }

    private static string FormatTime(double seconds)
    {
        var safeSeconds = Math.Max(0, seconds);
        var minutes = (int)(safeSeconds / 60);
        var remainingSeconds = (int)(safeSeconds % 60);
        return $"{minutes}:{remainingSeconds:00}";
    }
}
