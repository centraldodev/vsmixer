using VSMixer.Models;
using VSMixer.Services;

namespace VSMixer.Tests;

public sealed class ProjectRecoveryServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"vsmixer-recovery-tests-{Guid.NewGuid():N}");
    private readonly string _recoveryPath;

    public ProjectRecoveryServiceTests()
    {
        _recoveryPath = Path.Combine(_temporaryDirectory, "recovery.json");
    }

    [Fact]
    public void SaveAndLoad_PreserveDirtyProjectSnapshot()
    {
        var service = new ProjectRecoveryService(_recoveryPath);
        service.Save(
        [
            new RecoveryProject
            {
                ProjectPath = "/shows/show.vsmixer",
                Document = new VsmixerProjectDocument { ProjectName = "Show recuperado" }
            }
        ]);

        var snapshot = service.Load();

        Assert.NotNull(snapshot);
        var project = Assert.Single(snapshot.Projects);
        Assert.Equal("Show recuperado", project.Document.ProjectName);
    }

    [Fact]
    public void Clear_RemovesRecoverySnapshot()
    {
        var service = new ProjectRecoveryService(_recoveryPath);
        service.Save([new RecoveryProject()]);

        service.Clear();

        Assert.Null(service.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
