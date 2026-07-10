namespace VSMixer.Services;

public sealed record AudioTempoAnalysis(
    double Bpm,
    double BeatGridOffsetSeconds,
    double Confidence,
    string SourceTrack);
