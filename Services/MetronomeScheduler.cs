using System.Diagnostics;

namespace VSMixer.Services;

public sealed class MetronomeScheduler : IDisposable
{
    private readonly Action<int> _onBeat;
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    /// <summary>
    /// Notifies with the running beat index (relative to the scheduling start); callers
    /// derive accents or spoken count numbers from it via their own steps-per-measure.
    /// </summary>
    public MetronomeScheduler(Action<int> onBeat)
    {
        _onBeat = onBeat;
    }

    public void Start(
        double intervalSeconds,
        int firstBeatIndex,
        double initialDelaySeconds,
        bool playImmediately)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        var cancellation = new CancellationTokenSource();
        lock (_syncRoot)
        {
            _cancellation = cancellation;
        }

        var nextBeatIndex = Math.Max(0, firstBeatIndex);
        if (playImmediately)
        {
            _onBeat(nextBeatIndex);
            nextBeatIndex++;
        }

        _ = RunAsync(
            Math.Max(0.01, intervalSeconds),
            nextBeatIndex,
            playImmediately ? Math.Max(0.01, intervalSeconds) : Math.Max(0, initialDelaySeconds),
            cancellation.Token);
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            cancellation = _cancellation;
            _cancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task RunAsync(
        double intervalSeconds,
        int beatIndex,
        double initialDelaySeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var nextBeatSeconds = initialDelaySeconds;
            while (!cancellationToken.IsCancellationRequested)
            {
                var remaining = nextBeatSeconds - clock.Elapsed.TotalSeconds;
                if (remaining > 0.002)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(remaining, 0.01)), cancellationToken);
                    continue;
                }

                _onBeat(beatIndex);
                beatIndex++;
                nextBeatSeconds += intervalSeconds;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal scheduler shutdown.
        }

    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
