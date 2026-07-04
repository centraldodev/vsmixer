namespace VSMixer.Models;

public enum MidiMessageKind
{
    Note,
    ControlChange
}

public sealed class MidiMapping
{
    public required MidiMessageKind Kind { get; init; }
    public required int Channel { get; init; }
    public required int Number { get; init; }

    public string DisplayName => Kind == MidiMessageKind.Note
        ? $"Nota {Number} (canal {Channel + 1})"
        : $"CC {Number} (canal {Channel + 1})";
}
