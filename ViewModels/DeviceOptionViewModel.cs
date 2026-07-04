namespace VSMixer.ViewModels;

public sealed class DeviceOptionViewModel
{
    public DeviceOptionViewModel(string name, string detail, int id = 0, bool isPlaceholder = false)
    {
        Name = name;
        Detail = detail;
        Id = id;
        IsPlaceholder = isPlaceholder;
    }

    public int Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public bool IsPlaceholder { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Detail) ? Name : $"{Name} - {Detail}";

    public override string ToString()
    {
        return DisplayName;
    }
}
