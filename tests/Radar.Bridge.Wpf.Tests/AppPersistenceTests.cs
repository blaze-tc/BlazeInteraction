using Yuexin.Radar.Bridge.Wpf;
using Yuexin.Radar.Configuration;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class AppPersistenceTests
{
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
