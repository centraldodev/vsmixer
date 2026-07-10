using System.Text.Json;
using VSMixer.Models;

namespace VSMixer.Services;

public sealed class ProjectRecoveryService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _recoveryPath;

    public ProjectRecoveryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VSMixer",
            "Recovery",
            "last-session.json"))
    {
    }

    public ProjectRecoveryService(string recoveryPath)
    {
        _recoveryPath = Path.GetFullPath(recoveryPath);
    }

    public RecoverySnapshot? Load()
    {
        if (!File.Exists(_recoveryPath))
        {
            return null;
        }

        var json = File.ReadAllText(_recoveryPath);
        return JsonSerializer.Deserialize<RecoverySnapshot>(json);
    }

    public void Save(IReadOnlyList<RecoveryProject> projects)
    {
        var directory = Path.GetDirectoryName(_recoveryPath)!;
        Directory.CreateDirectory(directory);
        var snapshot = new RecoverySnapshot
        {
            SavedAtUtc = DateTime.UtcNow,
            Projects = projects.ToList()
        };
        var temporaryPath = _recoveryPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, Options));
        File.Move(temporaryPath, _recoveryPath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(_recoveryPath))
        {
            File.Delete(_recoveryPath);
        }
    }
}

public sealed class RecoverySnapshot
{
    public DateTime SavedAtUtc { get; set; }
    public List<RecoveryProject> Projects { get; set; } = [];
}

public sealed class RecoveryProject
{
    public string? ProjectPath { get; set; }
    public VsmixerProjectDocument Document { get; set; } = new();
}
