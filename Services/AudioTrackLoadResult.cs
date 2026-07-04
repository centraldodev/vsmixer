namespace VSMixer.Services;

public sealed record AudioTrackLoadResult(
    string FilePath,
    string Name,
    bool IsLoaded,
    string? ErrorMessage);
