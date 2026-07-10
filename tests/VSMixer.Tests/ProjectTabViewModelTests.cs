using VSMixer.Models;
using VSMixer.ViewModels;

namespace VSMixer.Tests;

public sealed class ProjectTabViewModelTests
{
    [Fact]
    public void DisplayName_IndicatesUnsavedChanges()
    {
        var tab = new ProjectTabViewModel("Culto", null, new VsmixerProjectDocument());

        Assert.Equal("Culto", tab.DisplayName);

        tab.IsDirty = true;

        Assert.Equal("● Culto", tab.DisplayName);
    }
}
