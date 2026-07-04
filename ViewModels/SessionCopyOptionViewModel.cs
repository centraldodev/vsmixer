namespace VSMixer.ViewModels;

public sealed class SessionCopyOptionViewModel
{
    public SessionCopyOptionViewModel(string name, int lengthMeasures, bool isNone = false)
    {
        Name = name;
        LengthMeasures = lengthMeasures;
        IsNone = isNone;
    }

    public string Name { get; }
    public int LengthMeasures { get; }
    public bool IsNone { get; }
    public string DisplayName => IsNone ? Name : $"{Name} ({LengthMeasures} compassos)";

    public override string ToString()
    {
        return DisplayName;
    }
}
