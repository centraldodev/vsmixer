using System.Collections.Generic;

namespace VSMixer.Services;

public interface IAudioEngine
{
    bool IsReady { get; }
    string? LastError { get; }
    int TrackCount { get; }
    double CurrentPositionSeconds { get; }
    double DurationSeconds { get; }
    void Initialize();
    IReadOnlyList<AudioDeviceOption> GetOutputDevices();
    bool SetOutputDevice(int deviceId);
    IReadOnlyList<AudioTrackLoadResult> LoadTracks(IEnumerable<string> filePaths);
    IReadOnlyList<double> GetSummedWaveform(int peakCount);
    int? DetectBpm();
    void Play();
    void Pause();
    void SeekToStart();
    void Seek(double positionSeconds);
    void RemoveTrack(int trackIndex);
    void SetTrackState(int trackIndex, double volume, double pan, bool isMuted);
    double GetTrackLevel(int trackIndex);
    void SetTempo(double bpmOffset);
    void SetPitch(double semitoneOffset, IReadOnlyCollection<int> excludedTrackIndices);
    void SetMetronomeState(double volume, double pan);
    void SetGuideVoiceState(double volume, double pan);
    void PlayMetronomeClick(bool isAccent);
    bool PlayPad(string filePath);
    void StopPad();
    bool PlayGuideVoice(string filePath);
    void StopGuideVoice();
}
