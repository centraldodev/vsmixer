using VSMixer.Models;

namespace VSMixer.Services;

public interface IProjectFileService
{
    VsmixerProjectDocument Load(string filePath);
    void Save(string filePath, VsmixerProjectDocument document);
}
