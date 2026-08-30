using System.Text.RegularExpressions;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraVisionWindowLayoutTests
{
    [Fact]
    public void CameraWindow_ContainsTwoVerticalPreviewRowsAndLinkedDropdowns()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "providers", "CameraVision", "Blaze.Provider.CameraVision", "UI",
            "CameraVisionSettingsWindow.xaml"));

        Assert.Contains("x:Name=\"RawCameraPreview\"", xaml);
        Assert.DoesNotContain("x:Name=\"CalibrationPreview\"", xaml);
        Assert.Contains("x:Name=\"CalibrationOverlay\"", xaml);
        Assert.Contains("x:Name=\"UnityPointPreview\"", xaml);
        Assert.Contains("Height=\"3*\"", xaml);
        Assert.Single(Regex.Matches(xaml, "Height=\\\"1\\*\\\"").Cast<Match>());
        Assert.Contains("x:Name=\"ResolutionCombo\"", xaml);
        Assert.Contains("x:Name=\"FrameRateCombo\"", xaml);
        Assert.DoesNotContain("Stretch=\"Fill\"", xaml);
        Assert.DoesNotContain("x:Name=\"OverlayCanvas\"", xaml);
        Assert.DoesNotContain("x:Name=\"PreviewImage\"", xaml);
    }

    [Fact]
    public void CameraWindow_UsesSharedDarkThemeAndExposesActualModeAndUiDiagnostics()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "providers", "CameraVision", "Blaze.Provider.CameraVision", "UI",
            "CameraVisionSettingsWindow.xaml"));

        Assert.Contains("InteractionConsoleTheme.xaml", xaml);
        Assert.Contains("{StaticResource CardStyle}", xaml);
        Assert.Contains("{StaticResource PrimaryButtonStyle}", xaml);
        Assert.Contains("ActualWidth", xaml);
        Assert.Contains("ActualHeight", xaml);
        Assert.Contains("UiRenderedFrames", xaml);
        Assert.Contains("UiSupersededFrames", xaml);
        Assert.Contains("CapabilityWarning", xaml);
        Assert.DoesNotContain("Background=\"#F5F7FA\"", xaml);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlazeInteraction.sln")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Unable to locate BlazeInteraction.sln.");
    }
}
