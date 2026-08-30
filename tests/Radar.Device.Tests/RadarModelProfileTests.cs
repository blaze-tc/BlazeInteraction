using Yuexin.Radar.Contracts;
using Yuexin.Radar.Device;

namespace Yuexin.Radar.Device.Tests;

public sealed class RadarModelProfileTests
{
    [Fact]
    public void DefaultProfile_IsF10()
    {
        var profile = RadarModelProfileFactory.CreateDefault();

        Assert.Equal(RadarModel.F10, profile.Model);
        Assert.Equal(10f, profile.MaximumDistanceMeters);
        Assert.Equal(15, profile.DefaultScanFrequencyHz);
        Assert.Equal(10, profile.MinimumScanFrequencyHz);
        Assert.Equal(25, profile.MaximumScanFrequencyHz);
    }

    [Fact]
    public void F20Profile_UsesDocumentedLimits()
    {
        var profile = RadarModelProfileFactory.Create(RadarModel.F20);

        Assert.Equal("FaseLase F20", profile.DisplayName);
        Assert.Equal(0.05f, profile.MinimumDistanceMeters);
        Assert.Equal(40f, profile.MaximumDistanceMeters);
        Assert.Equal(25, profile.DefaultScanFrequencyHz);
        Assert.Equal(10, profile.MinimumScanFrequencyHz);
        Assert.Equal(30, profile.MaximumScanFrequencyHz);
        Assert.Equal(0.3f, profile.DefaultAngularResolutionDegrees);
        Assert.Equal(230f, profile.BlindZoneStartDegrees);
        Assert.Equal(310f, profile.BlindZoneEndDegrees);
    }

    [Fact]
    public void UnknownModel_FallsBackToF10()
    {
        var profile = RadarModelProfileFactory.Create((RadarModel)999);

        Assert.Equal(RadarModel.F10, profile.Model);
    }
}
