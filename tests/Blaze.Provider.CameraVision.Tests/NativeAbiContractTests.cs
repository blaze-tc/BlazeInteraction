namespace Blaze.Provider.CameraVision.Tests;

public sealed class NativeAbiContractTests
{
    [Fact]
    public void NativeHeader_DeclaresVersionedExceptionSafeAbi()
    {
        var headerPath = RepositoryPath(
            "native",
            "Blaze.HandTracking.Native",
            "blaze_hand_tracking.h");

        Assert.True(File.Exists(headerPath), $"Native ABI header was not found: {headerPath}");
        var header = File.ReadAllText(headerPath);

        Assert.Contains("BLAZE_HAND_ABI_VERSION 1", header, StringComparison.Ordinal);
        Assert.Contains("BLAZE_HAND_LANDMARK_COUNT 21", header, StringComparison.Ordinal);
        foreach (var export in new[]
                 {
                     "blaze_hand_get_abi_version",
                     "blaze_hand_create",
                     "blaze_hand_process_frame",
                     "blaze_hand_get_hand_count",
                     "blaze_hand_copy_hand",
                     "blaze_hand_destroy",
                     "blaze_hand_get_last_error"
                 })
        {
            Assert.Contains(export, header, StringComparison.Ordinal);
        }
    }

    private static string RepositoryPath(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BlazeInteraction.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine([current!.FullName, .. segments]);
    }
}
