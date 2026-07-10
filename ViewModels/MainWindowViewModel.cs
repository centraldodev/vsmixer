using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    public event Action<ProjectTabViewModel>? DirtyTabCloseRequested;

    private static readonly HashSet<string> PersistedPropertyNames =
    [
        nameof(ProjectName),
        nameof(DetectedBpm),
        nameof(PlaybackBpm),
        nameof(BeatGridOffsetSeconds),
        nameof(PitchOffset),
        nameof(SelectedTimeSignature),
        nameof(SelectedGrid),
        nameof(MasterAVolume),
        nameof(MasterBVolume),
        nameof(MetronomeEnabled),
        nameof(MetronomeVolume),
        nameof(MetronomePan),
        nameof(GuideVoiceEnabled),
        nameof(GuideVoiceVolume),
        nameof(GuideVoicePan),
        nameof(PreCountEnabled),
        nameof(PreCountMeasures),
        nameof(PadContinuousEnabled),
        nameof(SelectedPadNote),
        nameof(SelectedAudioOutputDevice),
        nameof(IsMidiControllerEnabled),
        nameof(SelectedMidiController),
        nameof(SelectedMidiControlBank),
        nameof(PlayMidiMapping),
        nameof(RewindMidiMapping),
        nameof(MasterAMidiMapping),
        nameof(MasterBMidiMapping)
    ];

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
    private readonly IProjectFileService _projectFileService;
    private readonly ProjectRecoveryService _recoveryService;
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private SessionRegionViewModel? _editingSession;
    private SessionRegionViewModel? _queuedSession;
    private SessionRegionViewModel? _activePlaybackSession;
    private SessionRegionViewModel? _selectedSessionForMenu;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _recoveryTimer;
    private readonly MetronomeScheduler _metronomeScheduler;
    private readonly Stopwatch _virtualClock = new();
    private double _virtualPositionBaseSeconds;
    private double _virtualClockRatio = 1;
    private int _metronomeStepsPerMeasure = 4;
    private DispatcherTimer? _countInTimer;
    private double _durationSeconds;
    private string? _lastGuideVoiceKey;
    private bool _isRestoringProjectTab;
    private MidiMapTarget? _pendingMidiMapTarget;
    private DateTime _lastPerformanceSampleTime = DateTime.UtcNow;
    private TimeSpan _lastPerformanceSampleCpuTime;
    private DateTime _nextPerformanceSampleTime = DateTime.MinValue;
    private bool _disposed;
    private CancellationTokenSource? _operationCancellation;
    private bool _discardRecoveryOnExit;

    public MainWindowViewModel()
        : this(new BassAudioEngine(), new DryWetMidiControlService(), new ProjectFileService(), new ProjectRecoveryService())
    {
    }

    public MainWindowViewModel(IAudioEngine audioEngine, IMidiControlService midiControlService)
        : this(audioEngine, midiControlService, new ProjectFileService(), new ProjectRecoveryService())
    {
    }

    public MainWindowViewModel(
        IAudioEngine audioEngine,
        IMidiControlService midiControlService,
        IProjectFileService projectFileService)
        : this(audioEngine, midiControlService, projectFileService, new ProjectRecoveryService())
    {
    }

    public MainWindowViewModel(
        IAudioEngine audioEngine,
        IMidiControlService midiControlService,
        IProjectFileService projectFileService,
        ProjectRecoveryService recoveryService)
    {
        _audioEngine = audioEngine;
        _midiControlService = midiControlService;
        _projectFileService = projectFileService;
        _recoveryService = recoveryService;
        _metronomeScheduler = new MetronomeScheduler(OnMetronomeSchedulerBeat);
        _meterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(55)
        };
        _meterTimer.Tick += (_, _) => UpdateTransport();
        _meterTimer.Start();

        _recoveryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _recoveryTimer.Tick += (_, _) => SaveRecoverySnapshot();
        _recoveryTimer.Start();

        WaveformPeaks.Clear();
        RefreshGuideVoiceFiles();
        _midiControlService.MessageReceived += OnMidiMessageReceived;

        var initialTab = new ProjectTabViewModel(ProjectName, CurrentProjectPath, CreateProjectDocument())
        {
            IsActive = true
        };
        ProjectTabs.Add(initialTab);
        _activeProjectTab = initialTab;
        PropertyChanged += OnViewModelPropertyChanged;
        TryRestoreRecovery();
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
    private double _detectedBpm = 133;

    [ObservableProperty]
    private string _detectedBpmText = "133";

    [ObservableProperty]
    private double _playbackBpm = 133;

    [ObservableProperty]
    private string _playbackBpmText = "133";

    [ObservableProperty]
    private double _beatGridOffsetSeconds;

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
    private bool _preCountEnabled;

    [ObservableProperty]
    private int _preCountMeasures = 1;

    [ObservableProperty]
    private bool _isCountingIn;

    [ObservableProperty]
    private bool _newSessionPreCountEnabled;

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
    private bool _isBusy;

    [ObservableProperty]
    private double _operationProgress;

    [ObservableProperty]
    private string _operationStatus = string.Empty;

    [ObservableProperty]
    private string _tempoAnalysisSummary = "A grade será analisada ao importar as tracks.";

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
    private string _newSessionName = "Sessão";

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
    public bool HasUnsavedChanges => ProjectTabs.Any(tab => tab.IsDirty);
    public bool IsEditingSession => _editingSession is not null;
    public string SessionModalTitle => IsEditingSession ? "Editar sessão" : "Nova sessão";
    public string SessionModalSubmitText => IsEditingSession ? "Salvar" : "Adicionar";
    public string SessionActionsTitle => _selectedSessionForMenu is null ? "Sessão" : _selectedSessionForMenu.Name;
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
    public string BeatGridOffsetText => $"{BeatGridOffsetSeconds:0.000} s";
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
        UpdateClockFields(GetPlaybackPositionSeconds());
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

    partial void OnDetectedBpmChanged(double value)
    {
        DetectedBpm = Math.Round(Math.Clamp(value, 1, 300), 2);
        SyncBpmText(nameof(DetectedBpmText), DetectedBpmText, DetectedBpm);
        ApplyPlaybackTempo();
        UpdateClockFields(GetPlaybackPositionSeconds());
        RescheduleMetronome();
    }

    partial void OnPlaybackBpmChanged(double value)
    {
        PlaybackBpm = Math.Round(Math.Clamp(value, 1, 300), 2);
        SyncBpmText(nameof(PlaybackBpmText), PlaybackBpmText, PlaybackBpm);
        ApplyPlaybackTempo();
        RescheduleMetronome();
    }

    partial void OnBeatGridOffsetSecondsChanged(double value)
    {
        BeatGridOffsetSeconds = Math.Max(0, Math.Round(value, 3));
        OnPropertyChanged(nameof(BeatGridOffsetText));
        UpdateClockFields(GetPlaybackPositionSeconds());
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

    partial void OnPreCountMeasuresChanged(int value)
    {
        PreCountMeasures = Math.Clamp(value, 1, 4);
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
            ? $"Saída de áudio selecionada: {value.Name}."
            : _audioEngine.LastError ?? "Não foi possível selecionar a saída de áudio.";
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

    public async Task ImportTracksAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0 || IsBusy)
        {
            return;
        }

        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;
        IsBusy = true;
        OperationProgress = 0;
        OperationStatus = "Preparando importação...";
        CancelCountIn();
        IsPlaying = false;
        _audioEngine.Pause();
        SetVirtualClockRunning(false);
        _audioEngine.StopGuideVoice();
        StopMetronome();
        _lastGuideVoiceKey = null;
        try
        {
            var orderedFilePaths = OrderTrackFilePaths(filePaths);
            var progress = new Progress<(double Value, string Status)>(update =>
            {
                OperationProgress = update.Value;
                OperationStatus = update.Status;
            });
            var outcome = await Task.Run(() => ImportAndAnalyze(orderedFilePaths, progress, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyImportedTracks(outcome);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Importação cancelada.";
        }
        catch (Exception ex)
        {
            AppLogger.Error("Falha ao importar áudio", ex);
            StatusMessage = $"Não foi possível importar as tracks: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OperationProgress = 0;
            OperationStatus = string.Empty;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private AudioImportOutcome ImportAndAnalyze(
        IReadOnlyList<string> orderedFilePaths,
        IProgress<(double Value, string Status)> progress,
        CancellationToken cancellationToken)
    {
        progress.Report((0.08, "Carregando arquivos de áudio..."));
        cancellationToken.ThrowIfCancellationRequested();
        var results = _audioEngine.LoadTracks(orderedFilePaths);
        cancellationToken.ThrowIfCancellationRequested();

        progress.Report((0.42, "Gerando forma de onda..."));
        var waveform = _audioEngine.GetSummedWaveform(360);
        cancellationToken.ThrowIfCancellationRequested();

        progress.Report((0.72, "Detectando BPM e alinhando beats..."));
        var tempoAnalysis = _audioEngine.AnalyzeTempo();
        cancellationToken.ThrowIfCancellationRequested();

        progress.Report((1, "Finalizando importação..."));
        return new AudioImportOutcome(results, waveform, _audioEngine.DurationSeconds, tempoAnalysis);
    }

    private void ApplyImportedTracks(AudioImportOutcome outcome)
    {
        var results = outcome.Results;
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
        WaveformPeaks.Clear();
        foreach (var peak in outcome.Waveform)
        {
            WaveformPeaks.Add(peak);
        }

        _durationSeconds = Math.Max(0, outcome.DurationSeconds);
        CurrentTime = FormatTime(0);
        PlaybackProgress = 0;

        if (outcome.TempoAnalysis is { } tempoAnalysis)
        {
            DetectedBpm = tempoAnalysis.Bpm;
            PlaybackBpm = tempoAnalysis.Bpm;
            BeatGridOffsetSeconds = tempoAnalysis.BeatGridOffsetSeconds;
            TempoAnalysisSummary = FormatTempoAnalysisSummary(tempoAnalysis);
        }

        UpdateClockFields(0);

        StatusMessage = failed.Length == 0
            ? $"{loaded.Length} tracks importadas. BPM: {DetectedBpm:0.##}; grade: {BeatGridOffsetSeconds:0.000} s."
            : $"{loaded.Length} tracks importadas. {failed.Length} falharam: {string.Join(", ", failed.Select(item => item.ErrorMessage).Distinct())}";
        SaveActiveProjectTab();
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _operationCancellation?.Cancel();
        OperationStatus = "Cancelando...";
    }

    [RelayCommand]
    private async Task RedetectTempo()
    {
        if (!HasImportedAudio || IsBusy)
        {
            StatusMessage = HasImportedAudio ? "Já existe uma operação em andamento." : "Importe áudio antes de analisar o tempo.";
            return;
        }

        CancelCountIn();
        IsPlaying = false;
        _audioEngine.Pause();
        SetVirtualClockRunning(false);
        StopMetronome();
        IsBusy = true;
        OperationProgress = 0.25;
        OperationStatus = "Analisando BPM e posição dos beats...";
        try
        {
            var analysis = await Task.Run(_audioEngine.AnalyzeTempo);
            if (analysis is null)
            {
                StatusMessage = "Não foi possível identificar uma grade confiável. Ajuste o BPM e marque o 1.1 manualmente.";
                return;
            }

            DetectedBpm = analysis.Bpm;
            PlaybackBpm = analysis.Bpm;
            BeatGridOffsetSeconds = analysis.BeatGridOffsetSeconds;
            TempoAnalysisSummary = FormatTempoAnalysisSummary(analysis);
            StatusMessage = $"Grade atualizada: {DetectedBpm:0.##} BPM em {BeatGridOffsetSeconds:0.000} s.";
        }
        catch (Exception ex)
        {
            AppLogger.Error("Falha ao reanalisar BPM", ex);
            StatusMessage = $"Não foi possível reanalisar o tempo: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OperationProgress = 0;
            OperationStatus = string.Empty;
        }
    }

    private static string FormatTempoAnalysisSummary(AudioTempoAnalysis analysis)
    {
        return $"Fonte: {analysis.SourceTrack} · confiança {analysis.Confidence:P0} · beat inicial {analysis.BeatGridOffsetSeconds:0.000} s";
    }

    public void SaveProject(string filePath)
    {
        try
        {
            ProjectName = Path.GetFileNameWithoutExtension(filePath);
            var document = CreateProjectDocument();
            _projectFileService.Save(filePath, document);
            CurrentProjectPath = Path.GetFullPath(filePath);
            StatusMessage = $"Projeto salvo: {ProjectName}.";
            SaveActiveProjectTab();
            MarkActiveProjectSaved();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Falha ao salvar projeto", ex);
            StatusMessage = $"Não foi possível salvar o projeto: {ex.Message}";
        }
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
        try
        {
            var document = _projectFileService.Load(filePath);
            LoadProjectDocument(document, Path.GetFullPath(filePath), $"Projeto aberto: {document.ProjectName}.", updateActiveTab: true);
            MarkActiveProjectSaved();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Falha ao abrir projeto", ex);
            StatusMessage = $"Não foi possível abrir o projeto: {ex.Message}";
        }
    }

    public void SeekToProgress(double progress)
    {
        if (!HasImportedAudio || _durationSeconds <= 0)
        {
            return;
        }

        var targetSeconds = Math.Clamp(progress, 0, 1) * _durationSeconds;
        _audioEngine.Seek(targetSeconds);
        SeekVirtualClock(targetSeconds);
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
        if (ActiveProjectTab is not null)
        {
            SaveStateToProjectTab(ActiveProjectTab);
        }
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

        if (tab.IsDirty)
        {
            DirtyTabCloseRequested?.Invoke(tab);
            return;
        }

        CloseProjectTabCore(tab, index);
    }

    public void DiscardAndCloseProjectTab(ProjectTabViewModel tab)
    {
        var index = ProjectTabs.IndexOf(tab);
        if (index >= 0 && ProjectTabs.Count > 1)
        {
            CloseProjectTabCore(tab, index);
            SaveRecoverySnapshot();
        }
    }

    public void DiscardRecoveryOnExit()
    {
        _discardRecoveryOnExit = true;
        _recoveryService.Clear();
    }

    private void CloseProjectTabCore(ProjectTabViewModel tab, int index)
    {

        var wasActive = tab == ActiveProjectTab;
        ProjectTabs.Remove(tab);
        if (wasActive)
        {
            ActiveProjectTab = ProjectTabs[Math.Clamp(index, 0, ProjectTabs.Count - 1)];
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void LoadProjectDocument(VsmixerProjectDocument document, string? projectPath, string statusMessage, bool updateActiveTab)
    {
        _isRestoringProjectTab = true;
        CancelCountIn();
        IsPlaying = false;
        _audioEngine.Pause();
        ResetVirtualClock();
        _audioEngine.StopGuideVoice();
        StopMetronome();
        _lastGuideVoiceKey = null;
        ProjectName = document.ProjectName;
        DetectedBpm = document.DetectedBpm;
        PlaybackBpm = document.PlaybackBpm;
        BeatGridOffsetSeconds = document.BeatGridOffsetSeconds;
        TempoAnalysisSummary = $"Grade carregada do projeto · início em {BeatGridOffsetSeconds:0.000} s";
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
        PreCountEnabled = document.PreCountEnabled;
        PreCountMeasures = document.PreCountMeasures;
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
            ?? AudioOutputDevices.FirstOrDefault(device => string.Equals(device.Name, document.AudioOutputDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? AudioOutputDevices.FirstOrDefault();
        SelectedMidiController = MidiControllerDevices.FirstOrDefault(device => string.Equals(device.Name, document.MidiControllerName, StringComparison.OrdinalIgnoreCase))
            ?? MidiControllerDevices.FirstOrDefault(device => !device.IsPlaceholder);
        IsMidiControllerEnabled = document.MidiControllerEnabled;

        var paths = document.Tracks.Select(track => track.FilePath).Where(File.Exists).ToArray();
        var missingTrackCount = document.Tracks.Count - paths.Length;
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
                IsLooping = session.IsLooping,
                PreCountEnabled = session.PreCountEnabled
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
        StatusMessage = missingTrackCount == 0
            ? statusMessage
            : $"{statusMessage} {missingTrackCount} arquivo(s) de áudio não foram encontrados.";
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
            MarkActiveProjectDirty();
        }
    }

    private void MarkActiveProjectDirty()
    {
        if (_isRestoringProjectTab || ActiveProjectTab is null)
        {
            return;
        }

        ActiveProjectTab.IsDirty = true;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void MarkActiveProjectSaved()
    {
        if (ActiveProjectTab is null)
        {
            return;
        }

        ActiveProjectTab.IsDirty = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        if (!HasUnsavedChanges)
        {
            _recoveryService.Clear();
        }
    }

    private void SaveRecoverySnapshot()
    {
        if (_discardRecoveryOnExit)
        {
            return;
        }

        if (!HasUnsavedChanges)
        {
            _recoveryService.Clear();
            return;
        }

        try
        {
            if (ActiveProjectTab is not null)
            {
                SaveStateToProjectTab(ActiveProjectTab);
            }

            var projects = ProjectTabs
                .Where(tab => tab.IsDirty)
                .Select(tab => new RecoveryProject
                {
                    ProjectPath = tab.ProjectPath,
                    Document = tab.Document
                })
                .ToArray();
            _recoveryService.Save(projects);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Falha no salvamento de recuperação", ex);
        }
    }

    private void TryRestoreRecovery()
    {
        try
        {
            var snapshot = _recoveryService.Load();
            if (snapshot?.Projects.Count is not > 0)
            {
                return;
            }

            ProjectTabs.Clear();
            foreach (var recovered in snapshot.Projects)
            {
                ProjectTabs.Add(new ProjectTabViewModel(
                    recovered.Document.ProjectName,
                    recovered.ProjectPath,
                    recovered.Document)
                {
                    IsDirty = true
                });
            }

            ActiveProjectTab = ProjectTabs[0];
            ActiveProjectTab.IsActive = true;
            LoadProjectDocument(
ActiveProjectTab.Document,
ActiveProjectTab.ProjectPath,
                $"Sessão recuperada de {snapshot.SavedAtUtc.ToLocalTime():g}.",
                updateActiveTab: false);
            OnPropertyChanged(nameof(ActiveProjectTab));
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Falha ao restaurar sessão", ex);
            _recoveryService.Clear();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is not null && PersistedPropertyNames.Contains(e.PropertyName))
        {
            MarkActiveProjectDirty();
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
        if (IsBusy)
        {
            return;
        }

        if (IsCountingIn)
        {
            CancelCountIn();
            StatusMessage = "Pré-contagem cancelada.";
            return;
        }

        if (!HasImportedAudio && !MetronomeEnabled && !GuideVoiceEnabled && SessionRegions.Count == 0)
        {
            StatusMessage = "Importe uma track, habilite o metrônomo ou crie uma sessão antes de tocar.";
            return;
        }

        if (IsPlaying)
        {
            IsPlaying = false;
            _audioEngine.Pause();
            SetVirtualClockRunning(false);
            _audioEngine.StopGuideVoice();
            StopMetronome();
            StatusMessage = "Pausado.";
            UpdateClockFields(GetPlaybackPositionSeconds());
            return;
        }

        var startingFromBeginning = GetPlaybackPositionSeconds() <= 0.01;
        RunWithOptionalPreCount(PreCountEnabled && startingFromBeginning, BeginTransportPlayback);
    }

    private void BeginTransportPlayback()
    {
        IsPlaying = true;
        if (HasImportedAudio)
        {
            _audioEngine.Play();
        }

        SetVirtualClockRunning(true);
        _activePlaybackSession = GetSessionAtPosition(GetPlaybackPositionSeconds());
        StartMetronome(playFirstClick: MetronomeEnabled);
        StatusMessage = HasImportedAudio ? "Tocando." : "Tocando (sem tracks).";
        UpdateClockFields(GetPlaybackPositionSeconds());
    }

    [RelayCommand]
    private void Rewind()
    {
        CancelCountIn();
        IsPlaying = false;
        _audioEngine.Pause();
        _audioEngine.SeekToStart();
        ResetVirtualClock();
        _audioEngine.StopGuideVoice();
        StopMetronome();
        _lastGuideVoiceKey = null;
        ClearQueuedSession();
        _activePlaybackSession = null;
        UpdateClockFields(0);
    }

    [RelayCommand]
    private void EmergencyStop()
    {
        CancelCountIn();
        IsPlaying = false;
        _audioEngine.Pause();
        SetVirtualClockRunning(false);
        _audioEngine.StopGuideVoice();
        _audioEngine.StopPad();
        StopMetronome();
        PadContinuousEnabled = false;
        _lastGuideVoiceKey = null;
        ClearQueuedSession();
        StatusMessage = "Reprodução interrompida.";
    }

    [RelayCommand]
    private void MarkCurrentPositionAsBarStart()
    {
        if (!HasImportedAudio)
        {
            StatusMessage = "Importe áudio antes de marcar o compasso 1.1.";
            return;
        }

        BeatGridOffsetSeconds = _audioEngine.CurrentPositionSeconds;
        StatusMessage = $"Compasso 1.1 marcado em {BeatGridOffsetSeconds:0.000} s.";
    }

    [RelayCommand]
    private void AdjustBeatGridOffset(string amountMilliseconds)
    {
        if (!double.TryParse(amountMilliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return;
        }

        BeatGridOffsetSeconds = Math.Max(0, BeatGridOffsetSeconds + milliseconds / 1000d);
        StatusMessage = $"Grade ajustada para {BeatGridOffsetSeconds:0.000} s.";
    }

    [RelayCommand]
    private void ResetBeatGridOffset()
    {
        BeatGridOffsetSeconds = 0;
        StatusMessage = "Início da grade redefinido para 0.000 s.";
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
            SessionNameOptions.Add($"Sessão {SessionRegions.Count + 1}");
        }

        NewSessionName = SessionNameOptions.First();
        NewSessionStartMeasure = SessionRegions.Count == 0 ? 1 : SessionRegions.Max(session => session.EndMeasure) + 1;
        NewSessionEndMeasure = NewSessionStartMeasure + 15;
        NewSessionPreCountEnabled = PreCountEnabled;
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
        NewSessionPreCountEnabled = session.PreCountEnabled;
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
        SessionRegions.Add(new SessionRegionViewModel(source.Name, start, end, color)
        {
            PreCountEnabled = source.PreCountEnabled
        });
        StatusMessage = $"Sessão duplicada no fim: {source.Name}.";
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
        if (ReferenceEquals(_queuedSession, session))
        {
            ClearQueuedSession();
            StatusMessage = $"Agendamento cancelado: {session.Name}.";
            return;
        }

        var currentSession = _activePlaybackSession ?? GetSessionAtPosition(GetPlaybackPositionSeconds());
        if (IsPlaying && currentSession is not null && !ReferenceEquals(currentSession, session))
        {
            SetQueuedSession(session);
            StatusMessage = $"Sessão agendada: {session.Name}.";
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
        var name = string.IsNullOrWhiteSpace(NewSessionName) ? $"Sessão {SessionRegions.Count + 1}" : NewSessionName.Trim();
        var start = Math.Max(1, Math.Min(NewSessionStartMeasure, NewSessionEndMeasure));
        var end = Math.Max(start, Math.Max(NewSessionStartMeasure, NewSessionEndMeasure));

        if (_editingSession is not null)
        {
            _editingSession.Name = name;
            _editingSession.StartMeasure = start;
            _editingSession.EndMeasure = end;
            _editingSession.PreCountEnabled = NewSessionPreCountEnabled;
            _editingSession = null;
            OnPropertyChanged(nameof(IsEditingSession));
            OnPropertyChanged(nameof(SessionModalTitle));
            OnPropertyChanged(nameof(SessionModalSubmitText));
        }
        else
        {
            var color = _sectionColors[SessionRegions.Count % _sectionColors.Length];
            SessionRegions.Add(new SessionRegionViewModel(name, start, end, color)
            {
                PreCountEnabled = NewSessionPreCountEnabled
            });
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
            MarkActiveProjectDirty();
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
        if (!keepQueuedSession)
        {
            ClearQueuedSession();
        }

        _activePlaybackSession = session;
        CancelCountIn();

        if (session.PreCountEnabled)
        {
            _audioEngine.Pause();
            SetVirtualClockRunning(false);
            _audioEngine.StopGuideVoice();
            _lastGuideVoiceKey = null;
            StopMetronome();
        }

        RunWithOptionalPreCount(session.PreCountEnabled, () => BeginSessionPlayback(session));
    }

    private void BeginSessionPlayback(SessionRegionViewModel session)
    {
        var targetSeconds = GetMeasureStartSeconds(session.StartMeasure);
        _audioEngine.Seek(targetSeconds);
        SeekVirtualClock(targetSeconds);
        _audioEngine.StopGuideVoice();
        _lastGuideVoiceKey = null;

        IsPlaying = true;
        _audioEngine.Play();
        SetVirtualClockRunning(true);

        if (MetronomeEnabled)
        {
            StartMetronome(playFirstClick: true);
        }

        UpdateClockFields(targetSeconds);
        StatusMessage = $"Tocando sessão: {session.Name}.";
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
        if (IsBusy || IsCountingIn)
        {
            UpdatePerformanceUsage();
            return;
        }

        var position = GetPlaybackPositionSeconds();
        UpdateScheduledSessionPlayback(position);
        position = GetPlaybackPositionSeconds();
        UpdateClockFields(position);
        UpdateTrackMeters();
        UpdateGuideVoice(position);
        UpdatePerformanceUsage();

        if (IsPlaying && _durationSeconds > 0 && position >= _durationSeconds - 0.05)
        {
            IsPlaying = false;
            _audioEngine.StopGuideVoice();
            SetVirtualClockRunning(false);
            StopMetronome();
            _lastGuideVoiceKey = null;
            ClearQueuedSession();
            _activePlaybackSession = null;
            StatusMessage = "Fim da reprodução.";
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
        RebaseVirtualClockTempo();
    }

    /// <summary>
    /// Tracks the transport position in real time when there is no imported audio to drive it,
    /// so the timeline, metronome and sessions keep working for track-less projects.
    /// </summary>
    private double GetVirtualPositionSeconds()
    {
        return _virtualClock.IsRunning
            ? _virtualPositionBaseSeconds + _virtualClock.Elapsed.TotalSeconds * _virtualClockRatio
            : _virtualPositionBaseSeconds;
    }

    private void RebaseVirtualClockTempo()
    {
        var ratio = PlaybackBpm / Math.Max(1, DetectedBpm);
        if (!_virtualClock.IsRunning)
        {
            _virtualClockRatio = ratio;
            return;
        }

        _virtualPositionBaseSeconds = GetVirtualPositionSeconds();
        _virtualClockRatio = ratio;
        _virtualClock.Restart();
    }

    private void SeekVirtualClock(double positionSeconds)
    {
        _virtualPositionBaseSeconds = Math.Max(0, positionSeconds);
        _virtualClockRatio = PlaybackBpm / Math.Max(1, DetectedBpm);
        if (_virtualClock.IsRunning)
        {
            _virtualClock.Restart();
        }
    }

    private void SetVirtualClockRunning(bool isRunning)
    {
        if (isRunning == _virtualClock.IsRunning)
        {
            return;
        }

        if (isRunning)
        {
            _virtualClockRatio = PlaybackBpm / Math.Max(1, DetectedBpm);
            _virtualClock.Restart();
        }
        else
        {
            _virtualPositionBaseSeconds = GetVirtualPositionSeconds();
            _virtualClock.Reset();
        }
    }

    private void ResetVirtualClock()
    {
        _virtualClock.Reset();
        _virtualPositionBaseSeconds = 0;
    }

    private double GetPlaybackPositionSeconds()
    {
        return HasImportedAudio ? _audioEngine.CurrentPositionSeconds : GetVirtualPositionSeconds();
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
            StatusMessage = _audioEngine.LastError ?? $"Não foi possível tocar o pad {SelectedPadNote}.";
            return;
        }

        StatusMessage = $"Pad ligado: {SelectedPadNote}.";
    }

    /// <summary>
    /// Announces a session's name a bit before its downbeat (one measure of lead time at the
    /// current tempo), so the musician knows what's coming next instead of hearing it only
    /// once the section has already started.
    /// </summary>
    private void UpdateGuideVoice(double positionSeconds)
    {
        if (!IsPlaying || !GuideVoiceEnabled || SessionRegions.Count == 0)
        {
            return;
        }

        var leadSeconds = GetGuideVoiceLeadSeconds();
        var target = SessionRegions
            .Select(region => new
            {
                Region = region,
                AnnounceSeconds = Math.Max(0, GetMeasureStartSeconds(region.StartMeasure) - leadSeconds),
                EndSeconds = GetMeasureStartSeconds(region.EndMeasure + 1)
            })
            .Where(item => positionSeconds >= item.AnnounceSeconds - 0.05 && positionSeconds < item.EndSeconds)
            .OrderByDescending(item => item.AnnounceSeconds)
            .Select(item => item.Region)
            .FirstOrDefault();

        if (target is null)
        {
            return;
        }

        var guideKey = $"{target.StartMeasure}:{NormalizeGuideKey(target.Name)}";
        if (string.Equals(_lastGuideVoiceKey, guideKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastGuideVoiceKey = guideKey;
        if (!TryGetGuideVoicePath(target.Name, out var guidePath))
        {
            return;
        }

        if (!_audioEngine.PlayGuideVoice(guidePath))
        {
            StatusMessage = _audioEngine.LastError ?? $"Não foi possível tocar a voz guia: {target.Name}.";
        }
    }

    private double GetGuideVoiceLeadSeconds()
    {
        return GetBeatsPerMeasure() * (60d / Math.Clamp(PlaybackBpm, 1, 300));
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
            if (!IsCountInAudioStem(stem))
            {
                names.Add(HumanizeGuideVoiceName(stem));
            }
        }

        foreach (var name in names)
        {
            SessionNameOptions.Add(name);
        }
    }

    /// <summary>1.mp3–4.mp3 are reserved for the spoken pre-count and shouldn't show up as session names.</summary>
    private static bool IsCountInAudioStem(string stem)
    {
        return stem.Length == 1 && stem[0] is >= '1' and <= '4';
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
        SessionCopyOptions.Add(new SessionCopyOptionViewModel("Não copiar", 0, isNone: true));

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

    private static bool TryParseBpm(string value, out double bpm)
    {
        bpm = 0;
        var normalized = value.Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        bpm = Math.Round(Math.Clamp(parsed, 1, 300), 2);
        return true;
    }

    private void SyncBpmText(string propertyName, string currentValue, double bpm)
    {
        var nextValue = bpm.ToString("0.##", CultureInfo.InvariantCulture);
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
        var gridBeatLength = GetGridBeatLength();
        var stepsPerMeasure = Math.Max(1, (int)Math.Round(GetBeatsPerMeasure() / gridBeatLength));
        _metronomeStepsPerMeasure = stepsPerMeasure;
        var intervalSeconds = GetBeatIntervalSeconds();
        var positionSeconds = GetPlaybackPositionSeconds();
        var sourceStepSeconds = 60d / Math.Max(1, DetectedBpm) * gridBeatLength;
        var tempoRatio = PlaybackBpm / Math.Max(1, DetectedBpm);
        var relativeSeconds = positionSeconds - BeatGridOffsetSeconds;
        if (relativeSeconds < -0.001)
        {
            var delay = -relativeSeconds / Math.Max(0.01, tempoRatio);
            _metronomeScheduler.Start(intervalSeconds, 0, delay, playImmediately: false);
            return;
        }

        var stepPosition = Math.Max(0, relativeSeconds) / sourceStepSeconds;
        var nearestStep = Math.Round(stepPosition);
        var isOnGrid = Math.Abs(stepPosition - nearestStep) <= 0.025;
        if (playFirstClick && isOnGrid)
        {
            _metronomeScheduler.Start(
                intervalSeconds,
                (int)nearestStep,
                0,
                playImmediately: true);
            return;
        }

        var nextStep = (int)Math.Floor(stepPosition) + 1;
        var sourceDelay = (nextStep - stepPosition) * sourceStepSeconds;
        var playbackDelay = sourceDelay / Math.Max(0.01, tempoRatio);
        _metronomeScheduler.Start(
            intervalSeconds,
            nextStep,
            playbackDelay,
            playImmediately: false);
    }

    private void StopMetronome()
    {
        _metronomeScheduler.Stop();
    }

    private void RescheduleMetronome()
    {
        if (!IsPlaying || !MetronomeEnabled)
        {
            return;
        }

        StartMetronome(playFirstClick: false);
    }

    private void OnMetronomeSchedulerBeat(int beatIndex)
    {
        var stepsPerMeasure = Math.Max(1, _metronomeStepsPerMeasure);
        var beatInMeasure = beatIndex % stepsPerMeasure;
        _audioEngine.PlayMetronomeClick(isAccent: beatInMeasure == 0);

        if (IsCountingIn)
        {
            PlayCountInVoice(beatInMeasure + 1);
        }
    }

    private void PlayCountInVoice(int count)
    {
        if (_guideVoiceFiles.Count == 0)
        {
            RefreshGuideVoiceFiles();
        }

        if (_guideVoiceFiles.TryGetValue(count.ToString(CultureInfo.InvariantCulture), out var path))
        {
            _audioEngine.PlayGuideVoice(path);
        }
    }

    /// <summary>
    /// Plays a click + spoken count-in ("1, 2, 3, 4") of <see cref="PreCountMeasures"/> bars before
    /// invoking <paramref name="startAction"/>, so the actual transport/session start lands right on beat 1.
    /// </summary>
    private void RunWithOptionalPreCount(bool applyPreCount, Action startAction)
    {
        CancelCountIn();

        if (!applyPreCount)
        {
            startAction();
            return;
        }

        var measures = Math.Max(1, PreCountMeasures);
        var beatsPerMeasure = GetBeatsPerMeasure();
        var beatIntervalSeconds = 60d / Math.Clamp(PlaybackBpm, 1, 300);
        var totalSeconds = measures * beatsPerMeasure * beatIntervalSeconds;

        _metronomeStepsPerMeasure = beatsPerMeasure;
        IsCountingIn = true;
        StatusMessage = $"Pré-contagem: {measures} compasso(s)...";
        _metronomeScheduler.Start(beatIntervalSeconds, 0, 0, playImmediately: true);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(totalSeconds) };
        _countInTimer = timer;
        timer.Tick += OnCountInComplete;
        timer.Start();
        return;

        void OnCountInComplete(object? sender, EventArgs e)
        {
            timer.Tick -= OnCountInComplete;
            timer.Stop();
            if (ReferenceEquals(_countInTimer, timer))
            {
                _countInTimer = null;
            }

            IsCountingIn = false;
            _metronomeScheduler.Stop();
            startAction();
        }
    }

    private void CancelCountIn()
    {
        if (_countInTimer is not null)
        {
            _countInTimer.Stop();
            _countInTimer = null;
        }

        if (IsCountingIn)
        {
            IsCountingIn = false;
            _metronomeScheduler.Stop();
        }
    }

    private int GetMeasureAtPosition(double positionSeconds)
    {
        return BeatGridCalculator.GetMeasure(
            positionSeconds,
            DetectedBpm,
            BeatGridOffsetSeconds,
            GetBeatsPerMeasure());
    }

    private double GetMeasureStartSeconds(int measure)
    {
        return BeatGridCalculator.GetMeasureStart(
            measure,
            DetectedBpm,
            BeatGridOffsetSeconds,
            GetBeatsPerMeasure());
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
            BeatGridOffsetSeconds = BeatGridOffsetSeconds,
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
            PreCountEnabled = PreCountEnabled,
            PreCountMeasures = PreCountMeasures,
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
                    IsLooping = session.IsLooping,
                    PreCountEnabled = session.PreCountEnabled
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
                ? "Saída"
                : device.IsDefault
                    ? "Saída padrão"
                    : "Saída";
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
            AudioOutputDevices.Add(new DeviceOptionViewModel("Nenhuma saída de áudio encontrada", string.Empty, -999, true));
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
            StatusMessage = "Nenhum controlador MIDI disponível.";
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
            StatusMessage = $"Não foi possível habilitar MIDI: {ex.Message}";
        }
    }

    private void UpdateClockFields(double positionSeconds)
    {
        CurrentTime = FormatTime(positionSeconds);
        PlaybackProgress = _durationSeconds <= 0 ? 0 : Math.Clamp(positionSeconds / _durationSeconds, 0, 1);
        BarClock = FormatBarClock(positionSeconds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _countInTimer?.Stop();
        _countInTimer = null;
        _meterTimer.Stop();
        _metronomeScheduler.Dispose();
        _recoveryTimer.Stop();
        if (_discardRecoveryOnExit || !HasUnsavedChanges)
        {
            _recoveryService.Clear();
        }
        else
        {
            SaveRecoverySnapshot();
        }
        _midiControlService.MessageReceived -= OnMidiMessageReceived;
        PropertyChanged -= OnViewModelPropertyChanged;
        _midiControlService.Dispose();
        _audioEngine.Dispose();
        _currentProcess.Dispose();
        ClearTracks();
        GC.SuppressFinalize(this);
    }

    private sealed record AudioImportOutcome(
        IReadOnlyList<AudioTrackLoadResult> Results,
        IReadOnlyList<double> Waveform,
        double DurationSeconds,
        AudioTempoAnalysis? TempoAnalysis);

    private string FormatBarClock(double positionSeconds)
    {
        return BeatGridCalculator.FormatBarClock(
            positionSeconds,
            DetectedBpm,
            BeatGridOffsetSeconds,
            GetBeatsPerMeasure());
    }

    private static string FormatTime(double seconds)
    {
        var safeSeconds = Math.Max(0, seconds);
        var minutes = (int)(safeSeconds / 60);
        var remainingSeconds = (int)(safeSeconds % 60);
        return $"{minutes}:{remainingSeconds:00}";
    }
}
