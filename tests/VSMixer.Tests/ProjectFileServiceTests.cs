using System.Text.Json;
using VSMixer.Models;
using VSMixer.Services;

namespace VSMixer.Tests;

public sealed class ProjectFileServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"vsmixer-tests-{Guid.NewGuid():N}");
    private readonly ProjectFileService _service = new();

    public ProjectFileServiceTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void SaveAndLoad_PreserveProjectAndResolvePortableTrackPath()
    {
        var audioDirectory = Path.Combine(_temporaryDirectory, "audio");
        Directory.CreateDirectory(audioDirectory);
        var audioPath = Path.Combine(audioDirectory, "bateria.wav");
        File.WriteAllBytes(audioPath, []);
        var projectPath = Path.Combine(_temporaryDirectory, "show.vsmixer");
        var document = new VsmixerProjectDocument
        {
            ProjectName = "Show",
            DetectedBpm = 127.65,
            PlaybackBpm = 127.65,
            BeatGridOffsetSeconds = 0.42,
            Tracks =
            [
                new ProjectTrackDocument { Name = "Bateria", FilePath = audioPath }
            ]
        };

        _service.Save(projectPath, document);

        var rawJson = File.ReadAllText(projectPath);
        Assert.Contains(Path.Combine("audio", "bateria.wav"), rawJson);
        Assert.DoesNotContain(audioPath, rawJson);

        var loaded = _service.Load(projectPath);
        Assert.Equal(VsmixerProjectDocument.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("Show", loaded.ProjectName);
        Assert.Equal(127.65, loaded.DetectedBpm);
        Assert.Equal(0.42, loaded.BeatGridOffsetSeconds);
        Assert.Equal(audioPath, loaded.Tracks.Single().FilePath);
    }

    [Fact]
    public void Save_ReplacesDestinationAndLeavesNoTemporaryFile()
    {
        var projectPath = Path.Combine(_temporaryDirectory, "show.vsmixer");
        _service.Save(projectPath, new VsmixerProjectDocument { ProjectName = "Primeiro" });
        _service.Save(projectPath, new VsmixerProjectDocument { ProjectName = "Segundo" });

        Assert.Equal("Segundo", _service.Load(projectPath).ProjectName);
        Assert.Empty(Directory.GetFiles(_temporaryDirectory, "*.tmp"));
    }

    [Fact]
    public void Load_RejectsUnsupportedFutureSchema()
    {
        var projectPath = Path.Combine(_temporaryDirectory, "future.vsmixer");
        var document = new VsmixerProjectDocument
        {
            SchemaVersion = VsmixerProjectDocument.CurrentSchemaVersion + 1
        };
        File.WriteAllText(projectPath, JsonSerializer.Serialize(document));

        var error = Assert.Throws<InvalidDataException>(() => _service.Load(projectPath));
        Assert.Contains("formato", error.Message);
    }

    [Fact]
    public void Load_RejectsMalformedJson()
    {
        var projectPath = Path.Combine(_temporaryDirectory, "broken.vsmixer");
        File.WriteAllText(projectPath, "{not-json");

        Assert.Throws<JsonException>(() => _service.Load(projectPath));
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }
}
