namespace VSMixer.Models;

public sealed class VsmixerProjectDocument
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string ProjectName { get; set; } = "Projeto 1";
    public double DetectedBpm { get; set; } = 133;
    public double PlaybackBpm { get; set; } = 133;
    public double BeatGridOffsetSeconds { get; set; }
    public int PitchOffset { get; set; }
    public string SelectedTimeSignature { get; set; } = "4/4";
    public string SelectedGrid { get; set; } = "1/4";
    public double MasterAVolume { get; set; } = 0.96;
    public double MasterBVolume { get; set; } = 0.96;
    public bool MetronomeEnabled { get; set; } = true;
    public double MetronomeVolume { get; set; } = 0.82;
    public double MetronomePan { get; set; }
    public bool GuideVoiceEnabled { get; set; } = true;
    public double GuideVoiceVolume { get; set; } = 0.85;
    public double GuideVoicePan { get; set; }
    public bool PadContinuousEnabled { get; set; }
    public string SelectedPadNote { get; set; } = "C";
    public int AudioOutputDeviceId { get; set; } = -1;
    public string? AudioOutputDeviceName { get; set; }
    public bool MidiControllerEnabled { get; set; }
    public string? MidiControllerName { get; set; }
    public string MidiControlBank { get; set; } = "Transporte";
    public MidiMapping? PlayMidiMapping { get; set; }
    public MidiMapping? RewindMidiMapping { get; set; }
    public MidiMapping? MasterAMidiMapping { get; set; }
    public MidiMapping? MasterBMidiMapping { get; set; }
    public List<ProjectTrackDocument> Tracks { get; set; } = [];
    public List<ProjectSessionDocument> Sessions { get; set; } = [];
}

public sealed class ProjectTrackDocument
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public double Volume { get; set; } = 1;
    public MasterBus MasterBus { get; set; } = MasterBus.B;
    public bool IsMuted { get; set; }
    public bool IsSolo { get; set; }
}

public sealed class ProjectSessionDocument
{
    public string Name { get; set; } = string.Empty;
    public int StartMeasure { get; set; }
    public int EndMeasure { get; set; }
    public string Color { get; set; } = "#2f7dff";
    public bool IsLooping { get; set; }
}
