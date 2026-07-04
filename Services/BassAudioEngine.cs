using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            new(-1, "Padrao do sistema", true, true)
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
            devices.Add(new AudioDeviceOption(-999, $"Erro ao listar saidas: {ex.Message}", false, false));
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
                LastError = $"Nao foi possivel iniciar a saida selecionada: {Bass.LastError}";
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
                LastError = $"Nao foi possivel mover todas as tracks para a saida selecionada: {Bass.LastError}";
                return false;
            }

            _activeOutputDevice = channelDevice;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Nao foi possivel trocar a saida de audio: {ex.Message}";
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

    public int? DetectBpm()
    {
        Initialize();
        if (!IsReady || _filePaths.Count == 0)
        {
            return null;
        }

        var candidates = _filePaths
            .Where(IsRhythmCandidate)
            .DefaultIfEmpty(_filePaths[0])
            .Take(3)
            .ToArray();

        var estimates = candidates
            .Select(DetectBpmFromFile)
            .Where(bpm => bpm is >= 50 and <= 240)
            .Select(bpm => bpm!.Value)
            .ToArray();

        if (estimates.Length == 0)
        {
            return null;
        }

        return (int)Math.Round(estimates.Average());
    }

    public void Play()
    {
        if (!IsReady)
        {
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

    public void SetMasterVolume(double volume)
    {
        Initialize();
        if (!IsReady)
        {
            return;
        }

        Bass.GlobalStreamVolume = (int)(Math.Clamp(volume, 0, 1) * 10000);
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

        var channel = Bass.SampleGetChannel(sample, true);
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
            LastError = $"Nao foi possivel carregar o pad: {Bass.LastError}";
            return false;
        }

        if (_activeOutputDevice > 0)
        {
            Bass.ChannelSetDevice(_padStream, _activeOutputDevice);
        }

        Bass.ChannelSetAttribute(_padStream, ChannelAttribute.Volume, 0.65);
        if (!Bass.ChannelPlay(_padStream, true))
        {
            LastError = $"Nao foi possivel tocar o pad: {Bass.LastError}";
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
            LastError = $"Nao foi possivel carregar a voz guia: {Bass.LastError}";
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
            LastError = $"Nao foi possivel tocar a voz guia: {Bass.LastError}";
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
        foreach (var stream in _streams)
        {
            Bass.StreamFree(stream);
        }

        _streams.Clear();
        _filePaths.Clear();
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

        var sample = Bass.CreateSample(data.Length * sizeof(float), MetronomeSampleRate, 1, 8, BassFlags.Float);
        if (sample == 0)
        {
            return 0;
        }

        return Bass.SampleSetData(sample, data) ? sample : 0;
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
        var name = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        return name.Contains("drum", StringComparison.Ordinal)
            || name.Contains("drums", StringComparison.Ordinal)
            || name.Contains("drummer", StringComparison.Ordinal)
            || name.Contains("bateria", StringComparison.Ordinal)
            || name.Contains("click", StringComparison.Ordinal)
            || name.Contains("metronome", StringComparison.Ordinal)
            || name.Contains("metronomo", StringComparison.Ordinal)
            || name.Contains("metrônomo", StringComparison.Ordinal);
    }

    private static int? DetectBpmFromFile(string filePath)
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

                var bpm = 60d / interval;
                while (bpm < 70)
                {
                    bpm *= 2;
                }

                while (bpm > 190)
                {
                    bpm /= 2;
                }

                if (bpm is < 70 or > 190)
                {
                    continue;
                }

                var rounded = (int)Math.Round(bpm);
                votes[rounded] = votes.TryGetValue(rounded, out var count) ? count + 1 : 1;
            }
        }

        return votes.Count == 0
            ? null
            : votes.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
    }
}
