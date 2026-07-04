namespace VSMixer.Services;

public sealed class AudioDeviceOption
{
    public AudioDeviceOption(int id, string name, bool isDefault, bool isEnabled)
    {
        Id = id;
        Name = name;
        IsDefault = isDefault;
        IsEnabled = isEnabled;
    }

    public int Id { get; }
    public string Name { get; }
    public bool IsDefault { get; }
    public bool IsEnabled { get; }
}
