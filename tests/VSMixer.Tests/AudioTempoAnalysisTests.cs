using VSMixer.Services;

namespace VSMixer.Tests;

public sealed class AudioTempoAnalysisTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"vsmixer-tempo-tests-{Guid.NewGuid():N}");

    public AudioTempoAnalysisTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void AnalyzeTempo_DetectsSyntheticClickTrackAndInitialOffset()
    {
        var clickTrackPath = Path.Combine(_temporaryDirectory, "click.wav");
        WriteClickTrack(clickTrackPath, bpm: 120, firstBeatSeconds: 0.5, durationSeconds: 30);
        using var engine = new BassAudioEngine();

        var loaded = engine.LoadTracks([clickTrackPath]);
        var analysis = engine.AnalyzeTempo();

        Assert.True(loaded.Single().IsLoaded, loaded.Single().ErrorMessage);
        Assert.NotNull(analysis);
        Assert.InRange(analysis.Bpm, 118, 122);
        Assert.InRange(analysis.BeatGridOffsetSeconds, 0.35, 0.65);
        Assert.True(analysis.Confidence >= 0.4);
    }

    private static void WriteClickTrack(string filePath, double bpm, double firstBeatSeconds, double durationSeconds)
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        var sampleCount = (int)(durationSeconds * sampleRate);
        var samples = new short[sampleCount];
        var beatInterval = 60d / bpm;
        var clickLength = (int)(sampleRate * 0.07);

        for (var beatTime = firstBeatSeconds; beatTime < durationSeconds; beatTime += beatInterval)
        {
            var startSample = (int)Math.Round(beatTime * sampleRate);
            for (var i = 0; i < clickLength && startSample + i < samples.Length; i++)
            {
                var envelope = Math.Exp(-i / (sampleRate * 0.012));
                var signal = Math.Sin(2 * Math.PI * 100 * i / sampleRate) * envelope;
                samples[startSample + i] = (short)Math.Clamp(signal * short.MaxValue * 0.9, short.MinValue, short.MaxValue);
            }
        }

        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream);
        var dataLength = samples.Length * sizeof(short);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }
}
