using CouchControl;
using Xunit;

namespace CouchControl.Tests;

public class WindowWatcherTests
{
    private static readonly string[] NoProcesses = [];

    [Theory]
    [InlineData("Playnite.FullscreenApp", true)]
    [InlineData("playnite.fullscreenapp", true)]
    [InlineData("PLAYNITE.FULLSCREENAPP", true)]
    [InlineData("Playnite", false)]           // exact name match only, no substrings
    [InlineData("Notepad", false)]
    [InlineData("", false)]
    public void IsMuteProcess_MatchesConfiguredProcessesCaseInsensitively(string name, bool expected)
    {
        Assert.Equal(expected,
            WindowWatcher.IsMuteProcess(name, ["Playnite.FullscreenApp"], muteOnWindowsUi: false));
    }

    [Theory]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("SearchHost")]
    [InlineData("SearchApp")]
    [InlineData("ShellExperienceHost")]
    [InlineData("explorer")]
    [InlineData("Explorer")]
    public void IsMuteProcess_WindowsUi_MutesOnlyWhenEnabled(string name)
    {
        Assert.True(WindowWatcher.IsMuteProcess(name, NoProcesses, muteOnWindowsUi: true));
        Assert.False(WindowWatcher.IsMuteProcess(name, NoProcesses, muteOnWindowsUi: false));
    }

    [Fact]
    public void IsMuteProcess_NothingConfigured_NeverMutes()
    {
        Assert.False(WindowWatcher.IsMuteProcess("explorer", NoProcesses, muteOnWindowsUi: false));
    }
}
