using System.Globalization;
using System.Text;
using ManagedBass;
using ManagedBass.Fx;

namespace VSMixer.Services;

public sealed class BassAudioEngine : IAudioEngine
{
    private const int MetronomeSampleRate = 44100;
    private readonly List<int> _streams = [];
    private readonly List<string> _filePaths = [];
    private int _metronomeBeatSample;
    private int _metronomeAccentSample;
    private int _padStream;
    private int _guideVoiceStream;
    private double _metronomeVolume = 0.82;
    private double _metronomePan;
    private double _guideVoiceVolume = 0.85;
    private double _guideVoicePan;
    private int _selectedOutputDevice = -1;
    private int _activeOutputDevice = -1;
    private bool _disposed;
    private bool _streamsLinked;

    public bool IsReady { get; private set; }
    public string? LastError { get; private set; }
    public int TrackCount => _streams.Count;
    public double CurrentPositionSeconds
    {
        get
        {
            if (!IsReady || _streams.Count == 0)
            {
                return 0;
            }

            var position = Bass.ChannelGetPosition(_streams[0]);
            return Math.Max(0, Bass.ChannelBytes2Seconds(_streams[0], position));
        }
    }

    public double DurationSeconds => GetMaxDurationSeconds();

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReady)
        {
            return;
        }

        try
        {
            IsReady = Bass.Init(_selectedOutputDevice) || Bass.LastError == Errors.Already;
            _activeOutputDevice = IsReady ? Bass.CurrentDevice : -1;
            LastError = IsReady ? null : $"BASS init failed: {Bass.LastError}";
        }
        catch (DllNotFoundException ex)
        {
            LastError = $"Native BASS library was not found. Put bass.dll on Windows or libbass.dylib on macOS in the app output folder. Details: {ex.Message}";
            IsReady = false;
        }
        catch (Exception ex)
        {
            LastError = $"BASS init failed: {ex.Message}";
            IsReady = false;
        }
    }

    public IReadOnlyList<AudioDeviceOption> GetOutputDevices()
    {
        var devices = new List<AudioDeviceOption>
        {
            new(-1, "Padrão do sistema", true, true)
        };

        try
        {
            for (var i = 1; i < Bass.DeviceCount; i++)
            {
                var info = Bass.GetDeviceInfo(i);
                if (info.IsEnabled)
                {
                    devices.Add(new AudioDeviceOption(i, info.Name, info.IsDefault, info.IsEnabled));
                }
            }
        }
        catch (Exception ex)
        {
            devices.Add(new AudioDeviceOption(-999, $"Erro ao listar saídas: {ex.Message}", false, false));
        }

        return devices;
    }

    public bool SetOutputDevice(int deviceId)
    {
        if (deviceId <= -999)
        {
            return false;
        }

        _selectedOutputDevice = deviceId;
        try
        {
            Initialize();
            if (!IsReady)
            {
                return false;
            }

            var channelDevice = ResolveOutputDeviceId(deviceId);
            var initialized = Bass.Init(deviceId) || Bass.LastError == Errors.Already;
            if (!initialized && deviceId == -1)
            {
                initialized = Bass.Init(channelDevice) || Bass.LastError == Errors.Already;
            }

            if (!initialized)
            {
                LastError = $"Não foi possível iniciar a saída selecionada: {Bass.LastError}";
                return false;
            }

            Bass.CurrentDevice = channelDevice;
            var failed = false;
            foreach (var stream in _streams)
            {
                if (!Bass.ChannelSetDevice(stream, channelDevice) && Bass.LastError != Errors.Already)
                {
                    failed = true;
                }
            }

            if (_padStream != 0 && !Bass.ChannelSetDevice(_padStream, channelDevice) && Bass.LastError != Errors.Already)
            {
                failed = true;
            }

            if (_guideVoiceStream != 0 && !Bass.ChannelSetDevice(_guideVoiceStream, channelDevice) && Bass.LastError != Errors.Already)
            {
                failed = true;
            }

            MoveMetronomeSamplesToDevice(channelDevice);

            if (failed)
            {
                LastError = $"Não foi possível mover todas as tracks para a saída selecionada: {Bass.LastError}";
                return false;
            }

            _activeOutputDevice = channelDevice;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Não foi possível trocar a saída de áudio: {ex.Message}";
            return false;
        }
    }

    public IReadOnlyList<AudioTrackLoadResult> LoadTracks(IEnumerable<string> filePaths)
    {
        Initialize();
        FreeStreams();
        _filePaths.Clear();

        var results = new List<AudioTrackLoadResult>();
        if (!IsReady)
        {
            return filePaths
                .Select(path => new AudioTrackLoadResult(path, Path.GetFileNameWithoutExtension(path), false, LastError))
                .ToArray();
        }

        foreach (var filePath in filePaths)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            var sourceStream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
            if (sourceStream == 0)
            {
                results.Add(new AudioTrackLoadResult(filePath, name, false, Bass.LastError.ToString()));
                continue;
            }

            int stream;
            try
            {
                stream = BassFx.TempoCreate(sourceStream, BassFlags.FxFreeSource | BassFlags.FxTempoAlgorithmCubic);
            }
            catch (DllNotFoundException ex)
            {
                Bass.StreamFree(sourceStream);
                LastError = $"Native BASS_FX library was not found. Put bass_fx.dll on Windows or libbass_fx.dylib on macOS in the app output folder. Details: {ex.Message}";
                results.Add(new AudioTrackLoadResult(filePath, name, false, LastError));
                continue;
            }

            if (stream == 0)
            {
                Bass.StreamFree(sourceStream);
                results.Add(new AudioTrackLoadResult(filePath, name, false, Bass.LastError.ToString()));
                continue;
            }

            _streams.Add(stream);
            _filePaths.Add(filePath);
            if (_activeOutputDevice > 0)
            {
                Bass.ChannelSetDevice(stream, _activeOutputDevice);
            }

            SetStreamState(stream, 0.72, 0, false);
            results.Add(new AudioTrackLoadResult(filePath, name, true, null));
        }

        RelinkStreams();
        return results;
    }

    public IReadOnlyList<double> GetSummedWaveform(int peakCount)
    {
        Initialize();
        if (!IsReady || _filePaths.Count == 0 || peakCount <= 0)
        {
            return Array.Empty<double>();
        }

        var maxDuration = GetMaxDurationSeconds();
        if (maxDuration <= 0)
        {
            return Array.Empty<double>();
        }

        var summedPeaks = new double[peakCount];
        foreach (var filePath in _filePaths)
        {
            var trackPeaks = DecodeTrackPeaks(filePath, peakCount, maxDuration);
            for (var i = 0; i < peakCount; i++)
            {
                summedPeaks[i] += trackPeaks[i];
            }
        }

        var maxPeak = summedPeaks.Max();
        if (maxPeak <= 0)
        {
            return summedPeaks;
        }

        for (var i = 0; i < summedPeaks.Length; i++)
        {
            summedPeaks[i] = Math.Clamp(summedPeaks[i] / maxPeak, 0, 1);
        }

        return summedPeaks;
    }

    public AudioTempoAnalysis? AnalyzeTempo()
    {
        Initialize();
        if (!IsReady || _filePaths.Count == 0)
        {
            return null;
        }

        var candidates = _filePaths
            .Where(IsRhythmCandidate)
            .Take(4)
            .ToArray();

        if (candidates.Length == 0)
        {
            candidates = _filePaths
                .Where(path => !IsGuideCandidate(path))
                .Take(4)
                .ToArray();
        }

        if (candidates.Length == 0)
        {
            candidates = _filePaths.Take(2).ToArray();
        }

        var estimates = candidates
            .Select(AnalyzeTempoFromFile)
            .OfType<AudioTempoAnalysis>()
            .ToArray();

        if (estimates.Length == 0)
        {
            return null;
        }

        var normalized = estimates
            .Select(analysis => analysis with { Bpm = NormalizeBpm(analysis.Bpm) })
            .OrderBy(analysis => analysis.Bpm)
            .ToArray();
        var medianBpm = normalized[normalized.Length / 2].Bpm;
        var compatible = normalized
            .Where(analysis => Math.Abs(analysis.Bpm - medianBpm) <= 4)
            .ToArray();
        if (compatible.Length == 0)
        {
            compatible = normalized;
        }

        var totalWeight = compatible.Sum(analysis => Math.Max(0.1, analysis.Confidence));
        var bpm = compatible.Sum(analysis => analysis.Bpm * Math.Max(0.1, analysis.Confidence)) / totalWeight;
        var reference = compatible
            .OrderByDescending(analysis => analysis.Confidence)
            .ThenBy(analysis => Math.Abs(analysis.Bpm - bpm))
            .First();
        var spread = compatible.Average(analysis => Math.Abs(analysis.Bpm - bpm));
        var confidence = Math.Clamp(compatible.Average(analysis => analysis.Confidence) * (1 - spread / 8), 0, 1);

        return new AudioTempoAnalysis(
            Math.Round(bpm, 2),
            Math.Round(reference.BeatGridOffsetSeconds, 3),
            confidence,
            reference.SourceTrack);
    }

    public void Play()
    {
        if (!IsReady)
        {
            return;
        }

        if (_streamsLinked)
        {
            Bass.ChannelPlay(_streams[0], false);
            return;
        }

        foreach (var stream in _streams)
        {
            Bass.ChannelPlay(stream, false);
        }
    }

    public void Pause()
    {
        if (!IsReady)
        {
            return;
        }

        if (_streamsLinked)
        {
            Bass.ChannelPause(_streams[0]);
            return;
        }

        foreach (var stream in _streams)
        {
            Bass.ChannelPause(stream);
        }
    }

    public void SeekToStart()
    {
        if (!IsReady)
        {
            return;
        }

        foreach (var stream in _streams)
        {
            Bass.ChannelSetPosition(stream, 0);
        }
    }

    public void Seek(double positionSeconds)
    {
        if (!IsReady)
        {
            return;
        }

        var safePosition = Math.Max(0, positionSeconds);
        foreach (var stream in _streams)
        {
            var bytePosition = Bass.ChannelSeconds2Bytes(stream, safePosition);
            Bass.ChannelSetPosition(stream, Math.Max(0, bytePosition));
        }
    }

    public void RemoveTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= _streams.Count)
        {
            return;
        }

        Bass.StreamFree(_streams[trackIndex]);
        _streams.RemoveAt(trackIndex);
        _filePaths.RemoveAt(trackIndex);
        RelinkStreams();
    }

    public void SetTrackState(int trackIndex, double volume, double pan, bool isMuted)
    {
        if (!IsReady || trackIndex < 0 || trackIndex >= _streams.Count)
        {
            return;
        }

        SetStreamState(_streams[trackIndex], volume, pan, isMuted);
    }

    public double GetTrackLevel(int trackIndex)
    {
        if (!IsReady || trackIndex < 0 || trackIndex >= _streams.Count)
        {
            return 0;
        }

        var level = Bass.ChannelGetLevel(_streams[trackIndex]);
        if (level < 0)
        {
            return 0;
        }

        var left = level & 0xffff;
        var right = (level >> 16) & 0xffff;
        return Math.Clamp(Math.Max(left, right) / 32768d, 0, 1);
    }

    public void SetTempo(double tempoRatio)
    {
        if (!IsReady)
        {
            return;
        }

        var ratio = Math.Clamp(tempoRatio, 0.5, 2.0);
        var tempoPercent = (ratio - 1) * 100;
        foreach (var stream in _streams)
        {
            Bass.ChannelSetAttribute(stream, ChannelAttribute.Tempo, tempoPercent);
        }
    }

    public void SetPitch(double semitoneOffset, IReadOnlyCollection<int> excludedTrackIndices)
    {
        if (!IsReady)
        {
            return;
        }

        var semitones = Math.Clamp(semitoneOffset, -12, 12);
        for (var i = 0; i < _streams.Count; i++)
        {
            var trackSemitones = excludedTrackIndices.Contains(i) ? 0 : semitones;
            Bass.ChannelSetAttribute(_streams[i], ChannelAttribute.Pitch, trackSemitones);
        }
    }

    public void SetMetronomeState(double volume, double pan)
    {
        _metronomeVolume = Math.Clamp(volume, 0, 1);
        _metronomePan = Math.Clamp(pan, -1, 1);
    }

    public void SetGuideVoiceState(double volume, double pan)
    {
        _guideVoiceVolume = Math.Clamp(volume, 0, 1);
        _guideVoicePan = Math.Clamp(pan, -1, 1);

        if (_guideVoiceStream == 0)
        {
            return;
        }

        Bass.ChannelSetAttribute(_guideVoiceStream, ChannelAttribute.Volume, _guideVoiceVolume);
        Bass.ChannelSetAttribute(_guideVoiceStream, ChannelAttribute.Pan, _guideVoicePan);
    }

    public void PlayMetronomeClick(bool isAccent)
    {
        Initialize();
        if (!IsReady)
        {
            return;
        }

        EnsureMetronomeSamples();
        var sample = isAccent ? _metronomeAccentSample : _metronomeBeatSample;
        if (sample == 0)
        {
            return;
        }

        var channel = Bass.SampleGetChannel(sample, false);
        if (channel == 0)
        {
            return;
        }

        if (_activeOutputDevice > 0)
        {
            Bass.ChannelSetDevice(channel, _activeOutputDevice);
        }

        Bass.ChannelSetAttribute(channel, ChannelAttribute.Volume, _metronomeVolume);
        Bass.ChannelSetAttribute(channel, ChannelAttribute.Pan, _metronomePan);
        Bass.ChannelPlay(channel, true);
    }

    public bool PlayPad(string filePath)
    {
        Initialize();
        if (!IsReady || !File.Exists(filePath))
        {
            return false;
        }

        StopPad();
        _padStream = Bass.CreateStream(filePath, 0, 0, BassFlags.Loop | BassFlags.Float | BassFlags.Prescan);
        if (_padStream == 0)
        {
            LastError = $"Não foi possível carregar o pad: {Bass.LastError}";
            return false;
        }

        if (_activeOutputDevice > 0)
        {
            Bass.ChannelSetDevice(_padStream, _activeOutputDevice);
        }

        Bass.ChannelSetAttribute(_padStream, ChannelAttribute.Volume, 0.65);
        if (!Bass.ChannelPlay(_padStream, true))
        {
            LastError = $"Não foi possível tocar o pad: {Bass.LastError}";
            StopPad();
            return false;
        }

        LastError = null;
        return true;
    }

    public void StopPad()
    {
        if (_padStream == 0)
        {
            return;
        }

        Bass.ChannelStop(_padStream);
        Bass.StreamFree(_padStream);
        _padStream = 0;
    }

    public bool PlayGuideVoice(string filePath)
    {
        Initialize();
        if (!IsReady || !File.Exists(filePath))
        {
            return false;
        }

        StopGuideVoice();
        _guideVoiceStream = Bass.CreateStream(filePath, 0, 0, BassFlags.Float | BassFlags.Prescan);
        if (_guideVoiceStream == 0)
        {
            LastError = $"Não foi possível carregar a voz guia: {Bass.LastError}";
            return false;
        }

        if (_activeOutputDevice > 0)
        {
            Bass.ChannelSetDevice(_guideVoiceStream, _activeOutputDevice);
        }

        Bass.ChannelSetAttribute(_guideVoiceStream, ChannelAttribute.Volume, _guideVoiceVolume);
        Bass.ChannelSetAttribute(_guideVoiceStream, ChannelAttribute.Pan, _guideVoicePan);
        if (!Bass.ChannelPlay(_guideVoiceStream, true))
        {
            LastError = $"Não foi possível tocar a voz guia: {Bass.LastError}";
            StopGuideVoice();
            return false;
        }

        LastError = null;
        return true;
    }

    public void StopGuideVoice()
    {
        if (_guideVoiceStream == 0)
        {
            return;
        }

        Bass.ChannelStop(_guideVoiceStream);
        Bass.StreamFree(_guideVoiceStream);
        _guideVoiceStream = 0;
    }

    private void FreeStreams()
    {
        UnlinkStreams();
        foreach (var stream in _streams)
        {
            Bass.StreamFree(stream);
        }

        _streams.Clear();
        _filePaths.Clear();
    }

    private void RelinkStreams()
    {
        UnlinkStreams();
        if (_streams.Count < 2)
        {
            return;
        }

        var leader = _streams[0];
        for (var i = 1; i < _streams.Count; i++)
        {
            if (Bass.ChannelSetLink(leader, _streams[i]))
            {
                continue;
            }

            UnlinkStreams();
            return;
        }

        _streamsLinked = true;
    }

    private void UnlinkStreams()
    {
        _streamsLinked = false;
        if (_streams.Count < 2)
        {
            return;
        }

        var leader = _streams[0];
        for (var i = 1; i < _streams.Count; i++)
        {
            Bass.ChannelRemoveLink(leader, _streams[i]);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopGuideVoice();
        StopPad();
        FreeStreams();

        if (_metronomeBeatSample != 0)
        {
            Bass.SampleFree(_metronomeBeatSample);
            _metronomeBeatSample = 0;
        }

        if (_metronomeAccentSample != 0)
        {
            Bass.SampleFree(_metronomeAccentSample);
            _metronomeAccentSample = 0;
        }

        if (IsReady)
        {
            Bass.Free();
            IsReady = false;
        }

        GC.SuppressFinalize(this);
    }

    private static void SetStreamState(int stream, double volume, double pan, bool isMuted)
    {
        var effectiveVolume = isMuted ? 0 : Math.Clamp(volume, 0, 1);
        Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, effectiveVolume);
        Bass.ChannelSetAttribute(stream, ChannelAttribute.Pan, Math.Clamp(pan, -1, 1));
    }

    private void EnsureMetronomeSamples()
    {
        if (_metronomeBeatSample != 0 && _metronomeAccentSample != 0)
        {
            return;
        }

        _metronomeBeatSample = CreateMetronomeSample(1600, 0.58f, 0.042);
        _metronomeAccentSample = CreateMetronomeSample(2300, 0.74f, 0.052);
        MoveMetronomeSamplesToDevice(_activeOutputDevice);
    }

    private static int CreateMetronomeSample(double frequency, float volume, double lengthSeconds)
    {
        var sampleCount = Math.Max(1, (int)(MetronomeSampleRate * lengthSeconds));
        var data = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var progress = i / (double)sampleCount;
            var attack = Math.Min(1, progress / 0.08);
            var envelope = attack * Math.Pow(1 - progress, 3.2);
            data[i] = (float)(Math.Sin(2 * Math.PI * frequency * i / MetronomeSampleRate) * envelope * volume);
        }

        var sample = Bass.CreateSample(data.Length * sizeof(float), MetronomeSampleRate, 1, 32, BassFlags.Float);
        if (sample == 0)
        {
            return 0;
        }

        if (Bass.SampleSetData(sample, data))
        {
            return sample;
        }

        Bass.SampleFree(sample);
        return 0;
    }

    private void MoveMetronomeSamplesToDevice(int deviceId)
    {
        if (deviceId <= 0)
        {
            return;
        }

        if (_metronomeBeatSample != 0)
        {
            Bass.ChannelSetDevice(_metronomeBeatSample, deviceId);
        }

        if (_metronomeAccentSample != 0)
        {
            Bass.ChannelSetDevice(_metronomeAccentSample, deviceId);
        }
    }

    private static int ResolveOutputDeviceId(int deviceId)
    {
        if (deviceId != -1)
        {
            return deviceId;
        }

        for (var i = 1; i < Bass.DeviceCount; i++)
        {
            var info = Bass.GetDeviceInfo(i);
            if (info.IsEnabled && info.IsDefault)
            {
                return i;
            }
        }

        return Bass.DeviceCount > 1 ? 1 : 0;
    }

    private double GetMaxDurationSeconds()
    {
        var maxDuration = 0d;
        foreach (var filePath in _filePaths)
        {
            var decoder = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
            if (decoder == 0)
            {
                continue;
            }

            var length = Bass.ChannelGetLength(decoder);
            var duration = Bass.ChannelBytes2Seconds(decoder, length);
            maxDuration = Math.Max(maxDuration, duration);
            Bass.StreamFree(decoder);
        }

        return maxDuration;
    }

    private static double[] DecodeTrackPeaks(string filePath, int peakCount, double maxDuration)
    {
        var peaks = new double[peakCount];
        var decoder = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
        if (decoder == 0)
        {
            return peaks;
        }

        var info = Bass.ChannelGetInfo(decoder);
        var channels = Math.Max(1, info.Channels);
        var sampleRate = Math.Max(1, info.Frequency);
        var buffer = new float[8192 * channels];
        long framesRead = 0;

        while (true)
        {
            var bytesRead = Bass.ChannelGetData(decoder, buffer, buffer.Length * sizeof(float));
            if (bytesRead <= 0)
            {
                break;
            }

            var floatsRead = bytesRead / sizeof(float);
            var frameCount = floatsRead / channels;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var sum = 0d;
                var offset = frame * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += Math.Abs(buffer[offset + channel]);
                }

                var seconds = (framesRead + frame) / (double)sampleRate;
                var peakIndex = Math.Clamp((int)(seconds / maxDuration * peakCount), 0, peakCount - 1);
                peaks[peakIndex] = Math.Max(peaks[peakIndex], sum / channels);
            }

            framesRead += frameCount;
        }

        Bass.StreamFree(decoder);
        return peaks;
    }

    private static bool IsRhythmCandidate(string filePath)
    {
        var name = NormalizeTrackName(Path.GetFileNameWithoutExtension(filePath));
        var tokens = name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        if (tokens.Overlaps(RhythmKeywords))
        {
            return true;
        }

        return name.Contains("hi hat", StringComparison.Ordinal)
            || name.Contains("hihat", StringComparison.Ordinal)
            || name.Contains("drum loop", StringComparison.Ordinal)
            || name.Contains("percussion loop", StringComparison.Ordinal)
            || name.Contains("percussao loop", StringComparison.Ordinal);
    }

    private static bool IsGuideCandidate(string filePath)
    {
        var name = NormalizeTrackName(Path.GetFileNameWithoutExtension(filePath));
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(token => token is "guia" or "guide" or "voz" or "voice" or "vocal" or "vocals");
    }

    private static readonly HashSet<string> RhythmKeywords = new(StringComparer.Ordinal)
    {
        "bateria",
        "batera",
        "batida",
        "click",
        "metronome",
        "metronomo",
        "drum",
        "drums",
        "drummer",
        "beat",
        "beats",
        "percussion",
        "percussao",
        "perc",
        "kick",
        "bd",
        "bumbo",
        "snare",
        "caixa",
        "hat",
        "hats",
        "hihat",
        "tom",
        "toms",
        "cymbal",
        "cymbals",
        "prato",
        "pratos",
        "overhead",
        "overheads",
        "oh",
        "shaker",
        "tambourine",
        "tamborim",
        "conga",
        "congas",
        "bongo",
        "bongos"
    };

    private static string NormalizeTrackName(string value)
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

            if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static AudioTempoAnalysis? AnalyzeTempoFromFile(string filePath)
    {
        return TryAnalyzeTempoWithBassFx(filePath) ?? AnalyzeTempoFallback(filePath);
    }

    private static AudioTempoAnalysis? TryAnalyzeTempoWithBassFx(string filePath)
    {
        var bpmDecoder = 0;
        var beatDecoder = 0;
        try
        {
            bpmDecoder = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
            if (bpmDecoder == 0)
            {
                return null;
            }

            var length = Bass.ChannelGetLength(bpmDecoder);
            var duration = Math.Min(120, Math.Max(0, Bass.ChannelBytes2Seconds(bpmDecoder, length)));
            if (duration < 8)
            {
                return null;
            }

            const int minimumBpm = 55;
            const int maximumBpm = 220;
            var minMaxBpm = minimumBpm | (maximumBpm << 16);
            var bpm = BassFx.BPMDecodeGet(
                bpmDecoder,
                0,
                duration,
                minMaxBpm,
                BassFlags.Default,
                null,
                IntPtr.Zero);
            BassFx.BPMFree(bpmDecoder);
            Bass.StreamFree(bpmDecoder);
            bpmDecoder = 0;

            if (!double.IsFinite(bpm) || bpm <= 0)
            {
                return null;
            }

            bpm = (float)NormalizeBpm(bpm);
            beatDecoder = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
            if (beatDecoder == 0)
            {
                return null;
            }

            var beatPositions = new List<double>();
            BPMBeatProcedure beatProcedure = (_, beatPosition, _) =>
            {
                if (double.IsFinite(beatPosition) && beatPosition >= 0)
                {
                    beatPositions.Add(beatPosition);
                }
            };
            var detected = BassFx.BPMBeatDecodeGet(
                beatDecoder,
                0,
                duration,
                BassFlags.Default,
                beatProcedure,
                IntPtr.Zero);
            BassFx.BPMBeatFree(beatDecoder);
            Bass.StreamFree(beatDecoder);
            beatDecoder = 0;

            if (!detected || beatPositions.Count < 4)
            {
                var fallback = AnalyzeTempoFallback(filePath);
                return new AudioTempoAnalysis(
                    Math.Round(bpm, 2),
                    fallback?.BeatGridOffsetSeconds ?? 0,
                    Math.Max(0.55, fallback?.Confidence ?? 0),
                    Path.GetFileNameWithoutExtension(filePath));
            }

            var (offset, gridConfidence) = EstimateBeatGrid(beatPositions, bpm);
            return new AudioTempoAnalysis(
                Math.Round(bpm, 2),
                offset,
                Math.Clamp(0.55 + gridConfidence * 0.4, 0, 0.95),
                Path.GetFileNameWithoutExtension(filePath));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (bpmDecoder != 0)
            {
                BassFx.BPMFree(bpmDecoder);
                Bass.StreamFree(bpmDecoder);
            }

            if (beatDecoder != 0)
            {
                BassFx.BPMBeatFree(beatDecoder);
                Bass.StreamFree(beatDecoder);
            }
        }
    }

    private static AudioTempoAnalysis? AnalyzeTempoFallback(string filePath)
    {
        var decoder = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
        if (decoder == 0)
        {
            return null;
        }

        var info = Bass.ChannelGetInfo(decoder);
        var channels = Math.Max(1, info.Channels);
        var sampleRate = Math.Max(1, info.Frequency);
        var hopFrames = Math.Max(256, sampleRate / 50);
        var buffer = new float[hopFrames * channels];
        var envelopes = new List<double>(6000);
        var maxAnalysisSeconds = 120d;
        long framesReadTotal = 0;

        while (framesReadTotal / (double)sampleRate < maxAnalysisSeconds)
        {
            var bytesRead = Bass.ChannelGetData(decoder, buffer, buffer.Length * sizeof(float));
            if (bytesRead <= 0)
            {
                break;
            }

            var floatsRead = bytesRead / sizeof(float);
            var sum = 0d;
            for (var i = 0; i < floatsRead; i++)
            {
                sum += Math.Abs(buffer[i]);
            }

            envelopes.Add(sum / Math.Max(1, floatsRead));
            framesReadTotal += floatsRead / channels;
        }

        Bass.StreamFree(decoder);
        if (envelopes.Count < 80)
        {
            return null;
        }

        var mean = envelopes.Average();
        var variance = envelopes.Sum(value => Math.Pow(value - mean, 2)) / envelopes.Count;
        var threshold = mean + Math.Sqrt(variance) * 1.35;
        var peaks = new List<double>();
        var minPeakDistance = (int)Math.Max(2, 0.18 / (hopFrames / (double)sampleRate));
        var lastPeak = -minPeakDistance;

        for (var i = 1; i < envelopes.Count - 1; i++)
        {
            if (i - lastPeak < minPeakDistance)
            {
                continue;
            }

            if (envelopes[i] > threshold && envelopes[i] > envelopes[i - 1] && envelopes[i] >= envelopes[i + 1])
            {
                peaks.Add(i * hopFrames / (double)sampleRate);
                lastPeak = i;
            }
        }

        if (peaks.Count < 4)
        {
            return null;
        }

        var votes = new Dictionary<int, int>();
        for (var i = 0; i < peaks.Count; i++)
        {
            for (var j = i + 1; j < Math.Min(peaks.Count, i + 8); j++)
            {
                var interval = peaks[j] - peaks[i];
                if (interval <= 0)
                {
                    continue;
                }

                var candidateBpm = 60d / interval;
                while (candidateBpm < 70)
                {
                    candidateBpm *= 2;
                }

                while (candidateBpm > 190)
                {
                    candidateBpm /= 2;
                }

                if (candidateBpm is < 70 or > 190)
                {
                    continue;
                }

                var rounded = (int)Math.Round(candidateBpm);
                votes[rounded] = votes.TryGetValue(rounded, out var count) ? count + 1 : 1;
            }
        }

        if (votes.Count == 0)
        {
            return null;
        }

        var winningVote = votes.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First();
        var bpm = NormalizeBpm(winningVote.Key);
        var (offset, gridConfidence) = EstimateBeatGrid(peaks, bpm);
        var voteConfidence = winningVote.Value / (double)Math.Max(1, votes.Values.Sum());
        return new AudioTempoAnalysis(
            Math.Round(bpm, 2),
            offset,
            Math.Clamp(0.25 + voteConfidence + gridConfidence * 0.3, 0, 0.75),
            Path.GetFileNameWithoutExtension(filePath));
    }

    private static (double OffsetSeconds, double Confidence) EstimateBeatGrid(
        IReadOnlyList<double> beatPositions,
        double bpm)
    {
        if (beatPositions.Count == 0 || bpm <= 0)
        {
            return (0, 0);
        }

        var interval = 60d / bpm;
        const int phaseBins = 96;
        var histogram = new double[phaseBins];
        foreach (var position in beatPositions)
        {
            var phase = PositiveModulo(position, interval) / interval;
            var bin = Math.Clamp((int)Math.Round(phase * phaseBins) % phaseBins, 0, phaseBins - 1);
            histogram[bin] += 1;
            histogram[(bin + phaseBins - 1) % phaseBins] += 0.35;
            histogram[(bin + 1) % phaseBins] += 0.35;
        }

        var winningBin = Array.IndexOf(histogram, histogram.Max());
        var phaseSeconds = winningBin / (double)phaseBins * interval;
        var tolerance = interval * 0.14;
        var aligned = beatPositions
            .Where(position => CircularDistance(PositiveModulo(position, interval), phaseSeconds, interval) <= tolerance)
            .OrderBy(position => position)
            .ToArray();
        var offset = aligned.Length == 0 ? phaseSeconds : aligned[0];
        var confidence = aligned.Length / (double)beatPositions.Count;
        return (Math.Round(Math.Max(0, offset), 3), confidence);
    }

    private static double NormalizeBpm(double bpm)
    {
        while (bpm < 70)
        {
            bpm *= 2;
        }

        while (bpm > 190)
        {
            bpm /= 2;
        }

        return Math.Clamp(bpm, 50, 240);
    }

    private static double PositiveModulo(double value, double divisor)
    {
        return ((value % divisor) + divisor) % divisor;
    }

    private static double CircularDistance(double left, double right, double period)
    {
        var distance = Math.Abs(left - right);
        return Math.Min(distance, period - distance);
    }
}
