using System;
using VSMixer.Models;

namespace VSMixer.Services;

public interface IMidiControlService
{
    event EventHandler<MidiMessageReceivedEventArgs>? MessageReceived;
    void StartListening(string? deviceName);
    void StopListening();
}

public sealed class MidiMessageReceivedEventArgs : EventArgs
{
    public required MidiMessageKind Kind { get; init; }
    public required int Channel { get; init; }
    public required int Number { get; init; }
    public required int Value { get; init; }
}
