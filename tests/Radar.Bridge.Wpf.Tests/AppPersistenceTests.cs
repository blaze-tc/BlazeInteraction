using Yuexin.Radar.Bridge.Wpf;
using Yuexin.Radar.Configuration;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class AppPersistenceTests
{
    [Theory]
    [InlineData("123", 123)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("not-a-pid", null)]
    public void ParentPidArgument_OnlyPropagatesPositiveProcessIds(string value, int? expected)
    {
        Assert.Equal(expected, App.ReadExpectedParentProcessId(["--parent-pid", value]));
    }

    [Fact]
    public void ShouldAutoSaveConfiguration_RejectsUnsafeLoadedConfiguration()
    {
        var configuration = RadarConfigurationStore.LoadFromJson("{\"schemaVersion\":99}");

        Assert.False(App.ShouldAutoSaveConfiguration(configuration));
    }

    [Fact]
    public void ShouldAutoSaveConfiguration_AllowsNewAndSuccessfullyLoadedConfiguration()
    {
        Assert.True(App.ShouldAutoSaveConfiguration(RadarAppConfiguration.CreateDefault()));
    }
}
