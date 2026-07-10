using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using VSMixer.Models;

namespace VSMixer.Services;

public sealed class DryWetMidiControlService : IMidiControlService
{
    private readonly List<InputDevice> _devices = [];

    public event EventHandler<MidiMessageReceivedEventArgs>? MessageReceived;

    public void StartListening(string? deviceName)
    {
        StopListening();

        foreach (var device in InputDevice.GetAll())
        {
            if (!string.IsNullOrWhiteSpace(deviceName)
                && !string.Equals(device.Name, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                device.Dispose();
                continue;
            }

            device.EventReceived += OnMidiEventReceived;
            device.StartEventsListening();
            _devices.Add(device);
        }
    }

    public void StopListening()
    {
        foreach (var device in _devices)
        {
            device.EventReceived -= OnMidiEventReceived;
            device.StopEventsListening();
            device.Dispose();
        }

        _devices.Clear();
    }

    public void Dispose()
    {
        StopListening();
        GC.SuppressFinalize(this);
    }

    private void OnMidiEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        _ = sender;

        if (e.Event is NoteOnEvent noteOn)
        {
            if ((int)noteOn.Velocity <= 0)
            {
                return;
            }

            MessageReceived?.Invoke(this, new MidiMessageReceivedEventArgs
            {
                Kind = MidiMessageKind.Note,
                Channel = noteOn.Channel,
                Number = noteOn.NoteNumber,
                Value = noteOn.Velocity
            });
            return;
        }

        if (e.Event is ControlChangeEvent controlChange)
        {
            MessageReceived?.Invoke(this, new MidiMessageReceivedEventArgs
            {
                Kind = MidiMessageKind.ControlChange,
                Channel = controlChange.Channel,
                Number = controlChange.ControlNumber,
                Value = controlChange.ControlValue
            });
        }
    }
}
