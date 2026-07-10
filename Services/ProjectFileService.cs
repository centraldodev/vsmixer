using System.Text.Json;
using VSMixer.Models;

namespace VSMixer.Services;

public sealed class ProjectFileService : IProjectFileService
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public VsmixerProjectDocument Load(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var json = File.ReadAllText(fullPath);
        var document = JsonSerializer.Deserialize<VsmixerProjectDocument>(json, ReadOptions)
            ?? throw new InvalidDataException("O arquivo não contém um projeto VSMixer válido.");

        if (document.SchemaVersion > VsmixerProjectDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Este projeto usa o formato {document.SchemaVersion}, mas esta versão do VSMixer suporta até {VsmixerProjectDocument.CurrentSchemaVersion}.");
        }

        document.Tracks ??= [];
        document.Sessions ??= [];
        var projectDirectory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        foreach (var track in document.Tracks)
        {
            if (!string.IsNullOrWhiteSpace(track.FilePath) && !Path.IsPathRooted(track.FilePath))
            {
                track.FilePath = Path.GetFullPath(Path.Combine(projectDirectory, track.FilePath));
            }
        }

        document.SchemaVersion = VsmixerProjectDocument.CurrentSchemaVersion;
        return document;
    }

    public void Save(string filePath, VsmixerProjectDocument document)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Não foi possível determinar a pasta do projeto.");
        Directory.CreateDirectory(directory);

        var portableDocument = Clone(document);
        portableDocument.SchemaVersion = VsmixerProjectDocument.CurrentSchemaVersion;
        foreach (var track in portableDocument.Tracks)
        {
            if (string.IsNullOrWhiteSpace(track.FilePath))
            {
                continue;
            }

            track.FilePath = TryMakeRelative(directory, track.FilePath);
        }

        var json = JsonSerializer.Serialize(portableDocument, WriteOptions);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static VsmixerProjectDocument Clone(VsmixerProjectDocument document)
    {
        var json = JsonSerializer.Serialize(document);
        return JsonSerializer.Deserialize<VsmixerProjectDocument>(json, ReadOptions)
            ?? throw new InvalidOperationException("Não foi possível preparar o projeto para salvamento.");
    }

    private static string TryMakeRelative(string projectDirectory, string filePath)
    {
        try
        {
            var fullTrackPath = Path.GetFullPath(filePath);
            var relative = Path.GetRelativePath(projectDirectory, fullTrackPath);
            return Path.IsPathRooted(relative) ? fullTrackPath : relative;
        }
        catch (Exception) when (filePath.Length > 0)
        {
            return filePath;
        }
    }
}
